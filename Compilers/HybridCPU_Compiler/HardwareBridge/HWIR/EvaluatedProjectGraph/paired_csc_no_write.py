"""Opt-in pair of read-only Golden byte sets, using HB-INT-02 unchanged wire v2.

Consumes supplied graph + Csc observations; does not execute or attest Csc.
Never writes to either original snapshot; all output is a fresh OS TEMP child.
Receipts are local-only, not safe to upload wholesale (contain workspace paths).
"""
import argparse
import hashlib
import json
import os
import re
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile

import segmented_csc_capture_v2 as hb

SCHEMA = "hybridcpu.hardwarebridge.paired-csc-capture-receipt/v1"
CORE_MAX_ROWS = 8192
CORE_MAX_BYTES = 512 * 1024 * 1024
WITNESS_SCHEMA = "hybridcpu.hardwarebridge.r1-02-external-task-crosswalk/v1"
INVENTORY_MAX_FILES = hb.MAX_FILES


def reject(message):
    raise ValueError("Blocked: " + message)


def new_temp_child(path):
    raw = Path(os.path.abspath(path))
    root = Path(os.path.abspath(tempfile.gettempdir()))
    hb.safe_path(str(root), file=False)
    if raw.parent != root or raw.exists() or raw.is_symlink():
        reject("outputs must be different, nonexistent direct OS TEMP children")
    return raw


def source_inventory(path, expected_commit=None):
    root = hb.safe_path(os.path.abspath(path), file=False)
    if expected_commit is not None and (not isinstance(expected_commit, str)
                                        or len(expected_commit) != 40
                                        or any(c not in "0123456789abcdef" for c in expected_commit)):
        reject("expected Git commit must be exact lowercase 40-hex SHA")
    rows = []
    total = 0
    # Deliberately exclude only administrative .git metadata; the exact source
    # tree, including ignored/untracked source files, is enumerated and hashed.
    for folder, dirs, files in os.walk(root, topdown=True, followlinks=False):
        dirs.sort()
        files.sort()
        for directory in dirs:
            hb.safe_path(str(Path(folder) / directory), file=False)
        if Path(folder) == root and ".git" in dirs:
            dirs.remove(".git")
        for filename in files:
            full = Path(folder) / filename
            hb.safe_path(str(full))
            if Path(folder) == root and filename == ".git":
                reject("git worktree pointer file not supported in immutable snapshot")
            rel = full.relative_to(root).as_posix()
            hb.logical(rel)
            digest, length = hb.hash_file(full)
            total += length
            rows.append((rel, length, digest))
            if len(rows) > INVENTORY_MAX_FILES or total > hb.MAX_BYTES:
                reject("snapshot exceeds bounded 20000 files / 8 GiB; no truncation")
    identities = [x[0].casefold() for x in rows]
    if len(set(identities)) != len(identities):
        reject("duplicate case-insensitive snapshot path")
    status_sha = None
    commit = None
    if (root / ".git").exists():
        # No git index refresh/lock, no hooks, no config from the host account.
        env = dict(os.environ, GIT_OPTIONAL_LOCKS="0", GIT_CONFIG_NOSYSTEM="1",
                   GIT_CONFIG_GLOBAL=os.devnull, GIT_TERMINAL_PROMPT="0")
        def git(*args):
            process = subprocess.run(["git", "-C", str(root), *args], env=env,
                                     capture_output=True, timeout=30, check=False)
            if process.returncode or len(process.stdout) > hb.MAX_INDEX:
                reject("read-only git status/HEAD unavailable (exit=%d)" % process.returncode)
            return process.stdout
        top = git("rev-parse", "--show-toplevel").decode("utf-8", "strict").strip()
        if Path(top) != root:
            reject("snapshot path is not exact Git root")
        commit = git("rev-parse", "HEAD").decode("ascii", "strict").strip()
        status_sha = hb.sha(git("-c", "core.fsmonitor=false", "status", "--porcelain=v1", "-uall"))
    if expected_commit is not None and commit != expected_commit:
        reject("Git commit does not match explicitly pinned snapshot")
    return {"inventorySha256": hb.sha(hb.canonical(rows)), "fileCount": len(rows),
            "totalBytes": total, "gitCommit": commit, "gitStatusSha256": status_sha}


def require_unchanged(path, first, expected_commit):
    again = source_inventory(path, expected_commit)
    if first != again:
        reject("source drift / Git status changed across capture or receipt publication")


def check_external_witness(path, reviewed_sha, graph, observation):
    # Structural crosswalk only. It cannot authenticate a process event or
    # promote observed/owned-body bytes into independent Core qualification.
    if not isinstance(reviewed_sha, str) or not re.fullmatch(r"[0-9a-f]{64}", reviewed_sha):
        reject("missing exact reviewed R1-01 witness SHA")
    if path is None:
        reject("missing external R1-01 witness file")
    raw = hb.read_bounded(path, hb.MAX_OBSERVATION)
    if hb.sha(raw) != reviewed_sha:
        reject("external R1-01 witness byte digest differs from reviewed pin")
    witness = hb.strict_json(raw)
    if not isinstance(witness, dict) or set(witness) != {
            "schema", "graphSha256", "observationSha256", "events"
    } or witness["schema"] != WITNESS_SCHEMA:
        reject("unknown external witness schema/fields")
    if (witness["graphSha256"] != hb.hash_file(graph)[0]
            or witness["observationSha256"] != hb.hash_file(observation)[0]):
        reject("external witness graph/observation identity mismatch")
    obs = hb.load(observation, hb.MAX_OBSERVATION)
    events = witness["events"]
    if not isinstance(events, list) or not 1 <= len(events) <= 100:
        reject("missing/oversized R1-01 event mapping")
    index = {}
    for event in events:
        if not isinstance(event, dict) or set(event) != {
                "nodeId", "argvSha256", "compilerSha256", "workingDirectory",
                "eventSha256", "stdoutSha256", "stderrSha256", "exitCode", "eventSource"
        }:
            reject("invalid external event contract")
        if (event["eventSource"] != "R1-01" or type(event["exitCode"]) is not int
                or event["exitCode"] != 0 or any(not isinstance(event[k], str)
                or not re.fullmatch(r"[0-9a-f]{64}", event[k])
                for k in ("eventSha256", "stdoutSha256", "stderrSha256"))):
            reject("invalid R1-01 recorded event or nonzero exit")
        node = event["nodeId"]
        if not isinstance(node, str) or node in index:
            reject("duplicate/invalid R1-01 event mapping")
        index[node] = event
    projects = obs.get("projects")
    if not isinstance(projects, list) or set(index) != {
            project["nodeId"] for project in projects} or len(index) != len(projects):
        reject("R1-01 event coverage differs from observed project graph")
    for project in projects:
        event = index[project["nodeId"]]
        if (event["argvSha256"] != project["argvSha256"]
                or event["compilerSha256"] != project["compiler"]["sha256"]
                or event["workingDirectory"] != project["workingDirectory"]):
            reject("R1-01 event/argv/executable/CWD crosswalk mismatch")
    return reviewed_sha


def capture_one(graph, observation, snapshot, out):
    root = hb.capture(graph, observation, snapshot, str(out))
    if hb.verify(str(out)) != root:
        reject("segmented byte replay changed")
    index = hb.load(str(out / "index.json"), hb.MAX_INDEX)
    rows = index["entries"]
    if len(rows) > CORE_MAX_ROWS:
        reject("Core transition exceeds 8192 entries; no truncation")
    if sum(x["bytes"] for x in rows) > CORE_MAX_BYTES:
        reject("Core transition exceeds 512 MiB; no truncation")
    raw_chunks = [part["sha256"] for row in rows for part in row["chunks"]]
    # The original per-chunk hashes are retained in the LOCAL receipt; the CI
    # should publish only a digest/aggregate count, never original paths/bytes.
    commands = [[x["nodeId"], x["argvSha256"]] for x in index["commands"]]
    return {"closureRootSha256": root, "observedArgvDigestSha256": hb.sha(hb.canonical(commands)),
            "rawChunkSha256": raw_chunks, "chunkCount": len(raw_chunks), "entryCount": len(rows),
            "outputWorkspacePath": str(out), "graphSha256": index["graphSha256"],
            "observationSha256": index["observationSha256"]}


def pair(*, clean_root, dirty_root, clean_graph, dirty_graph,
         clean_observation, dirty_observation, clean_output, dirty_output,
         receipt_output, clean_commit=None, dirty_commit=None,
         require_git=False, require_witness=False,
         clean_witness=None, dirty_witness=None,
         clean_witness_sha256=None, dirty_witness_sha256=None,
         after_clean=None, after_publish=None):
    clean = hb.safe_path(os.path.abspath(clean_root), file=False)
    dirty = hb.safe_path(os.path.abspath(dirty_root), file=False)
    if clean == dirty or hb.inside(clean, dirty) or hb.inside(dirty, clean):
        reject("clean and dirty must be disjoint, separately provisioned snapshots")
    outputs = [new_temp_child(x) for x in (clean_output, dirty_output, receipt_output)]
    if len(set(outputs)) != 3:
        reject("duplicate output workspace")
    for path in outputs:
        if hb.inside(path, clean) or hb.inside(path, dirty):
            reject("output is nested in a Golden source root")
    before = [source_inventory(str(clean), clean_commit), source_inventory(str(dirty), dirty_commit)]
    if require_git and any(x["gitCommit"] is None or x["gitStatusSha256"] is None for x in before):
        reject("real capture requires readable, exact Git HEAD and status")
    if require_git and before[0]["gitStatusSha256"] != hb.sha(b""):
        reject("pinned clean snapshot has tracked/untracked changes")
    if require_git and before[1]["gitStatusSha256"] == hb.sha(b""):
        reject("dirty snapshot lacks an independently visible Git overlay")
    if require_witness:
        check_external_witness(clean_witness, clean_witness_sha256, clean_graph, clean_observation)
        check_external_witness(dirty_witness, dirty_witness_sha256, dirty_graph, dirty_observation)
    if before[0]["inventorySha256"] == before[1]["inventorySha256"]:
        reject("clean and dirty byte inventories identical; overlay not established")
    try:
        c = capture_one(clean_graph, clean_observation, str(clean), outputs[0])
        require_unchanged(str(clean), before[0], clean_commit)
        if after_clean is not None:  # tests only, never CLI
            after_clean()
        d = capture_one(dirty_graph, dirty_observation, str(dirty), outputs[1])
        for root, baseline, expected in zip((clean, dirty), before, (clean_commit, dirty_commit)):
            require_unchanged(str(root), baseline, expected)
        if c["closureRootSha256"] == d["closureRootSha256"]:
            reject("dirty and clean segmented roots identical; no byte-bound difference")
        receipt = {"schema": SCHEMA, "disposition": "unqualified-software-observation",
                   "executionGate": "Blocked: R1-01 executed Csc provenance not independently attested",
                   "clean": {**before[0], **c}, "dirty": {**before[1], **d}}
        outputs[2].mkdir(mode=0o700)
        with (outputs[2] / "receipt.json").open("xb") as stream:
            stream.write(hb.canonical(receipt) + b"\n")
        if after_publish is not None:  # test-only drift injection
            after_publish()
        for root, baseline, expected in zip((clean, dirty), before, (clean_commit, dirty_commit)):
            require_unchanged(str(root), baseline, expected)
        # Raw paths and hashes stay in TEMP receipt, never in CI log output.
        print("HB-INT-02 unqualified paired receipt SHA256=" + hb.hash_file(outputs[2] / "receipt.json")[0])
        print("cleanClosureRootSha256=" + c["closureRootSha256"])
        print("dirtyClosureRootSha256=" + d["closureRootSha256"])
        print("executionGate=Blocked (R1-01 not independently attested)")
        return receipt
    except BaseException:
        for output in outputs:
            if output.is_dir() and not output.is_symlink():
                shutil.rmtree(output)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("clean-root", "dirty-root", "clean-graph", "dirty-graph",
                 "clean-observation", "dirty-observation",
                 "clean-output", "dirty-output", "receipt-output"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--clean-commit", required=True)
    parser.add_argument("--dirty-commit", required=True)
    for name in ("clean-witness", "dirty-witness",
                 "clean-witness-sha256", "dirty-witness-sha256"):
        parser.add_argument("--" + name, required=True)
    args = vars(parser.parse_args())
    pair(**{key.replace("-", "_"): value for key, value in args.items()},
         require_git=True, require_witness=True)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, TypeError, OSError, subprocess.TimeoutExpired, UnicodeError) as error:
        print("HB-INT-02 Blocked: " + str(error), file=sys.stderr)
        sys.exit(2)
