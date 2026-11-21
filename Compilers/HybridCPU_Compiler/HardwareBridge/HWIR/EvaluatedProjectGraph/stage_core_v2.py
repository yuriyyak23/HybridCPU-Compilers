"""Release HB-INT-02 -> Core v2 data-only staging with independently reviewed
HB-INT-02 closure and root-manifest SHA pins. The reference wire writer is
SourceSnapshot/BridgeSegmentIndexV2.cs; Python must remain byte-identical.
Neither source claims nor file hashes qualify executable ownership.
NEVER executes Csc, changes Golden, or verifies hardware.
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
import segmented_csc_capture_v2 as capture

CORE = "circuitweave.bridge-source-closure-index/v2"
ROOTS = "hybridcpu.hardwarebridge.root-selectors/v1"
MAPPING = "hybridcpu.hardwarebridge.csc-to-core-map/v1"
MAX_ROWS, MAX_SIZE = 8192, 512 * 1024 * 1024

def refuse(message):
    raise ValueError("HBINT0203 " + message)

def json_bytes(value):
    return json.dumps(value, separators=(",", ":"), ensure_ascii=True).encode("ascii")

def sha(data):
    return hashlib.sha256(data).hexdigest().upper()

def load(path, ceiling):
    path = capture.safe_path(str(path))
    if path.stat().st_size > ceiling:
        refuse("oversized document")
    return json.loads(path.read_bytes(), object_pairs_hook=capture.pairs_unique)

def check_roots(path, reviewed_sha):
    f = capture.safe_path(str(path))
    if f.stat().st_size > 65536:
        refuse("oversized root manifest")
    raw = f.read_bytes()
    if not re.fullmatch(r"[0-9a-f]{64}", reviewed_sha) or capture.sha(raw) != reviewed_sha:
        refuse("wrong/missing independently reviewed root manifest SHA")
    # Parse the *same pinned raw bytes*. Loading the path again previously allowed
    # a concurrent edit to swap the selectors between hashing and parsing.
    if not raw or len(raw) > 65536:
        refuse("oversized/empty root manifest")
    roots = json.loads(raw, object_pairs_hook=capture.pairs_unique)
    if not isinstance(roots, dict) or set(roots) != {"Schema", "Roots"} or roots["Schema"] != ROOTS:
        refuse("unknown root contract")
    selectors = roots["Roots"]
    if not isinstance(selectors, list) or not 1 <= len(selectors) <= 32:
        refuse("missing/oversized roots")
    seen = set()
    for selector in selectors:
        if not isinstance(selector, dict) or set(selector) != {
            "TypeName", "MethodName", "MetadataToken", "TypeArguments", "MethodArguments"
        }:
            refuse("invalid root selector fields")
        for field in ("TypeName", "MethodName"):
            value = selector[field]
            if not isinstance(value, str) or len(value) > 256 or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.$+]*", value):
                refuse("unsupported root identity")
        token = selector["MetadataToken"]
        if token is not None and (type(token) is not int or token < 0 or token > 2147483647):
            refuse("invalid root token")
        for field in ("TypeArguments", "MethodArguments"):
            values = selector[field]
            if not isinstance(values, list) or len(values) > 8 or any(
                not isinstance(v, str) or len(v) > 256 or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.$+]*", v)
                for v in values
            ):
                refuse("unsupported type arguments")
        if token is None and (selector["TypeArguments"] or selector["MethodArguments"]):
            refuse("generic root requires token")
        identity = (selector["TypeName"], selector["MethodName"], token,
                    tuple(selector["TypeArguments"]), tuple(selector["MethodArguments"]))
        if identity in seen:
            refuse("duplicate root")
        seen.add(identity)
    if any(r["MetadataToken"] is None and sum(
        (r["TypeName"], r["MethodName"]) == (x["TypeName"], x["MethodName"])
        for x in selectors) > 1 for r in selectors):
        refuse("ambiguous untokened overload")
    # Match the ordered C# BridgeRootSelectorV1 record serialization, regardless
    # of the JSON property order in the externally reviewed manifest.
    normalized = [{"TypeName": x["TypeName"], "MethodName": x["MethodName"],
                   "MetadataToken": x["MetadataToken"],
                   "TypeArguments": x["TypeArguments"],
                   "MethodArguments": x["MethodArguments"]} for x in selectors]
    return normalized, raw

PAIRED = "hybridcpu.hardwarebridge.paired-csc-capture-receipt/v1"

def require_execution_receipt(path, reviewed_sha, captured_root):
    """Bounded negative gate: current R1-02 receipts explicitly lack Csc attestation.

    This never interprets hash equality or zero Csc exit as executable provenance.
    A future qualified receipt needs a *new*, independently approved versioned
    contract and a Core-owned acceptance path, not a flag on the existing v1.
    """
    if not isinstance(reviewed_sha, str) or not re.fullmatch(r"[0-9a-f]{64}", reviewed_sha) or not path:
        refuse("R2_01_EXECUTION_PIN_MISSING")
    receipt_path = capture.safe_path(os.path.abspath(path))
    if receipt_path.stat().st_size > 131072:
        refuse("R2_01_EXECUTION_RECEIPT_SIZE")
    raw = receipt_path.read_bytes()
    if not raw or len(raw) > 131072 or capture.sha(raw) != reviewed_sha:
        refuse("R2_01_EXECUTION_RECEIPT_SHA")
    receipt = json.loads(raw, object_pairs_hook=capture.pairs_unique)
    if not isinstance(receipt, dict) or set(receipt) != {
        "schema", "disposition", "executionGate", "clean", "dirty"
    } or receipt["schema"] != PAIRED or receipt["disposition"] != "unqualified-software-observation":
        refuse("R2_01_EXECUTION_RECEIPT_SCHEMA")
    closures = []
    for label in ("clean", "dirty"):
        item = receipt[label]
        if not isinstance(item, dict) or not isinstance(item.get("closureRootSha256"), str):
            refuse("R2_01_EXECUTION_CLOSURE_MISSING")
        value = item["closureRootSha256"]
        if not re.fullmatch(r"[0-9a-f]{64}", value):
            refuse("R2_01_EXECUTION_CLOSURE_INVALID")
        closures.append(value)
    if captured_root not in closures:
        refuse("R2_01_EXECUTION_CAPTURE_MISMATCH")
    # R1-02 v1 always records an unqualified/Blocked executionGate.
    # Neither a rewritten gate field nor a freshly recomputed SHA grants proof.
    refuse("R2_01_EXECUTION_BLOCKED: no independently approved task-time Csc + "
           "response-file read witness for this versioned source closure")

def role(row):
    kind, ownership = row["kind"], row["role"]
    if kind not in ("csc-source", "generator-source", "reference", "analyzer",
                    "analyzer-config", "additional-file", "source-link", "app-config",
                    "csc-binary", "project", "assets"):
        refuse("unknown captured Csc kind")
    if kind not in ("csc-source", "generator-source"):
        if ownership != "metadata-only":
            refuse("metadata promoted to body")
        return "metadata-only"
    if ownership not in ("primary", "owned-body", "metadata-only"):
        refuse("unknown ownership")
    return {"primary": "source", "owned-body": "owned-body",
            "metadata-only": "metadata-only"}[ownership]

def verify(output, reviewed_capture, reviewed_roots, capture_bundle=None):
    if (not isinstance(reviewed_capture, str) or not re.fullmatch(r"[0-9a-f]{64}", reviewed_capture)
        or not isinstance(reviewed_roots, str) or not re.fullmatch(r"[0-9a-f]{64}", reviewed_roots)):
        refuse("missing reviewed hash inputs")
    base = capture.safe_path(os.path.abspath(output), file=False)
    roots, _ = check_roots(base / "roots-manifest.json", reviewed_roots)
    index_file = capture.safe_path(str(base / "bridge-index.json"))
    if index_file.stat().st_size > 1048576:
        refuse("oversized Core index")
    raw = index_file.read_bytes()
    index = json.loads(raw, object_pairs_hook=capture.pairs_unique)
    if not isinstance(index, dict) or set(index) != {"Schema", "Roots", "Chunks", "ClosureSha256"} or (
        index["Schema"] != CORE or index["Roots"] != roots or json_bytes(index) != raw
    ):
        refuse("mutated/noncanonical Core index")
    # Core's Shape() requires exact JSON property order, not merely a canonical
    # serialization of whichever insertion order an attacker supplied.
    if list(index) != ["Schema", "Roots", "Chunks", "ClosureSha256"] or any(
        not isinstance(r, dict) or list(r) != [
            "TypeName", "MethodName", "MetadataToken", "TypeArguments", "MethodArguments"
        ] for r in index["Roots"]
    ):
        refuse("R2_01_INDEX_PROPERTY_ORDER")
    mapping_file = capture.safe_path(str(base / "capture-map.json"))
    if mapping_file.stat().st_size > 4 * 1048576:
        refuse("oversized source provenance mapping")
    mapping_raw = mapping_file.read_bytes()
    mapping = json.loads(mapping_raw, object_pairs_hook=capture.pairs_unique)
    if json_bytes(mapping) != mapping_raw:
        refuse("noncanonical source provenance mapping")
    if not isinstance(mapping, dict) or set(mapping) != {"Schema", "Disposition",
        "Hb02ClosureRootSha256", "RootManifestSha256", "CoreClosureSha256", "Rows"} or (
        mapping["Schema"] != MAPPING or mapping["Disposition"] != "unqualified-software-observation"
        or mapping["Hb02ClosureRootSha256"] != reviewed_capture
        or mapping["RootManifestSha256"] != reviewed_roots
        or mapping["CoreClosureSha256"] != index["ClosureSha256"]
    ):
        refuse("invalid source provenance mapping")
    if list(mapping) != ["Schema", "Disposition", "Hb02ClosureRootSha256",
                         "RootManifestSha256", "CoreClosureSha256", "Rows"]:
        refuse("R2_01_MAPPING_PROPERTY_ORDER")
    descriptors = index["Chunks"]
    if not isinstance(descriptors, list) or not 1 <= len(descriptors) <= 256:
        refuse("invalid chunk list")
    rows, aggregate = [], hashlib.sha256()
    aggregate_manifest_bytes = 0
    for i, item in enumerate(descriptors):
        if not isinstance(item, dict) or set(item) != {"Path", "Sha256", "Bytes", "Rows"} or (
            item["Path"] != "bridge-index/chunk-%04d.jsonl" % i
        ):
            refuse("changed chunk descriptor")
        if list(item) != ["Path", "Sha256", "Bytes", "Rows"]:
            refuse("R2_01_CHUNK_PROPERTY_ORDER")
        chunk_file = capture.safe_path(str(base / item["Path"]))
        if type(item["Bytes"]) is not int or not 0 < item["Bytes"] <= 4194304 or (
            chunk_file.stat().st_size != item["Bytes"]
        ):
            refuse("oversized/truncated raw JSONL chunk before read")
        if type(item["Rows"]) is not int or not 1 <= item["Rows"] <= 64 or (
            i < len(descriptors) - 1 and item["Rows"] != 64
        ):
            refuse("noncanonical 64-row Core chunk partition")
        aggregate_manifest_bytes += item["Bytes"]
        if aggregate_manifest_bytes > 67108864:
            refuse("cumulative Core manifest byte budget exceeded")
        contents = chunk_file.read_bytes()
        if len(contents) != item["Bytes"] or sha(contents) != item["Sha256"]:
            refuse("changed raw JSONL chunk bytes")
        lines = contents.splitlines(keepends=True)
        if len(lines) != item["Rows"] or any(not x.endswith(b"\n") for x in lines):
            refuse("changed JSONL row count")
        for line in lines:
            item = json.loads(line[:-1], object_pairs_hook=capture.pairs_unique)
            if json_bytes(item) != line[:-1]:
                refuse("noncanonical Core JSONL row")
            rows.append(item)
        aggregate.update(contents)
    if aggregate.hexdigest().upper() != index["ClosureSha256"] or len(rows) != len(mapping["Rows"]):
        refuse("changed closure digest / row count")
    if not 1 <= len(rows) <= MAX_ROWS:
        refuse("Core file count exceeded")
    total_bytes = 0
    for i, (row, original) in enumerate(zip(rows, mapping["Rows"])):
        if not isinstance(row, dict) or set(row) != {"Path", "Role", "Sha256", "Bytes"} or (
            not isinstance(original, dict) or set(original) != {
                "NodeId", "Kind", "SourceRole", "LogicalPath", "Path", "Sha256", "Bytes"
            }
        ):
            refuse("invalid row contract")
        if list(row) != ["Path", "Role", "Sha256", "Bytes"] or list(original) != [
            "NodeId", "Kind", "SourceRole", "LogicalPath", "Path", "Sha256", "Bytes"
        ]:
            refuse("R2_01_ROW_PROPERTY_ORDER")
        category = role({"kind": original["Kind"], "role": original["SourceRole"]})
        expected_path = "inputs/%05d%s" % (i, ".cs" if category in ("source", "owned-body") else ".bin")
        if row != {"Path": expected_path, "Role": category,
                    "Sha256": original["Sha256"], "Bytes": original["Bytes"]}:
            refuse("wrong source role/path/digest mapping")
        if type(row["Bytes"]) is not int or row["Bytes"] < (0 if category == "source" else 1) or (
            row["Bytes"] > (2097152 if category == "source" else 33554432)
        ):
            refuse("Core file limit exceeded")
        total_bytes += row["Bytes"]
        if total_bytes > MAX_SIZE:
            refuse("Core 512MiB total budget exceeded")
        f = capture.safe_path(str(base / expected_path))
        if f.stat().st_size != row["Bytes"]:
            refuse("mutated output file length")
        file_sha, size = capture.hash_file(f)
        if size != row["Bytes"] or file_sha.upper() != row["Sha256"]:
            refuse("mutated output file bytes")
        if category == "source":
            try:
                with f.open("rb") as stream:
                    import codecs
                    decode = codecs.getincrementaldecoder("utf-8")("strict")
                    while block := stream.read(65536):
                        decode.decode(block)
                    decode.decode(b"", final=True)
            except UnicodeDecodeError:
                refuse("Core source must be UTF-8")
    if not any(x["Role"] == "source" for x in rows):
        refuse("R2_01_NO_PRIMARY_SOURCE")
    if [x["Path"] for x in rows] != sorted(x["Path"] for x in rows):
        refuse("reordered Core source list")
    if len(descriptors) != (len(rows) + 63) // 64:
        refuse("noncanonical Core chunk count")
    if capture_bundle is not None:
        # Producer-side crosswalk replay only. Not a Core qualification gate.
        original_base = capture.safe_path(os.path.abspath(capture_bundle), file=False)
        if capture.verify(str(original_base)) != reviewed_capture:
            refuse("reviewed capture bytes changed during crosswalk replay")
        original = load(original_base / "index.json", capture.MAX_INDEX)
        if len(original["entries"]) != len(mapping["Rows"]):
            refuse("capture-map does not cover all reviewed inputs")
        for i, entry in enumerate(original["entries"]):
            expected_mapping = {
                "NodeId": entry["nodeId"], "Kind": entry["kind"],
                "SourceRole": entry["role"], "LogicalPath": entry["logicalPath"],
                "Path": rows[i]["Path"], "Sha256": entry["sha256"].upper(),
                "Bytes": entry["bytes"]}
            if mapping["Rows"][i] != expected_mapping:
                refuse("capture-map differs from independently pinned HB02 inventory")
    expected = {"bridge-index.json", "capture-map.json", "roots-manifest.json"} | {
        x["Path"] for x in rows
    } | {x["Path"] for x in descriptors}
    actual = set()
    for child in base.rglob("*"):
        if child.is_symlink() or getattr(child, "is_junction", lambda: False)():
            refuse("symlink/reparse output")
        if child.is_file():
            actual.add(child.relative_to(base).as_posix())
    if actual != expected:
        refuse("extra/missing transport files")
    actual_dirs = {x.relative_to(base).as_posix() for x in base.rglob("*") if x.is_dir()}
    if actual_dirs != {"inputs", "bridge-index"}:
        refuse("unexpected output directory")
    return index["ClosureSha256"]

def _publish_new_directory(work, target):
    """Atomic same-TEMP-filesystem publish that never replaces a destination."""
    if target.exists() or target.is_symlink():
        refuse("output destination created during stage; no overwrite")
    if sys.platform.startswith("linux"):
        # Plain POSIX rename can replace an empty destination directory.
        # Linux renameat2(RENAME_NOREPLACE) closes that TOCTOU window.
        import ctypes
        libc = ctypes.CDLL(None, use_errno=True)
        renameat2 = getattr(libc, "renameat2", None)
        if renameat2 is None:
            refuse("atomic no-replace directory publish unavailable")
        renameat2.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int,
                              ctypes.c_char_p, ctypes.c_uint]
        renameat2.restype = ctypes.c_int
        if renameat2(-100, os.fsencode(work), -100, os.fsencode(target), 1) != 0:
            errno = ctypes.get_errno()
            raise OSError(errno, os.strerror(errno), str(target))
    elif os.name == "nt":
        # Win32 MoveFile semantics behind os.rename refuse an existing target.
        os.rename(work, target)
    else:
        refuse("atomic no-replace directory publish unavailable on this OS")


def stage(bundle, reviewed_capture, roots_file, reviewed_roots, output, timeout_seconds=300,
          after_copy=None, *, require_executed_csc=False, paired_receipt=None,
          reviewed_receipt_sha=None):
    if not isinstance(reviewed_capture, str) or not re.fullmatch(r"[0-9a-f]{64}", reviewed_capture):
        refuse("explicit reviewed HB-INT-02 SHA required")
    if (paired_receipt is not None or reviewed_receipt_sha is not None) and not require_executed_csc:
        refuse("R2_01_EXECUTION_ARGUMENTS_REQUIRE_EXPLICIT_GATE")
    if require_executed_csc:
        require_execution_receipt(paired_receipt, reviewed_receipt_sha, reviewed_capture)
    if type(timeout_seconds) not in (int, float) or not 0 < timeout_seconds <= 3600:
        refuse("invalid capture staging timeout")
    started = time.monotonic()
    def deadline():
        if time.monotonic() - started >= timeout_seconds:
            refuse("staging timeout")
    source = capture.safe_path(os.path.abspath(bundle), file=False)
    if capture.verify(str(source)) != reviewed_capture:
        refuse("source capture differs from reviewed SHA")
    roots, raw_roots = check_roots(roots_file, reviewed_roots)
    captured = load(source / "index.json", capture.MAX_INDEX)
    entries = captured["entries"]
    if not isinstance(entries, list) or not 1 <= len(entries) <= MAX_ROWS:
        refuse("Core row budget exceeded; no truncation")
    expected = set(("nodeId", "kind", "role", "logicalPath", "path",
                    "sha256", "bytes", "chunks"))
    total, has_source = 0, False
    for row in entries:
        if not isinstance(row, dict) or set(row) != expected:
            refuse("unknown input entry fields")
        file_role = role(row)
        has_source |= file_role == "source"
        size = row["bytes"]
        if type(size) is not int or size < (0 if file_role == "source" else 1) or size > (
            2097152 if file_role == "source" else 33554432
        ):
            refuse("Core per-file byte budget exceeded")
        total += size
        if total > MAX_SIZE:
            refuse("Core workspace budget exceeded")
    if not has_source:
        refuse("missing primary source")
    target = Path(os.path.abspath(output))
    temporary = Path(os.path.abspath(tempfile.gettempdir()))
    if target.parent != temporary or target.exists() or target.is_symlink():
        refuse("output must be new direct TEMP child")
    capture.safe_path(str(temporary), file=False)
    deadline()
    # Build privately; the requested final destination is never visible partially.
    work = Path(tempfile.mkdtemp(prefix="hb-r2-01-stage-", dir=temporary))
    try:
        (work / "inputs").mkdir()
        (work / "bridge-index").mkdir()
        with (work / "roots-manifest.json").open("xb") as sink:
            sink.write(raw_roots)
        core_rows, mapping = [], []
        for i, row in enumerate(entries):
            deadline()
            category = role(row)
            name = "inputs/%05d%s" % (i, ".cs" if category in ("source", "owned-body") else ".bin")
            h, length = hashlib.sha256(), 0
            with (work / name).open("xb") as sink:
                for part in row["chunks"]:
                    deadline()
                    if not isinstance(part, dict) or set(part) != {"name", "bytes", "sha256"} or (
                        not re.fullmatch(r"chunk-[0-9]{8}\.bin", part["name"])
                    ):
                        refuse("invalid captured chunk identity")
                    chunk_path = capture.safe_path(str(source / part["name"]))
                    if type(part["bytes"]) is not int or not 0 < part["bytes"] <= capture.CHUNK_SIZE or (
                        chunk_path.stat().st_size != part["bytes"]
                    ):
                        refuse("captured chunk size changed before bounded read")
                    with chunk_path.open("rb") as stream:
                        data = stream.read(capture.CHUNK_SIZE + 1)
                    if len(data) != part["bytes"] or capture.sha(data) != part["sha256"]:
                        refuse("chunk changed during stage")
                    sink.write(data)
                    h.update(data)
                    length += len(data)
            if length != row["bytes"] or h.hexdigest() != row["sha256"]:
                refuse("source mutated during stage")
            core_rows.append({"Path": name, "Role": category,
                              "Sha256": h.hexdigest().upper(), "Bytes": length})
            mapping.append({"NodeId": row["nodeId"], "Kind": row["kind"],
                            "SourceRole": row["role"], "LogicalPath": row["logicalPath"],
                            "Path": name, "Sha256": h.hexdigest().upper(), "Bytes": length})
        if after_copy is not None:  # test injection, not exposed by CLI
            after_copy()
        deadline()
        if capture.verify(str(source)) != reviewed_capture:
            refuse("source changed after copy")
        descriptors, closure = [], hashlib.sha256()
        for i in range(0, len(core_rows), 64):
            deadline()
            raw = b"".join(json_bytes(row) + b"\n" for row in core_rows[i:i+64])
            if len(raw) > 4194304 or len(descriptors) >= 256:
                refuse("Core JSONL chunk budget exceeded")
            name = "bridge-index/chunk-%04d.jsonl" % len(descriptors)
            with (work / name).open("xb") as sink:
                sink.write(raw)
            descriptors.append({"Path": name, "Sha256": sha(raw),
                                "Bytes": len(raw), "Rows": len(core_rows[i:i+64])})
            closure.update(raw)
        close_sha = closure.hexdigest().upper()
        index = json_bytes({"Schema": CORE, "Roots": roots,
                            "Chunks": descriptors, "ClosureSha256": close_sha})
        if len(index) > 1048576:
            refuse("Core v2 index too large")
        with (work / "bridge-index.json").open("xb") as sink:
            sink.write(index)
        mapping_payload = json_bytes({"Schema": MAPPING, "Disposition": "unqualified-software-observation",
            "Hb02ClosureRootSha256": reviewed_capture, "RootManifestSha256": reviewed_roots,
            "CoreClosureSha256": close_sha, "Rows": mapping})
        if len(mapping_payload) > 4 * 1048576:
            refuse("mapping too large")
        with (work / "capture-map.json").open("xb") as sink:
            sink.write(mapping_payload)
        deadline()
        verify(work, reviewed_capture, reviewed_roots, source)
        deadline()
        if capture.verify(str(source)) != reviewed_capture:
            refuse("capture changed before atomic publish")
        check_roots(roots_file, reviewed_roots)
        deadline()
        _publish_new_directory(work, target)
        return close_sha
    finally:
        if work.exists():
            shutil.rmtree(work)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    command = commands.add_parser("stage")
    for name in ("bundle", "reviewed-capture", "roots", "reviewed-roots", "output"):
        command.add_argument("--" + name, required=True)
    command.add_argument("--timeout", type=float, default=300)
    command.add_argument("--require-executed-csc", action="store_true",
                         help="fail-closed real-execution gate; R1-02 v1 is not qualified")
    command.add_argument("--paired-receipt")
    command.add_argument("--reviewed-receipt-sha")
    command = commands.add_parser("verify")
    for name in ("output", "reviewed-capture", "reviewed-roots"):
        command.add_argument("--" + name, required=True)
    command.add_argument("--bundle", help="optional reviewed HB02 capture for exact crosswalk replay")
    args = parser.parse_args()
    result = (stage(args.bundle, args.reviewed_capture, args.roots, args.reviewed_roots,
                    args.output, args.timeout,
                    require_executed_csc=args.require_executed_csc,
                    paired_receipt=args.paired_receipt,
                    reviewed_receipt_sha=args.reviewed_receipt_sha) if args.command == "stage" else
              verify(args.output, args.reviewed_capture, args.reviewed_roots, args.bundle))
    print("Unqualified Core v2 ClosureSha256=" + result)

if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, TypeError, OSError, OverflowError, RecursionError, json.JSONDecodeError) as exc:
        print("HB-INT-02/03 Blocked: " + str(exc), file=sys.stderr)
        sys.exit(2)
