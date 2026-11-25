"""Additive, unqualified Csc output-byte snapshot bound to a verified V2 input closure.

This module does not run Csc and does not attest that an observed Csc process read the
captured bytes. It transports bytes from an independently pinned output manifest into
a separate output workspace; the source-closure/v2 index is not modified.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
import segmented_csc_capture_v2 as v2

MANIFEST_SCHEMA = "hybridcpu.hardwarebridge.csc-output-manifest/v1"
INDEX_SCHEMA = "circuitweave.bridge-compiler-output-supplement/v4"
WITNESS_SCHEMA = "circuitweave.csc-task-output-witness/v2"
DISPOSITION = "unqualified-software-output-observation"
MAX_MANIFEST = 2 * 1024 * 1024
MAX_OUTPUT = 32 * 1024 * 1024
MAX_OUTPUTS = 4
MAX_TOTAL_OUTPUT_BYTES = 128 * 1024 * 1024
CHUNK_NAME = "chunk-00000000.jsonl"
REQUIRED_ROLES = ("compiler-pe", "compiler-pdb", "compiler-refout")
OPTIONAL_ROLE = "win32-resource-output"
ALLOWED_ROLES = frozenset((*REQUIRED_ROLES, OPTIONAL_ROLE))
HEX = re.compile(r"^[0-9a-fA-F]{64}$")


def fail(message):
    raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("utf-8")


def _hash_pin(value, name):
    if not isinstance(value, str) or not HEX.fullmatch(value):
        fail("invalid SHA-256 pin: " + name)
    return value.lower()


def _logical_output(value):
    if not isinstance(value, str) or "\\" in value:
        fail("output logical path must use relative forward-slash separators")
    normalized = v2.logical(value)
    if normalized != value:
        fail("noncanonical output logical path")
    return normalized


def _ordinal_key(value):
    # Match .NET StringComparer.Ordinal over UTF-16 code units. Python's default
    # string ordering compares Unicode code points and differs for supplementary
    # characters versus BMP values in U+D800..U+DBFF.
    return value.encode("utf-16-be", errors="surrogatepass")


def _primary(parent_index):
    commands = parent_index.get("commands")
    if not isinstance(commands, list) or not commands:
        fail("parent V2 index has no Csc commands")
    primary = [row for row in commands if isinstance(row, dict) and row.get("projectRole") == "primary"]
    if len(primary) != 1:
        fail("parent V2 index must contain exactly one primary Csc command")
    row = primary[0]
    if not isinstance(row.get("nodeId"), str) or not row["nodeId"]:
        fail("primary Csc node identity missing")
    if not isinstance(row.get("argv"), list) or not row["argv"]:
        fail("primary ordered Csc argv missing")
    argv_sha = sha(canonical(row["argv"]))
    if argv_sha != _hash_pin(row.get("argvSha256"), "primary argv"):
        fail("primary ordered argv digest mismatch")
    cwd = row.get("workingDirectory")
    if not isinstance(cwd, str) or not cwd:
        fail("primary Csc working directory missing")
    return row, argv_sha


def _command_output_paths(command):
    argv = command["argv"]
    def single(prefix, *, optional=False):
        found = [arg[len(prefix):] for arg in argv
                 if isinstance(arg, str) and arg.lower().startswith(prefix.lower())]
        if len(found) > 1 or (not optional and len(found) != 1):
            fail("missing/duplicate primary output switch: " + prefix)
        return found[0] if found else None

    out = single("/out:")
    refout = single("/refout:")
    pdb = single("/pdb:", optional=True)
    if pdb is None:
        pdb = str(PurePosixPath(out.replace("\\", "/")).with_suffix(".pdb"))
    return {
        "compiler-pe": _to_logical(out, command["workingDirectory"]),
        "compiler-pdb": _to_logical(pdb, command["workingDirectory"]),
        "compiler-refout": _to_logical(refout, command["workingDirectory"]),
    }


def _to_logical(token_path, cwd):
    if not isinstance(token_path, str) or not token_path:
        fail("empty Csc output switch path")
    # The task response paths in this capture use Windows path semantics. On a
    # Windows producer, make a stable path relative to the observed Csc cwd.
    if os.name == "nt":
        import ntpath
        absolute = ntpath.normpath(token_path if ntpath.isabs(token_path) else ntpath.join(cwd, token_path))
        base = ntpath.normpath(cwd)
        try:
            relative = ntpath.relpath(absolute, base)
        except ValueError:
            fail("Csc output path has a different drive from its working directory")
        if relative == ".." or relative.startswith("..\\"):
            fail("Csc output escapes the primary working directory")
        return _logical_output(relative.replace("\\", "/"))
    absolute = os.path.abspath(token_path if os.path.isabs(token_path) else os.path.join(cwd, token_path))
    relative = os.path.relpath(absolute, cwd)
    if relative == ".." or relative.startswith("../"):
        fail("Csc output escapes the primary working directory")
    return _logical_output(relative.replace(os.sep, "/"))


def _parse_manifest(manifest_bytes, expected_manifest_sha, parent_index_sha, parent_closure_sha,
                    parent_index, primary, primary_argv_sha):
    if not manifest_bytes or len(manifest_bytes) > MAX_MANIFEST:
        fail("empty/oversized Csc output manifest")
    actual_manifest_sha = sha(manifest_bytes)
    if actual_manifest_sha != _hash_pin(expected_manifest_sha, "output manifest"):
        fail("output manifest differs from its external SHA-256 pin")
    obj = v2.strict_json(manifest_bytes)
    shape = {"schema", "disposition", "baseIndexSha256", "baseClosureSha256", "primaryNodeId",
             "primaryArgvSha256", "taskCrosswalkSha256", "directBuildComparisonSha256",
             "resourceOutputStatus", "outputs"}
    if not isinstance(obj, dict) or set(obj) != shape:
        fail("unexpected Csc output manifest shape")
    if obj["schema"] != MANIFEST_SCHEMA or obj["disposition"] != "unqualified-software-output-observation":
        fail("unsupported Csc output manifest schema/disposition")
    if _hash_pin(obj["baseIndexSha256"], "manifest parent index") != parent_index_sha or \
            _hash_pin(obj["baseClosureSha256"], "manifest parent closure") != parent_closure_sha:
        fail("output manifest is bound to stale/different V2 parent pins")
    if obj["primaryNodeId"] != primary["nodeId"] or \
            _hash_pin(obj["primaryArgvSha256"], "manifest primary argv") != primary_argv_sha:
        fail("output manifest primary command binding differs from V2 parent")
    crosswalk_sha = _hash_pin(obj["taskCrosswalkSha256"], "task crosswalk")
    comparison_sha = _hash_pin(obj["directBuildComparisonSha256"], "direct build comparison")
    resource_status = obj["resourceOutputStatus"]
    if resource_status not in ("NotPresent", "CapturedTaskOutput"):
        fail("resource output status must be explicit")
    outputs = obj["outputs"]
    if not isinstance(outputs, list) or not 1 <= len(outputs) <= MAX_OUTPUTS:
        fail("empty/oversized Csc output manifest")
    expected_paths = _command_output_paths(primary)
    by_role = {}
    seen_source = set()
    seen_logical = set()
    for row in outputs:
        if not isinstance(row, dict) or set(row) != {"role", "logicalPath", "sourcePath", "sha256", "bytes"}:
            fail("unexpected output row shape")
        role = row["role"]
        if role not in ALLOWED_ROLES or role in by_role:
            fail("duplicate/unsupported Csc output role")
        logical_path = _logical_output(row["logicalPath"])
        key = logical_path.casefold()
        if key in seen_logical:
            fail("duplicate output logical path")
        seen_logical.add(key)
        if role in expected_paths and logical_path.casefold() != expected_paths[role].casefold():
            fail("output manifest path differs from primary Csc option: " + role)
        if role in REQUIRED_ROLES and logical_path != expected_paths[role]:
            fail("required Csc output role missing its primary argv path")
        source = v2.safe_path(row["sourcePath"])
        source_logical = _to_logical(str(source), primary["workingDirectory"])
        if source_logical.casefold() != logical_path.casefold():
            fail("output source path differs from primary Csc argv location: " + role)
        expected_source = os.path.normcase(os.path.normpath(os.path.abspath(
            os.path.join(primary["workingDirectory"], *PurePosixPath(logical_path).parts))))
        actual_source = os.path.normcase(os.path.normpath(os.path.abspath(str(source))))
        if actual_source != expected_source:
            fail("output source absolute path differs from primary Csc argv location: " + role)
        source_key = v2.path_key(str(source))
        if source_key in seen_source:
            fail("multiple output roles alias one source file")
        seen_source.add(source_key)
        size = row["bytes"]
        if not isinstance(size, int) or isinstance(size, bool) or not 1 <= size <= MAX_OUTPUT:
            fail("Csc output size outside bounded 1..32 MiB range")
        digest = _hash_pin(row["sha256"], "output row")
        if v2.pins_file(source, digest) != size:
            fail("changed/stale file size differs from output witness")
        by_role[role] = {"role": role, "logicalPath": logical_path, "sourcePath": str(source),
                         "sha256": digest, "bytes": size}
    if set(by_role) - set(ALLOWED_ROLES):
        fail("unsupported output role")
    if not all(role in by_role for role in REQUIRED_ROLES):
        fail("missing required compiler-pe/compiler-pdb/compiler-refout role")
    has_resource = OPTIONAL_ROLE in by_role
    if (resource_status == "NotPresent" and has_resource) or \
            (resource_status == "CapturedTaskOutput" and not has_resource):
        fail("resource output role cardinality differs from explicit status")
    if sum(row["bytes"] for row in by_role.values()) > MAX_TOTAL_OUTPUT_BYTES:
        fail("total Csc output byte budget exceeded")
    return actual_manifest_sha, crosswalk_sha, comparison_sha, resource_status, \
        sorted(by_role.values(), key=lambda x: x["logicalPath"])


def _capture_source(source_path, target_path, expected_sha, expected_bytes):
    source, stream, before = v2.stable_open(source_path)
    target_path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    if target_path.exists() or target_path.is_symlink():
        fail("output payload path collision")
    digest = hashlib.sha256()
    total = 0
    try:
        with stream:
            with target_path.open("xb") as output:
                while block := stream.read(v2.CHUNK_SIZE):
                    total += len(block)
                    if total > MAX_OUTPUT:
                        fail("Csc output exceeded per-file bound during capture")
                    digest.update(block)
                    output.write(block)
                output.flush()
                os.fsync(output.fileno())
            v2.ensure_stable(source, stream, before)
        if total != expected_bytes or digest.hexdigest() != expected_sha:
            fail("Csc output changed while being captured: " + str(source))
        return total, digest.hexdigest()
    except BaseException:
        raise


def capture(parent_dir, expected_parent_index_sha, expected_parent_closure_sha,
            manifest_path, expected_manifest_sha, output_dir, *,
            expected_capture_index_sha, expected_capture_root_sha, after_copy=None):
    parent = v2.safe_path(os.path.abspath(parent_dir), file=False)
    output = Path(os.path.abspath(output_dir))
    temp_root = v2.safe_path(os.path.abspath(tempfile.gettempdir()), file=False)
    if output.parent != temp_root or output.exists() or output.is_symlink():
        fail("output sidecar must be a new direct child of TEMP")
    v2.safe_path(str(output.parent), file=False)
    parent_index_bytes = v2.read_bounded(parent / "index.json", v2.MAX_INDEX)
    parent_index_sha = sha(parent_index_bytes)
    expected_index_sha = _hash_pin(expected_parent_index_sha, "parent index")
    expected_closure_sha = _hash_pin(expected_parent_closure_sha, "parent closure")
    capture_index_sha = _hash_pin(expected_capture_index_sha, "raw capture index")
    capture_root_sha = _hash_pin(expected_capture_root_sha, "raw capture root")
    if parent_index_sha != expected_index_sha:
        fail("parent V2 index SHA-256 differs from external pin")
    parent_closure = v2.verify(str(parent))
    if parent_closure != expected_closure_sha:
        fail("parent V2 closure SHA-256 differs from external pin")
    parent_index = v2.strict_json(parent_index_bytes)
    primary, primary_argv_sha = _primary(parent_index)
    manifest_bytes = v2.read_bounded(manifest_path, MAX_MANIFEST)
    _, crosswalk_sha, comparison_sha, resource_status, outputs = _parse_manifest(
        manifest_bytes, expected_manifest_sha, parent_index_sha, parent_closure,
        parent_index, primary, primary_argv_sha)

    output.mkdir(mode=0o700)
    rows = []
    try:
        for row in outputs:
            logical_path = row["logicalPath"]
            target = output.joinpath(*PurePosixPath(logical_path).parts)
            # Keep the Core relative path equal to the captured Csc output path.
            resolved = Path(os.path.abspath(target))
            if not v2.inside(resolved, output) or resolved == output:
                fail("output payload escapes sidecar workspace")
            size, actual_sha = _capture_source(row["sourcePath"], resolved,
                                              row["sha256"], row["bytes"])
            rows.append({"Path": logical_path, "Role": row["role"],
                         "Sha256": actual_sha.upper(), "Bytes": size})
        if after_copy is not None:  # test hook only; not exposed by the CLI
            after_copy()

        # Core V4 consumes one globally Path-sorted canonical JSONL manifest chunk.
        rows.sort(key=lambda row: _ordinal_key(row["Path"]))
        jsonl = b"".join(canonical(row) + b"\n" for row in rows)
        chunk_path = output / CHUNK_NAME
        with chunk_path.open("xb") as stream:
            stream.write(jsonl)
            stream.flush()
            os.fsync(stream.fileno())
        closure_sha = sha(jsonl)
        witness = {
            "Schema": WITNESS_SCHEMA,
            # V4 Base* identify the raw Bridge capture pinned by CscClosureProfileV1.
            # Core V2 index/closure remain separate request pins.
            "BaseIndexSha256": capture_index_sha.upper(),
            "BaseClosureSha256": capture_root_sha.upper(),
            "PrimaryNodeId": primary["nodeId"],
            "PrimaryArgvSha256": primary_argv_sha.upper(),
            "ResourceOutputStatus": resource_status,
            "TaskCrosswalkSha256": crosswalk_sha.upper(),
            "DirectBuildComparisonSha256": comparison_sha.upper(),
            "Outputs": rows,
            "Disposition": "unqualified-software-output-observation",
            "TaskExecution": "NotEstablished",
            "ResponseConsumption": "NotEstablished",
            "Provenance": "NotEstablished",
            "Admission": "NotAdmitted",
        }
        witness_bytes = canonical(witness)
        witness_sha = sha(witness_bytes)
        with (output / "task-output-witness.json").open("xb") as stream:
            stream.write(witness_bytes)
            stream.flush()
            os.fsync(stream.fileno())
        index = {
            "Schema": INDEX_SCHEMA,
            "Disposition": DISPOSITION,
            "BaseIndexSha256": capture_index_sha.upper(),
            "BaseClosureSha256": capture_root_sha.upper(),
            "TaskOutputWitnessSha256": witness_sha.upper(),
            "PrimaryNodeId": primary["nodeId"],
            "PrimaryArgvSha256": primary_argv_sha.upper(),
            "ResourceOutputStatus": resource_status,
            "TaskCrosswalkSha256": crosswalk_sha.upper(),
            "DirectBuildComparisonSha256": comparison_sha.upper(),
            "Chunks": [{"Path": CHUNK_NAME, "Sha256": sha(jsonl).upper(),
                        "Bytes": len(jsonl), "Rows": len(rows)}],
            "ClosureSha256": closure_sha.upper(),
            "TaskExecution": "NotEstablished",
            "ResponseConsumption": "NotEstablished",
            "Provenance": "NotEstablished",
            "Admission": "NotAdmitted",
        }
        index_bytes = canonical(index)
        with (output / "index.json").open("xb") as stream:
            stream.write(index_bytes)
            stream.flush()
            os.fsync(stream.fileno())
        verify(str(output), expected_capture_index_sha=capture_index_sha,
               expected_capture_root_sha=capture_root_sha,
               expected_output_index_sha=sha(index_bytes),
               expected_task_witness_sha=witness_sha)
        return {"indexSha256": sha(index_bytes), "closureSha256": closure_sha,
                "taskOutputWitnessSha256": witness_sha, "rows": len(rows),
                "bytes": sum(row["Bytes"] for row in rows), "resourceOutputStatus": resource_status}
    except BaseException:
        # Retain partial output for diagnosis; never remove by pathname.
        raise


def verify(output_dir, *, expected_capture_index_sha, expected_capture_root_sha,
           expected_output_index_sha, expected_task_witness_sha):
    root = v2.safe_path(os.path.abspath(output_dir), file=False)
    index_bytes = v2.read_bounded(root / "index.json", MAX_MANIFEST)
    if sha(index_bytes) != _hash_pin(expected_output_index_sha, "output index"):
        fail("output index SHA-256 differs from external pin")
    index = v2.strict_json(index_bytes)
    expected_shape = {"Schema", "Disposition", "BaseIndexSha256", "BaseClosureSha256",
                      "TaskOutputWitnessSha256", "PrimaryNodeId", "PrimaryArgvSha256",
                      "ResourceOutputStatus", "TaskCrosswalkSha256", "DirectBuildComparisonSha256",
                      "Chunks", "ClosureSha256", "TaskExecution", "ResponseConsumption",
                      "Provenance", "Admission"}
    if not isinstance(index, dict) or set(index) != expected_shape or \
            index["Schema"] != INDEX_SCHEMA or index["Disposition"] != DISPOSITION:
        fail("unknown/malformed compiler output supplement index")
    if _hash_pin(index["BaseIndexSha256"], "indexed raw capture") != _hash_pin(expected_capture_index_sha, "expected raw capture index") or \
            _hash_pin(index["BaseClosureSha256"], "indexed raw capture root") != _hash_pin(expected_capture_root_sha, "expected raw capture root"):
        fail("output supplement is bound to a stale/different raw Bridge capture")
    if _hash_pin(index["TaskOutputWitnessSha256"], "indexed witness") != _hash_pin(expected_task_witness_sha, "expected witness"):
        fail("output supplement task witness differs from external pin")
    if index["TaskExecution"] != "NotEstablished" or index["ResponseConsumption"] != "NotEstablished" or \
            index["Provenance"] != "NotEstablished" or index["Admission"] != "NotAdmitted":
        fail("output supplement cannot assert execution, provenance or admission")
    if index["ResourceOutputStatus"] not in ("NotPresent", "CapturedTaskOutput"):
        fail("resource output status missing/unsupported")
    descriptors = index["Chunks"]
    if not isinstance(descriptors, list) or len(descriptors) != 1 or \
            not isinstance(descriptors[0], dict) or \
            set(descriptors[0]) != {"Path", "Sha256", "Bytes", "Rows"} or \
            descriptors[0].get("Path") != CHUNK_NAME:
        fail("output supplement requires exactly one canonical row chunk")
    desc = descriptors[0]
    if not isinstance(desc["Bytes"], int) or not 1 <= desc["Bytes"] <= 64 * 1024 or \
            not isinstance(desc["Rows"], int) or not 3 <= desc["Rows"] <= MAX_OUTPUTS:
        fail("output row chunk is outside bounds")
    chunk_bytes = v2.read_bounded(root / CHUNK_NAME, 64 * 1024)
    if len(chunk_bytes) != desc["Bytes"] or sha(chunk_bytes) != _hash_pin(desc["Sha256"], "row chunk"):
        fail("output row chunk missing/changed")
    if sha(chunk_bytes) != _hash_pin(index["ClosureSha256"], "output closure"):
        fail("output closure digest mismatch")
    lines = chunk_bytes.splitlines(keepends=True)
    if len(lines) != desc["Rows"] or any(not line.endswith(b"\n") or line == b"\n" for line in lines):
        fail("output JSONL row count/termination mismatch")
    parsed = []
    for line in lines:
        row = v2.strict_json(line[:-1])
        if not isinstance(row, dict) or set(row) != {"Path", "Role", "Sha256", "Bytes"} or canonical(row) + b"\n" != line:
            fail("noncanonical/unsupported output row")
        path = _logical_output(row["Path"])
        if row["Role"] not in ALLOWED_ROLES or not isinstance(row["Bytes"], int) or isinstance(row["Bytes"], bool) or not 1 <= row["Bytes"] <= MAX_OUTPUT:
            fail("invalid output role/size")
        digest = _hash_pin(row["Sha256"], "output row")
        payload = v2.read_bounded(root.joinpath(*PurePosixPath(path).parts), MAX_OUTPUT)
        if len(payload) != row["Bytes"] or sha(payload) != digest:
            fail("output payload missing/altered")
        parsed.append(row)
    if [r["Path"] for r in parsed] != sorted((r["Path"] for r in parsed), key=_ordinal_key) or \
            len({r["Path"].casefold() for r in parsed}) != len(parsed) or \
            len({r["Role"] for r in parsed}) != len(parsed):
        fail("duplicate/reordered output paths or roles")
    roles = {r["Role"] for r in parsed}
    if not set(REQUIRED_ROLES).issubset(roles) or roles - ALLOWED_ROLES:
        fail("missing/unsupported compiler output role")
    if (index["ResourceOutputStatus"] == "NotPresent" and OPTIONAL_ROLE in roles) or \
            (index["ResourceOutputStatus"] == "CapturedTaskOutput" and OPTIONAL_ROLE not in roles):
        fail("resource output role cardinality mismatch")
    witness_bytes = v2.read_bounded(root / "task-output-witness.json", MAX_MANIFEST)
    if sha(witness_bytes) != _hash_pin(expected_task_witness_sha, "witness file"):
        fail("task-output-witness.json missing/altered")
    witness = v2.strict_json(witness_bytes)
    expected_witness_shape = {"Schema", "BaseIndexSha256", "BaseClosureSha256", "PrimaryNodeId",
                              "PrimaryArgvSha256", "ResourceOutputStatus", "TaskCrosswalkSha256",
                              "DirectBuildComparisonSha256", "Outputs", "Disposition", "TaskExecution",
                              "ResponseConsumption", "Provenance", "Admission"}
    if not isinstance(witness, dict) or set(witness) != expected_witness_shape or \
            witness["Schema"] != WITNESS_SCHEMA or canonical(witness) != witness_bytes:
        fail("unknown/noncanonical task output witness")
    for field in ("BaseIndexSha256", "BaseClosureSha256", "TaskOutputWitnessSha256",
                  "PrimaryNodeId", "PrimaryArgvSha256", "ResourceOutputStatus",
                  "TaskCrosswalkSha256", "DirectBuildComparisonSha256"):
        witness_field = "TaskOutputWitnessSha256" if field == "TaskOutputWitnessSha256" else field
        if witness_field in witness and index[field] != witness[witness_field]:
            fail("witness/index output binding mismatch: " + field)
    if witness["Outputs"] != parsed or witness["Disposition"] != DISPOSITION or \
            witness["TaskExecution"] != "NotEstablished" or witness["ResponseConsumption"] != "NotEstablished" or \
            witness["Provenance"] != "NotEstablished" or witness["Admission"] != "NotAdmitted":
        fail("witness outputs or authority fields differ from verified index")
    expected_files = {CHUNK_NAME, "index.json", "task-output-witness.json"}
    expected_files.update(PurePosixPath(row["Path"]).as_posix() for row in parsed)
    actual_files = {path.relative_to(root).as_posix() for path in root.rglob("*") if path.is_file()}
    if actual_files != expected_files:
        fail("extra/missing output sidecar files")
    return {"indexSha256": sha(index_bytes), "closureSha256": index["ClosureSha256"].lower(),
            "taskOutputWitnessSha256": sha(witness_bytes), "rows": len(parsed),
            "bytes": sum(row["Bytes"] for row in parsed), "resourceOutputStatus": index["ResourceOutputStatus"]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--parent", required=True, help="Verified Csc closure/v2 input capture directory")
    parser.add_argument("--expected-parent-index-sha256", required=True)
    parser.add_argument("--expected-parent-closure-sha256", required=True)
    parser.add_argument("--expected-capture-index-sha256", required=True,
                        help="Raw HardwareBridge capture index pin from the audited Csc profile")
    parser.add_argument("--expected-capture-root-sha256", required=True,
                        help="Raw HardwareBridge capture closure pin from the audited Csc profile")
    parser.add_argument("--manifest", required=True, help="Canonical, externally pinned output witness manifest")
    parser.add_argument("--expected-manifest-sha256", required=True)
    parser.add_argument("--output", required=True, help="New direct child of TEMP")
    args = parser.parse_args()
    result = capture(args.parent, args.expected_parent_index_sha256,
                     args.expected_parent_closure_sha256, args.manifest,
                     args.expected_manifest_sha256, args.output,
                     expected_capture_index_sha=args.expected_capture_index_sha256,
                     expected_capture_root_sha=args.expected_capture_root_sha256)
    print("HB-INT-02 compiler output snapshot unqualified " + json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, TypeError, OSError, json.JSONDecodeError) as error:
        message = str(error)
        code = "HB02_OUTPUT_INVALID"
        if "path" in message or "symlink" in message or "reparse" in message:
            code = "HB02_OUTPUT_PATH"
        elif "changed" in message or "mutated" in message or "concurrent" in message:
            code = "HB02_OUTPUT_DRIFT"
        elif "bound" in message or "oversized" in message or "budget" in message:
            code = "HB02_OUTPUT_BOUND"
        print(json.dumps({"schema": "hybridcpu.hardwarebridge.r1-02-refusal/v1",
                          "disposition": "Blocked", "reasonCode": code,
                          "reasonDigestSha256": sha(message.encode("utf-8"))}, sort_keys=True))
        raise SystemExit(2)
