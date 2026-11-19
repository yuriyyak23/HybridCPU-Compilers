"""HB-INT-02/HB-INT-03: project a captured (unqualified) Csc byte inventory
into the EXISTING CW-INT-03 v2 index wire contract.

No compiler/targets/Golden code execution. This is a producer/stager only.
The Core owner MUST independently pin ExpectedLiveClosureSha256 and rehash
the workspace before considering a candidate source inventory.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile

import segmented_csc_capture_v2 as hb02

CORE_SCHEMA = "circuitweave.bridge-source-closure-index/v2"
LINK_SCHEMA = "hybridcpu.hardwarebridge.csc-core-transport-link/v1"
MAX_ROWS = 8192
MAX_TOTAL = 536_870_912
MAX_INDEX = 1_048_576
MAX_CHUNK = 4_194_304
MAX_CHUNKS = 256
MAX_MANIFEST = 67_108_864
MAX_SOURCE = 2_097_152
MAX_OTHER = 33_554_432
SOURCE_KINDS = {"csc-source", "generator-source"}
METADATA_KINDS = {"reference", "analyzer", "analyzer-config", "additional-file",
                  "source-link", "app-config", "csc-binary", "project", "assets"}


def reject(reason):
    raise ValueError(reason)


def encode(obj):
    # Safe ASCII paths/identities only. Matches .NET compact System.Text.Json for
    # this subset (no non-ASCII/HTML-sensitive strings or noncanonical numbers).
    return json.dumps(obj, ensure_ascii=True, separators=(",", ":")).encode("utf-8")


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def strict_keys(obj, keys):
    if not isinstance(obj, dict) or list(obj) != list(keys):
        reject("unknown/missing object properties")


def safe_root_text(s):
    if not isinstance(s, str) or not s or len(s) > 256 or any(
        c not in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._" for c in s
    ):
        reject("root identity outside Core-compatible conservative ASCII subset")
    return s


def roots_from_file(path):
    roots = hb02.load(str(path), 1_048_576)
    if not isinstance(roots, list) or not 1 <= len(roots) <= 32:
        reject("expected 1..32 explicitly selected roots")
    seen = set()
    methods = set()
    for root in roots:
        strict_keys(root, ("TypeName", "MethodName", "MetadataToken", "TypeArguments", "MethodArguments"))
        a = safe_root_text(root["TypeName"])
        b = safe_root_text(root["MethodName"])
        token = root["MetadataToken"]
        if token is not None and (type(token) is not int or token < 0 or token > 2147483647):
            reject("invalid exact root token")
        for field in ("TypeArguments", "MethodArguments"):
            args = root[field]
            if not isinstance(args, list) or len(args) > 8:
                reject("invalid generic root arguments")
            for arg in args:
                safe_root_text(arg)
        if token is None and (root["TypeArguments"] or root["MethodArguments"]):
            reject("unqualified/untokened generic root")
        key = encode(root)
        if key in seen or (token is None and (a, b) in methods):
            reject("duplicate/ambiguous root selector")
        seen.add(key)
        methods.add((a, b))
    for root in roots:
        if root["MetadataToken"] is None and sum(r["TypeName"] == root["TypeName"] and
                                                  r["MethodName"] == root["MethodName"] for r in roots) != 1:
            reject("ambiguous untokened overload")
    return roots


def exact_index(capture_dir):
    # Producer's own integrity re-read is not an independent Core verifier.
    expected = hb02.verify(capture_dir)
    data = hb02.load(str(Path(capture_dir) / "index.json"), hb02.MAX_INDEX)
    if set(data) != {"schema", "disposition", "graphSha256", "observationSha256",
                     "chunkSizeLimit", "commands", "entries", "closureRootSha256"} or (
                     data["closureRootSha256"] != expected):
        reject("unsupported HB-INT-02 index shape or root")
    rows = data["entries"]
    if len(rows) > MAX_ROWS:
        reject("Core v2 bounded importer limit exceeded")
    allowed = {"nodeId", "kind", "role", "logicalPath", "path", "sha256", "bytes", "chunks"}
    total = 0
    for row in rows:
        strict_keys(row, allowed)
        if row["kind"] in SOURCE_KINDS:
            if row["role"] not in ("primary", "owned-body", "metadata-only"):
                reject("unknown source owner role")
            try:
                # Core OwnedFileReader requires strict UTF-8 source inputs.
                decoder = __import__("codecs").getincrementaldecoder("utf-8")("strict")
                for part in row["chunks"]:
                    decoder.decode((Path(capture_dir) / part["name"]).read_bytes())
                decoder.decode(b"", final=True)
            except UnicodeDecodeError as error:
                reject("Core v2 source is not strict UTF-8: " + str(error))
            if row["bytes"] > MAX_SOURCE:
                reject("Core source per-file budget exceeded")
        elif row["kind"] in METADATA_KINDS:
            if row["role"] != "metadata-only" or row["bytes"] == 0 or row["bytes"] > MAX_OTHER:
                reject("metadata cannot be promoted to executable body")
        else:
            reject("unknown compiler input kind")
        total += row["bytes"]
        if total > MAX_TOTAL:
            reject("Core 512MiB workspace byte budget exceeded")
    return data


def project(capture_dir, roots_path, output_dir, after_stage=None):
    capture_dir = str(hb02.safe_path(os.path.abspath(capture_dir), file=False))
    parent = Path(os.path.abspath(tempfile.gettempdir()))
    output = Path(os.path.abspath(output_dir))
    if output.parent != parent or output.exists() or output.is_symlink():
        reject("output must be a NEW direct child of TEMP")
    hb02.safe_path(str(parent), file=False)
    src = exact_index(capture_dir)
    roots = roots_from_file(roots_path)
    output.mkdir(mode=0o700)
    try:
        (output / "src").mkdir()
        (output / "metadata").mkdir()
        (output / "bridge-index").mkdir()
        manifest_rows = []
        links = []
        for number, row in enumerate(src["entries"]):
            is_source = row["kind"] in SOURCE_KINDS
            dest = ("src/" if is_source else "metadata/") + f"{number:05d}" + (
                ".cs" if is_source else ".bin")
            path = output / dest
            hasher = hashlib.sha256()
            length = 0
            with path.open("xb") as sink:
                for part in row["chunks"]:
                    chunk_path = hb02.safe_path(str(Path(capture_dir) / part["name"]))
                    with chunk_path.open("rb") as stream:
                        # Bounded block by HB-INT-02 chunk ceilings, rehashed here.
                        chunk = stream.read(hb02.CHUNK_SIZE + 1)
                        if len(chunk) != part["bytes"] or hb02.sha(chunk) != part["sha256"]:
                            reject("HB02 chunk replaced while staging")
                        sink.write(chunk)
                        hasher.update(chunk)
                        length += len(chunk)
            if length != row["bytes"] or hasher.hexdigest() != row["sha256"]:
                reject("HB02 file replaced while staging")
            manifest_rows.append({"Path": dest, "Role": "source" if is_source else "metadata-only",
                                  "Sha256": row["sha256"].upper(), "Bytes": length})
            links.append({"Path": dest, "NodeId": row["nodeId"], "Kind": row["kind"],
                          "ClaimedProjectRole": row["role"], "ObservedLogicalPath": row["logicalPath"],
                          "Sha256": row["sha256"].upper(), "Bytes": length})
        if after_stage is not None:  # regression-only mutation injection
            after_stage(output)
        # Rehash captured output; no "same HEAD means same bytes" shortcut.
        for row in manifest_rows:
            current = output / row["Path"]
            h, size = hb02.hash_file(current)
            if h.upper() != row["Sha256"] or size != row["Bytes"]:
                reject("staged output mutated during projection")
        if hb02.verify(capture_dir) != src["closureRootSha256"]:
            reject("HB02 input changed during projection")
        manifest_rows.sort(key=lambda r: r["Path"])
        chunks = []
        hclosure = hashlib.sha256()
        chunk_payload = bytearray()
        chunk_rows = 0
        manifest_bytes = 0
        def flush():
            nonlocal chunk_payload, chunk_rows, manifest_bytes
            if not chunk_rows:
                return
            if len(chunks) >= MAX_CHUNKS:
                reject("Core chunk-count limit exceeded")
            name = f"bridge-index/chunk-{len(chunks):04d}.jsonl"
            raw = bytes(chunk_payload)
            with (output / name).open("xb") as f:
                f.write(raw)
            hclosure.update(raw)
            chunks.append({"Path": name, "Sha256": sha(raw),
                           "Bytes": len(raw), "Rows": chunk_rows})
            manifest_bytes += len(raw)
            if manifest_bytes > MAX_MANIFEST:
                reject("Core cumulative chunk manifest limit exceeded")
            chunk_payload = bytearray()
            chunk_rows = 0
        for row in manifest_rows:
            line = encode(row) + b"\n"
            if len(line) > MAX_CHUNK:
                reject("oversized Core row")
            if chunk_rows == 64 or len(chunk_payload) + len(line) > MAX_CHUNK:
                flush()
            chunk_payload.extend(line)
            chunk_rows += 1
        flush()
        index = {"Schema": CORE_SCHEMA, "Roots": roots, "Chunks": chunks,
                 "ClosureSha256": hclosure.hexdigest().upper()}
        index_bytes = encode(index)
        if len(index_bytes) > MAX_INDEX:
            reject("Core v2 index byte budget exceeded")
        with (output / "index.json").open("xb") as f:
            f.write(index_bytes)
        link = {"Schema": LINK_SCHEMA, "Disposition": "unqualified-software-observation",
                "CaptureClosureSha256": src["closureRootSha256"].upper(),
                "CoreIndexSha256": sha(index_bytes),
                "CoreClosureSha256": index["ClosureSha256"],
                "Inputs": sorted(links, key=lambda row: row["Path"])}
        with (output / "capture-crosswalk.json").open("xb") as f:
            f.write(encode(link))
        # This method checks only producer-side integrity. The Core importer
        # must independently derive ExpectedLiveClosureSha256 and read bytes.
        if verify_projection(output, capture_dir) != index["ClosureSha256"]:
            reject("projected current bytes do not match emitted manifest")
        return index["ClosureSha256"]
    except BaseException:
        shutil.rmtree(output)
        raise


def verify_projection(output_dir, capture_dir=None):
    root = hb02.safe_path(os.path.abspath(output_dir), file=False)
    index = hb02.load(str(root / "index.json"), MAX_INDEX)
    strict_keys(index, ("Schema", "Roots", "Chunks", "ClosureSha256"))
    if index["Schema"] != CORE_SCHEMA or not 1 <= len(index["Chunks"]) <= MAX_CHUNKS:
        reject("invalid Core v2 index")
    link = hb02.load(str(root / "capture-crosswalk.json"), 8 * 1024 * 1024)
    strict_keys(link, ("Schema", "Disposition", "CaptureClosureSha256",
                       "CoreIndexSha256", "CoreClosureSha256", "Inputs"))
    if link["Schema"] != LINK_SCHEMA or link["Disposition"] != "unqualified-software-observation":
        reject("invalid projection crosswalk")
    if sha((root / "index.json").read_bytes()) != link["CoreIndexSha256"]:
        reject("modified Core v2 index")
    if index["ClosureSha256"] != link["CoreClosureSha256"]:
        reject("stale crosswalk root")
    h = hashlib.sha256()
    source_count = 0
    entries = []
    for i, part in enumerate(index["Chunks"]):
        expected_path = f"bridge-index/chunk-{i:04d}.jsonl"
        if part["Path"] != expected_path or part["Bytes"] > MAX_CHUNK:
            reject("noncanonical Core chunk")
        raw = hb02.safe_path(str(root / expected_path)).read_bytes()
        if len(raw) != part["Bytes"] or sha(raw) != part["Sha256"]:
            reject("modified Core chunk")
        h.update(raw)
        lines = raw.splitlines(keepends=True)
        if len(lines) != part["Rows"] or any(not l.endswith(b"\n") or l == b"\n" for l in lines):
            reject("invalid Core chunk row count")
        for line in lines:
            entry = json.loads(line[:-1], object_pairs_hook=hb02.pairs_unique)
            strict_keys(entry, ("Path", "Role", "Sha256", "Bytes"))
            if encode(entry) + b"\n" != line:
                reject("noncanonical Core row")
            if entry["Role"] not in ("source", "metadata-only"):
                reject("unqualified role promoted")
            if entry["Role"] == "source":
                source_count += 1
            path = entry["Path"]
            if not isinstance(path, str) or not (path.startswith("src/") or path.startswith("metadata/")):
                reject("unexpected staged file path")
            actual = root / path
            hfile, length = hb02.hash_file(actual)
            if length != entry["Bytes"] or hfile.upper() != entry["Sha256"]:
                reject("staged file changed")
            entries.append(entry)
    if not source_count or len(entries) > MAX_ROWS or \
       [row["Path"] for row in entries] != sorted(set(row["Path"] for row in entries)):
        reject("unordered/duplicate/missing source entries")
    if h.hexdigest().upper() != index["ClosureSha256"]:
        reject("changed Core closure root")
    if len(entries) != len(link["Inputs"]) or [x["Path"] for x in entries] != [x["Path"] for x in link["Inputs"]]:
        reject("crosswalk missing/reordered identities")
    for entry, mapping in zip(entries, link["Inputs"]):
        strict_keys(mapping, ("Path", "NodeId", "Kind", "ClaimedProjectRole",
                              "ObservedLogicalPath", "Sha256", "Bytes"))
        if (entry["Sha256"], entry["Bytes"]) != (mapping["Sha256"], mapping["Bytes"]):
            reject("crosswalk changed file identity")
        if (entry["Role"] == "metadata-only" and mapping["Kind"] in SOURCE_KINDS or
            entry["Role"] == "source" and mapping["Kind"] not in SOURCE_KINDS or
            mapping["Kind"] in METADATA_KINDS and mapping["ClaimedProjectRole"] != "metadata-only"):
            reject("crosswalk promoted metadata to executable body")
    if capture_dir is not None:
        original = exact_index(capture_dir)
        if original["closureRootSha256"].upper() != link["CaptureClosureSha256"]:
            reject("original HB02 capture stale")
        if len(original["entries"]) != len(entries):
            reject("crosswalk no longer covers original capture")
        expected = sorted(
            [{"NodeId": r["nodeId"], "Kind": r["kind"], "ClaimedProjectRole": r["role"],
              "ObservedLogicalPath": r["logicalPath"], "Sha256": r["sha256"].upper(), "Bytes": r["bytes"]}
             for r in original["entries"]],
            key=lambda row: (row["NodeId"], row["Kind"], row["ObservedLogicalPath"]))
        observed = sorted(
            [{k: m[k] for k in ("NodeId", "Kind", "ClaimedProjectRole",
                                "ObservedLogicalPath", "Sha256", "Bytes")}
             for m in link["Inputs"]],
            key=lambda row: (row["NodeId"], row["Kind"], row["ObservedLogicalPath"]))
        if observed != expected:
            reject("crosswalk differs from original captured identities")
    return index["ClosureSha256"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    p = sub.add_parser("project")
    p.add_argument("--capture", required=True)
    p.add_argument("--roots", required=True)
    p.add_argument("--output", required=True)
    v = sub.add_parser("verify")
    v.add_argument("--workspace", required=True)
    v.add_argument("--capture")
    args = parser.parse_args()
    if args.action == "project":
        result = project(args.capture, args.roots, args.output)
    else:
        result = verify_projection(args.workspace, args.capture)
    print("HB-INT-02/03 unqualified Core v2 closure: " + result)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, TypeError, OSError, UnicodeDecodeError) as ex:
        print("HB-INT-02/03 Blocked: " + str(ex), file=sys.stderr)
        sys.exit(2)
