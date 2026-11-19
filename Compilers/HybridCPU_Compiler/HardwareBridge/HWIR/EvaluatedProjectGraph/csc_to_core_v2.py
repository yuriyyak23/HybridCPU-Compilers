"""HB-INT-02/03: bridge an observed, unqualified Csc closure to Core's v2 byte transport.

This is a pure offline *byte* converter, never an MSBuild/Csc/Golden executor, independent
source verifier, C# frontend or hardware authority. The caller MUST independently pin
the existing capture root and explicitly provide non-hardware root selectors. The
existing Core importer re-reads output bytes and still labels the result Candidate.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import sys
import tempfile
import time

import segmented_csc_capture_v2 as hb

SCHEMA = "circuitweave.bridge-source-closure-index/v2"
RECEIPT_SCHEMA = "hybridcpu.hardwarebridge.core-transport-receipt/v1"
MAX_ROWS = 8192
MAX_ROOTS = 32
MAX_INDEX = 1_048_576
MAX_CHUNK = 4_194_304
MAX_CHUNKS = 256
MAX_MANIFEST = 67_108_864
MAX_WORKSPACE = 536_870_912
MAX_SOURCE = 2_097_152
MAX_OTHER = 33_554_432
KINDS = frozenset(("csc-source", "generator-source", "reference", "analyzer",
                   "analyzer-config", "additional-file", "source-link", "app-config",
                   "csc-binary", "project", "assets"))
SOURCE_KINDS = frozenset(("csc-source", "generator-source"))
IDENTITY = re.compile(r"^[A-Za-z_][A-Za-z0-9_.+]*$")
HASH = re.compile(r"^[0-9a-f]{64}$")


def refuse(reason):
    raise ValueError("HB-INT-02 Core v2 transport Blocked: " + reason)


def raw_json(value):
    # Restricted ASCII strings avoid System.Text.Json's encoder-specific escaping.
    return json.dumps(value, separators=(",", ":"), ensure_ascii=True).encode("ascii")


def file_hash(path):
    p = hb.safe_path(str(path))
    result = hashlib.sha256()
    count = 0
    with p.open("rb") as stream:
        while block := stream.read(1024 * 1024):
            count += len(block)
            result.update(block)
    return result.hexdigest().upper(), count


def validate_roots(roots):
    if not isinstance(roots, list) or not 1 <= len(roots) <= MAX_ROOTS:
        refuse("explicit external roots must contain 1..32 entries")
    seen, names = set(), set()
    for root in roots:
        if not isinstance(root, dict) or list(root) != [
            "TypeName", "MethodName", "MetadataToken", "TypeArguments", "MethodArguments"
        ]:
            refuse("unsupported or reordered root selector fields")
        if not all(isinstance(root[x], str) and IDENTITY.fullmatch(root[x]) and
                   len(root[x]) <= 256 for x in ("TypeName", "MethodName")):
            refuse("unsafe/ambiguous method identity")
        token = root["MetadataToken"]
        if token is not None and (type(token) is not int or not 0 <= token <= 0x7FFFFFFF):
            refuse("invalid root metadata token")
        for name in ("TypeArguments", "MethodArguments"):
            values = root[name]
            if not isinstance(values, list) or len(values) > 8 or not all(
                isinstance(v, str) and IDENTITY.fullmatch(v) and len(v) <= 256 for v in values
            ):
                refuse("unsupported root generic argument")
        if token is None and (root["TypeArguments"] or root["MethodArguments"]):
            refuse("untokened generic root is ambiguous")
        key = raw_json(root)
        if key in seen:
            refuse("duplicate root selector")
        seen.add(key)
        sig = (root["TypeName"], root["MethodName"])
        if sig in names and token is None:
            refuse("ambiguous un-tokened overload")
        names.add(sig)
    for root in roots:
        if root["MetadataToken"] is None and sum(
            x["TypeName"] == root["TypeName"] and x["MethodName"] == root["MethodName"]
            for x in roots
        ) != 1:
            refuse("root with ambiguous overload")


def capture_index(capture_root, externally_expected_root):
    if not isinstance(externally_expected_root, str) or not HASH.fullmatch(externally_expected_root):
        refuse("explicitly pinned lowercase HB capture closureRootSha256 required")
    capture = hb.safe_path(os.path.abspath(capture_root), file=False)
    observed_root = hb.verify(capture)
    if observed_root != externally_expected_root:
        refuse("stale external HB capture root SHA")
    source_file = hb.safe_path(str(capture / "index.json"))
    index = hb.load(str(source_file), hb.MAX_INDEX)
    if set(index) != {"schema", "disposition", "graphSha256", "observationSha256",
                      "chunkSizeLimit", "commands", "entries", "closureRootSha256"}:
        refuse("unknown HB-INT-02 capture index fields")
    return capture, index, file_hash(source_file)[0]


def build_plan(capture_root, expected_root, roots):
    validate_roots(roots)
    capture, original, index_sha = capture_index(capture_root, expected_root)
    rows = original["entries"]
    if not isinstance(rows, list) or not 1 <= len(rows) <= MAX_ROWS:
        refuse("HB closure too large for Core 8192-row contract")
    result = []
    source_count = 0
    aggregate = 0
    for order, entry in enumerate(rows):
        if not isinstance(entry, dict) or set(entry) != {
            "nodeId", "kind", "role", "logicalPath", "path", "sha256", "bytes", "chunks"
        }:
            refuse("unknown Csc capture row fields")
        kind, asserted = entry["kind"], entry["role"]
        if kind not in KINDS:
            refuse("unrecognized Csc captured input kind")
        if kind not in SOURCE_KINDS and asserted != "metadata-only":
            refuse("metadata-only compiler input promoted to source")
        if kind in SOURCE_KINDS:
            role = {"primary": "source", "owned-body": "owned-body",
                    "metadata-only": "metadata-only"}.get(asserted)
            if role is None:
                refuse("unknown source ownership role")
        else:
            role = "metadata-only"
        length = entry["bytes"]
        limit = MAX_SOURCE if kind in SOURCE_KINDS else MAX_OTHER
        if type(length) is not int or length > limit or length < (0 if kind in SOURCE_KINDS else 1):
            refuse("file exceeds Core role budget; no truncation is permitted")
        aggregate += length
        if aggregate > MAX_WORKSPACE:
            refuse("Core 512 MiB workspace byte budget exceeded")
        if role == "source":
            source_count += 1
        # No arbitrary path components from untrusted compile inputs are used for
        # materialization. The unqualified receipt retains node/logical provenance.
        short = hashlib.sha256(hb.canonical([
            entry["nodeId"], entry["kind"], entry["logicalPath"]
        ])).hexdigest()[:16]
        rel = "inputs/%05d-%s%s" % (order, short, ".cs" if kind in SOURCE_KINDS else ".bin")
        result.append({"Path": rel, "Role": role, "Sha256": entry["sha256"].upper(),
                       "Bytes": length, "Original": entry})
    if source_count < 1:
        refuse("no primary source; other projects cannot be promoted")
    # Full path ordering is independent of the HB index's (node, kind, logicalPath) ordering.
    result.sort(key=lambda x: x["Path"])
    chunks, current = [], []
    manifest_size = 0
    def flush():
        nonlocal manifest_size
        if not current:
            return
        if len(chunks) >= MAX_CHUNKS:
            refuse("Core chunk-count budget exceeded")
        name = "bridge-index/chunk-%04d.jsonl" % len(chunks)
        payload = b"".join(current)
        manifest_size += len(payload)
        if len(payload) > MAX_CHUNK or manifest_size > MAX_MANIFEST:
            refuse("Core byte manifest budget exceeded")
        chunks.append((name, payload, len(current)))
        current.clear()

    for row in result:
        wire_row = {field: row[field] for field in ("Path", "Role", "Sha256", "Bytes")}
        line = raw_json(wire_row) + b"\n"
        if len(line) > MAX_CHUNK:
            refuse("oversized JSONL row")
        if len(current) == 64 or sum(len(x) for x in current) + len(line) > MAX_CHUNK:
            flush()
        current.append(line)
    flush()
    digest = hashlib.sha256(b"".join(payload for _, payload, _ in chunks)).hexdigest().upper()
    descriptors = [{"Path": name, "Sha256": hashlib.sha256(data).hexdigest().upper(),
                    "Bytes": len(data), "Rows": count} for name, data, count in chunks]
    index_bytes = raw_json({"Schema": SCHEMA, "Roots": roots,
                            "Chunks": descriptors, "ClosureSha256": digest})
    if len(index_bytes) > MAX_INDEX:
        refuse("Core index byte budget exceeded")
    receipt = {
        "Schema": RECEIPT_SCHEMA, "Disposition": "unqualified-software-byte-transport",
        "HbCaptureRootSha256": expected_root,
        "HbCaptureIndexSha256": index_sha,
        "CoreIndexSha256": hashlib.sha256(index_bytes).hexdigest().upper(),
        "CoreClosureSha256": digest,
        "Roots": roots,
        "Inputs": [{
            "Path": row["Path"], "Role": row["Role"],
            "Sha256": row["Sha256"], "Bytes": row["Bytes"],
            "HbNodeId": row["Original"]["nodeId"],
            "HbKind": row["Original"]["kind"],
            "HbLogicalPath": row["Original"]["logicalPath"]
        } for row in result]
    }
    return capture, result, index_bytes, chunks, raw_json(receipt)


def output_root(path):
    full = Path(os.path.abspath(path))
    temp = Path(os.path.abspath(tempfile.gettempdir()))
    if full.parent != temp or full.exists() or full.is_symlink():
        refuse("output must be a NEW direct OS TEMP child")
    hb.safe_path(str(temp), file=False)
    return full


def source_part(capture, part):
    name = part["name"]
    if not isinstance(name, str) or not re.fullmatch(r"chunk-[0-9]{8}\.bin", name):
        refuse("noncanonical HB captured chunk path")
    path = hb.safe_path(str(capture / name))
    # O_NOFOLLOW on supporting hosts plus input rehash before and after export.
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    fd = os.open(path, flags)
    try:
        if not stat.S_ISREG(os.fstat(fd).st_mode):
            refuse("nonregular input chunk")
        if type(part["bytes"]) is not int or not 0 < part["bytes"] <= hb.CHUNK_SIZE:
            refuse("invalid captured chunk size")
        data = os.read(fd, part["bytes"] + 1)
        if len(data) != part["bytes"] or hb.sha(data) != part["sha256"]:
            refuse("changed or truncated HB captured chunk")
        return data
    finally:
        os.close(fd)


def validate_emitted(capture_root, expected_root, roots, destination):
    capture, rows, index_bytes, chunks, receipt = build_plan(
        capture_root, expected_root, roots
    )
    root = hb.safe_path(os.path.abspath(destination), file=False)
    expected_files = {"bridge-index.json", "bridge-export-receipt.json"}
    expected_files.update(row["Path"] for row in rows)
    expected_files.update(name for name, _, _ in chunks)
    actual_files = set()
    for p in root.rglob("*"):
        hb.safe_path(str(p), file=not p.is_dir())
        if p.is_file():
            actual_files.add(p.relative_to(root).as_posix())
    if actual_files != expected_files:
        refuse("missing/extra staged Core file")
    for name, expected in (("bridge-index.json", index_bytes),
                           ("bridge-export-receipt.json", receipt),
                           *((name, payload) for name, payload, _ in chunks)):
        if hb.safe_path(str(root / name)).read_bytes() != expected:
            refuse("changed Core index, manifest, receipt or chunk")
    for row in rows:
        if file_hash(root / row["Path"]) != (row["Sha256"], row["Bytes"]):
            refuse("stale/mutated Core staged file")
        if row["Original"]["kind"] in SOURCE_KINDS:
            try:
                (root / row["Path"]).read_bytes().decode("utf-8", errors="strict")
            except UnicodeError as ex:
                refuse("Core source requires strict UTF-8 bytes: " + str(ex))
    if hb.verify(capture) != expected_root:
        refuse("input capture changed during Core transport validation")
    return json.loads(receipt)["CoreClosureSha256"]


def export(capture_root, expected_root, roots, out_dir, *, timeout_seconds=300, after_copy=None):
    if type(timeout_seconds) not in (int, float) or not 0 < timeout_seconds <= 3600:
        refuse("invalid bounded timeout")
    deadline = time.monotonic() + timeout_seconds
    def budget():
        if time.monotonic() >= deadline:
            refuse("export timed out")
    output = output_root(out_dir)
    capture, rows, index_bytes, chunks, receipt = build_plan(capture_root, expected_root, roots)
    budget()
    output.mkdir(mode=0o700)
    try:
        (output / "inputs").mkdir()
        (output / "bridge-index").mkdir()
        for row in rows:
            budget()
            source = row["Original"]
            h = hashlib.sha256()
            total = 0
            dest = output / row["Path"]
            with dest.open("xb") as stream:
                for part in source["chunks"]:
                    budget()
                    data = source_part(capture, part)
                    stream.write(data)
                    h.update(data)
                    total += len(data)
            if total != row["Bytes"] or h.hexdigest().upper() != row["Sha256"]:
                refuse("source chunk reconstruction mismatch")
            if source["kind"] in SOURCE_KINDS:
                try:
                    dest.read_bytes().decode("utf-8", errors="strict")
                except UnicodeError:
                    refuse("Core only admits strict UTF-8 source")
        for name, data, _ in chunks:
            budget()
            with (output / name).open("xb") as stream:
                stream.write(data)
        for name, data in (("bridge-index.json", index_bytes),
                           ("bridge-export-receipt.json", receipt)):
            budget()
            with (output / name).open("xb") as stream:
                stream.write(data)
        if after_copy is not None:  # test-only mutation hook; not CLI reachable
            after_copy()
        budget()
        return validate_emitted(capture, expected_root, roots, output)
    except BaseException:
        shutil.rmtree(output)
        raise


def read_roots(path):
    p = hb.safe_path(os.path.abspath(path))
    if p.stat().st_size > 65536:
        refuse("oversized root selector manifest")
    roots = json.loads(p.read_bytes(), object_pairs_hook=hb.pairs_unique)
    validate_roots(roots)
    return roots


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("export", "verify"):
        action = sub.add_parser(name)
        action.add_argument("--capture", required=True, help="existing HB-INT-02 output")
        action.add_argument("--expected-capture-sha", required=True,
                            help="externally expected lowercase HB closureRootSha256")
        action.add_argument("--roots", required=True, help="explicit root selector JSON array")
        action.add_argument("--output", required=True, help="new direct TEMP child for export")
        if name == "export":
            action.add_argument("--timeout-seconds", type=float, default=300)
    args = parser.parse_args()
    roots = read_roots(args.roots)
    if args.command == "export":
        digest = export(args.capture, args.expected_capture_sha, roots, args.output,
                        timeout_seconds=args.timeout_seconds)
    else:
        digest = validate_emitted(args.capture, args.expected_capture_sha, roots, args.output)
    print("HB-INT-02 Core v2 unqualified byte transport ClosureSha256=" + digest)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError, TypeError, UnicodeError, json.JSONDecodeError) as ex:
        print("HB-INT-02 Core transport Blocked: " + str(ex), file=sys.stderr)
        sys.exit(2)
