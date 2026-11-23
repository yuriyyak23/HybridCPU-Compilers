"""Candidate-only, lossless Csc argument to staged Core-row crosswalk.

This sidecar does not establish execution, provenance, ownership or admission. All
SHA-256 arguments are external pins; values copied into the sidecar are claims only.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sys

import segmented_csc_capture_v2 as capture
import stage_core_v2 as core_stage

SCHEMA = "hybridcpu.hardwarebridge.csc-closure-profile/v1"


def fail(message):
    raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def canonical(value):
    return capture.canonical(value)


def norm(path):
    return os.path.normcase(os.path.abspath(path))


def index_rows(bundle, index):
    """Read and validate each capture chunk; capture.verify already checks identities."""
    result = []
    for row in index["entries"]:
        pieces = []
        h = hashlib.sha256()
        size = 0
        for part in row["chunks"]:
            raw = capture.read_bounded(str(Path(bundle) / part["name"]), capture.CHUNK_SIZE)
            if len(raw) != part["bytes"] or sha(raw) != part["sha256"]:
                fail("capture chunk changed during sidecar projection")
            pieces.append(raw)
            h.update(raw)
            size += len(raw)
        if size != row["bytes"] or h.hexdigest() != row["sha256"]:
            fail("capture row bytes changed during sidecar projection")
        result.append(row)
    return result


def classify_token(token, cwd):
    """Mirror the file-valued token grammar in segmented_csc_capture_v2.command_files."""
    lower = token.lower()
    file_inputs = (("/reference:", "reference"), ("/r:", "reference"),
                   ("/analyzer:", "analyzer"), ("/analyzerconfig:", "analyzer-config"),
                   ("/additionalfile:", "additional-file"), ("/sourcelink:", "source-link"),
                   ("/appconfig:", "app-config"), ("/embed:", "embedded"))
    if lower.endswith(".cs") and (not token.startswith(("/", "-")) or
            (os.path.isabs(token) and not re.match(r"^/[a-z][a-z0-9+-]*:", lower))):
        path = str(capture.compiler_input_path(token, cwd))
        return "source", "csc-source", path
    for prefix, kind in file_inputs:
        if lower.startswith(prefix):
            path = token.split(":", 1)[1]
            if kind == "reference" and "=" in path:
                fail("reference aliases require an unsupported versioned profile")
            return "file-input", kind, str(capture.compiler_input_path(path, cwd))
    if lower.startswith("/generatedfilesout:"):
        path = token.split(":", 1)[1]
        return "generated-output-directory", None, str(capture.compiler_input_path(path, cwd, file=False))
    if lower.startswith(("/out:", "/pdb:", "/refout:", "/doc:", "/errorlog:")):
        return "output", None, token.split(":", 1)[1]
    return "option", None, None


def project(hb_bundle, hb_root_sha, hb_index_sha,
            core_bundle, roots_sha, core_index_sha, core_closure_sha):
    for name, value in (("HB closure", hb_root_sha), ("HB index", hb_index_sha),
                        ("roots manifest", roots_sha), ("Core index", core_index_sha),
                        ("Core closure", core_closure_sha)):
        if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{64}", value):
            fail("externally supplied canonical lower-case SHA-256 required: " + name)

    hb_root = capture.verify(hb_bundle)
    if hb_root != hb_root_sha:
        fail("HB closure differs from external pin")
    hb_index_path = capture.safe_path(str(Path(hb_bundle) / "index.json"))
    hb_index_raw = capture.read_bounded(str(hb_index_path), capture.MAX_INDEX)
    if sha(hb_index_raw) != hb_index_sha:
        fail("HB index bytes differ from external pin")
    hb_index = capture.strict_json(hb_index_raw)
    hb_rows = index_rows(hb_bundle, hb_index)

    # stage_core.verify checks Core chunks, staged input bytes, the capture-map,
    # roots, and the pinned HB capture. It is intentionally not an execution witness.
    verified_core_closure = core_stage.verify(core_bundle, hb_root_sha, roots_sha, hb_bundle)
    if verified_core_closure.lower() != core_closure_sha:
        fail("Core row closure differs from external pin")
    core_index_path = capture.safe_path(str(Path(core_bundle) / "bridge-index.json"))
    core_index_raw = capture.read_bounded(str(core_index_path), 1024 * 1024)
    if sha(core_index_raw) != core_index_sha:
        fail("Core index bytes differ from external pin")
    core_index = capture.strict_json(core_index_raw)
    if core_index.get("ClosureSha256", "").lower() != core_closure_sha:
        fail("Core index closure field differs from external pin")
    mapping_raw = capture.read_bounded(str(Path(core_bundle) / "capture-map.json"), 4 * 1024 * 1024)
    mapping = capture.strict_json(mapping_raw)
    if canonical(mapping) != mapping_raw or mapping.get("Hb02ClosureRootSha256") != hb_root_sha or \
            mapping.get("CoreClosureSha256", "").lower() != core_closure_sha:
        fail("Core crosswalk is not canonical or its pins disagree")

    if len(hb_rows) != len(mapping.get("Rows", [])):
        fail("Core staging omitted or added captured rows")
    files = []
    by_node_kind_path = {}
    for ordinal, (row, cross) in enumerate(zip(hb_rows, mapping["Rows"])):
        if cross != {"NodeId": row["nodeId"], "Kind": row["kind"],
                     "SourceRole": row["role"], "LogicalPath": row["logicalPath"],
                     "Path": "inputs/%05d%s" % (ordinal,
                         ".cs" if row["kind"] in ("csc-source", "generator-source") and
                         row["role"] in ("primary", "owned-body") else ".bin"),
                     "Sha256": row["sha256"].upper(), "Bytes": row["bytes"]}:
            fail("Core row ordinal does not exactly crosswalk to pinned HB entry")
        core_row = {"Path": cross["Path"], "Role": core_stage.role(row),
                    "Sha256": row["sha256"].upper(), "Bytes": row["bytes"]}
        files.append({"coreRowOrdinal": ordinal, "nodeId": row["nodeId"],
                      "kind": row["kind"], "sourceRole": row["role"],
                      "logicalPath": row["logicalPath"], "corePath": core_row["Path"],
                      "role": core_row["Role"], "bytes": row["bytes"], "sha256": row["sha256"]})
        key = (row["nodeId"], row["kind"], norm(row["path"]))
        if key in by_node_kind_path:
            fail("duplicate HB input identity prevents an unambiguous argv join")
        by_node_kind_path[key] = ordinal

    generated = [{"nodeId": f["nodeId"], "coreRowOrdinal": f["coreRowOrdinal"],
                  "logicalPath": f["logicalPath"], "corePath": f["corePath"],
                  "bytes": f["bytes"], "sha256": f["sha256"]}
                 for f in files if f["kind"] == "generator-source"]
    commands = []
    toolchains = []
    for command in hb_index["commands"]:
        node_id = command["nodeId"]
        argv = command["argv"]
        cwd = command["workingDirectory"]
        sources, artifacts, generator_dir = capture.command_files(argv, cwd)
        source_cursor = artifact_cursor = 0
        projected_argv = []
        associated = []
        for ordinal, token in enumerate(argv):
            token_kind, input_kind, path = classify_token(token, cwd)
            core_ordinal = None
            if token_kind == "source":
                if source_cursor >= len(sources) or norm(path) != norm(sources[source_cursor]):
                    fail("source token order differs from HB response parser")
                source_cursor += 1
                core_ordinal = by_node_kind_path.get((node_id, "csc-source", norm(path)))
            elif token_kind == "file-input":
                if artifact_cursor >= len(artifacts) or artifacts[artifact_cursor] != (input_kind, path):
                    fail("file-valued option order differs from HB response parser")
                artifact_cursor += 1
                core_ordinal = by_node_kind_path.get((node_id, input_kind, norm(path)))
            if token_kind in ("source", "file-input"):
                if core_ordinal is None:
                    fail("Core staged closure omitted an argv-referenced file input")
                associated.append(core_ordinal)
            projected_argv.append({"ordinal": ordinal, "token": token,
                                   "kind": token_kind, "coreRowOrdinal": core_ordinal})
        if source_cursor != len(sources) or artifact_cursor != len(artifacts):
            fail("not all parser-recognized source/artifact arguments were projected")
        expected_rows = {i for i, f in enumerate(files) if f["nodeId"] == node_id and
                         f["kind"] in ("csc-source", "reference", "analyzer", "analyzer-config",
                                        "additional-file", "source-link", "app-config", "embedded")}
        if set(associated) != expected_rows:
            fail("argv inputs and staged rows differ, including role or multiplicity")
        commands.append({"nodeId": node_id, "projectRole": command["projectRole"],
                         "workingDirectory": cwd,
                         "generatorOutputDirectory": command["generatorOutputDirectory"],
                         "argvSha256": command["argvSha256"], "argv": projected_argv})
        compiler = next((f for f in files if f["nodeId"] == node_id and f["kind"] == "csc-binary"), None)
        project_file = next((f for f in files if f["nodeId"] == node_id and f["kind"] == "project"), None)
        assets = next((f for f in files if f["nodeId"] == node_id and f["kind"] == "assets"), None)
        if compiler is None or project_file is None:
            fail("Core staged closure omitted compiler or project identity")
        toolchains.append({"nodeId": node_id, "compilerSha256": compiler["sha256"],
                           "projectSha256": project_file["sha256"],
                           "assetsSha256": assets["sha256"] if assets else None})

    payload = {"schema": SCHEMA, "disposition": "candidate-only",
               "hbClosureRootSha256": hb_root_sha, "hbIndexSha256": hb_index_sha,
               "coreIndexSha256": core_index_sha, "coreClosureSha256": core_closure_sha,
               "commands": commands, "files": files, "generated": generated,
               "toolchains": toolchains,
               "claims": {"execution": "NotEstablished", "provenance": "NotEstablished",
                          "ownership": "NotEstablished", "admission": "NotAdmitted"}}
    raw = canonical(payload)
    return raw, sha(raw)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--hb-bundle", required=True)
    parser.add_argument("--hb-closure-sha256", required=True)
    parser.add_argument("--hb-index-sha256", required=True)
    parser.add_argument("--core-bundle", required=True)
    parser.add_argument("--roots-sha256", required=True)
    parser.add_argument("--core-index-sha256", required=True)
    parser.add_argument("--core-closure-sha256", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args(argv)
    raw, digest = project(args.hb_bundle, args.hb_closure_sha256, args.hb_index_sha256,
                          args.core_bundle, args.roots_sha256, args.core_index_sha256,
                          args.core_closure_sha256)
    output = Path(args.output)
    if output.exists() or not output.is_absolute() or os.path.abspath(output) != str(output):
        fail("output must be a new canonical absolute path")
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("xb") as sink:
        sink.write(raw)
    print(json.dumps({"schema": SCHEMA, "status": "CandidateOnly", "sidecar": str(output),
                      "sha256": digest, "bytes": len(raw), "execution": "NotEstablished",
                      "provenance": "NotEstablished", "admission": "NotAdmitted"},
                     sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, OSError, KeyError, TypeError, IndexError) as exc:
        print("Csc closure profile refused: " + str(exc), file=sys.stderr)
        raise SystemExit(2)
