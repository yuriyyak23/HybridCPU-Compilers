#!/usr/bin/env python3
"""Opt-in Bridge->Core *candidate transport* replay, NOT independent qualification.

The only v2 parser/consumer is the Core-owned BridgeSegmentIndexImporterV2.
The external observer must actually run to produce graph, Csc observation and
PE/PDB evidence; pre-written JSON is permitted only with --scope fixture.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import time

from r2_02_core_mutations import MUTATIONS, prepare_fixture_mutation

SHA = re.compile(r"^[0-9a-f]{64}$")
GIT = re.compile(r"^[0-9a-f]{40}$")
MAX_FILES = 25000
MAX_BYTES = 8 * 1024**3
MAX_LOG = 16 * 1024**2
ROOT = Path(__file__).resolve().parents[2]   # HardwareBridge
EVAL = ROOT / "HWIR" / "EvaluatedProjectGraph"
HOST = Path(__file__).resolve().parent / "CoreReaderHost"

class Blocked(Exception):
    pass


def require(ok, reason):
    if not ok:
        raise Blocked(reason)


def canonical(obj):
    return json.dumps(obj, ensure_ascii=True, separators=(",", ":"), sort_keys=True).encode("ascii")


def sha_file(path):
    d = hashlib.sha256()
    size = 0
    with open(path, "rb") as src:
        while True:
            block = src.read(1024 * 1024)
            if not block:
                break
            size += len(block)
            require(size <= MAX_BYTES, "input exceeds byte budget")
            d.update(block)
    return d.hexdigest(), size


def reject_links(path, *, require_existing=True):
    path = Path(os.path.abspath(path))
    for part in (path, *path.parents):
        if not part.exists() and not part.is_symlink():
            if require_existing:
                raise Blocked("missing required path: " + str(part))
            continue
        mode = part.lstat().st_mode
        attributes = getattr(part.lstat(), "st_file_attributes", 0)
        require(not stat.S_ISLNK(mode) and not getattr(part, "is_junction", lambda: False)()
                and not (attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)),
                "symlink/reparse input: " + str(part))
    return path


def within(path, parent):
    try:
        Path(path).relative_to(parent)
        return True
    except ValueError:
        return False


def json_file(path, max_bytes=1024 * 1024):
    path = reject_links(path)
    require(path.is_file() and path.stat().st_size <= max_bytes, "JSON file missing/oversized")
    def pairs(p):
        obj = {}
        for k, v in p:
            require(k not in obj, "duplicate JSON field: " + k)
            obj[k] = v
        return obj
    return json.loads(path.read_bytes(), object_pairs_hook=pairs)


def git(root, args):
    result = subprocess.run(["git", "-C", str(root), *args], capture_output=True, timeout=45)
    require(result.returncode == 0, "Git check failed: " + " ".join(args))
    return result.stdout.decode("utf-8", errors="strict").strip()


def inventory(root):
    """Content-based full tree audit, without storing Golden source bytes in receipts."""
    root = reject_links(root)
    require(root.is_dir(), "Golden directory missing")
    count, total = 0, 0
    items = []
    for current, dirs, files in os.walk(root, followlinks=False):
        dirs[:] = sorted(d for d in dirs if d != ".git")
        for d in dirs:
            reject_links(Path(current) / d)
        for name in sorted(files):
            path = reject_links(Path(current) / name)
            require(path.is_file(), "Golden nonregular file")
            rel = path.relative_to(root).as_posix()
            require("\t" not in rel and "\n" not in rel, "noncanonical inventory path")
            digest, size = sha_file(path)
            count += 1
            total += size
            require(count <= MAX_FILES and total <= MAX_BYTES, "Golden inventory limit exceeded")
            items.append((rel, size, digest.upper()))
    h = hashlib.sha256()
    for path, size, digest in sorted(items, key=lambda x: x[0]):
        h.update(f"{path}\t{size}\t{digest}\n".encode("utf-8"))
    return {"head": git(root, ["rev-parse", "HEAD"]),
            "status": git(root, ["status", "--porcelain=v1", "--untracked-files=all"]),
            "files": count, "bytes": total, "inventorySha256": h.hexdigest()}


def byte_tree_inventory(root):
    """Pure byte-tree SHA audit; staged outputs have no Git history."""
    root = reject_links(root)
    require(root.is_dir(), "missing staged byte tree")
    count, total, rows = 0, 0, []
    for current, dirs, files in os.walk(root, followlinks=False):
        dirs[:] = sorted(dirs)
        for name in dirs + sorted(files):
            reject_links(Path(current) / name)
        for name in sorted(files):
            file = Path(current) / name
            require(file.is_file(), "nonregular staged input")
            relative = file.relative_to(root).as_posix()
            require("\t" not in relative and "\n" not in relative, "noncanonical staged path")
            digest, size = sha_file(file)
            count += 1
            total += size
            require(count <= MAX_FILES and total <= MAX_BYTES, "staged byte inventory budget exceeded")
            rows.append((relative, size, digest.upper()))
    h = hashlib.sha256()
    for name, size, digest in sorted(rows):
        h.update(f"{name}\t{size}\t{digest}\n".encode("utf-8"))
    return {"files": count, "bytes": total, "inventorySha256": h.hexdigest()}


def kill_tree(proc):
    if proc.poll() is not None:
        return
    try:
        if os.name == "nt":
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10)
        else:
            os.killpg(proc.pid, signal.SIGKILL)
    except (OSError, subprocess.TimeoutExpired):
        proc.kill()


def run(label, argv, cwd, logs, timeout=900, env=None):
    require(0 < timeout <= 3600, "invalid timeout")
    require(isinstance(argv, list) and argv and all(isinstance(x, str) and x and len(x) < 8192 for x in argv),
            "invalid ordered argv")
    # System-installed shims (/usr/bin/python3, /usr/bin/dotnet) may be links.
    # Resolve the executable to the final regular file, hash those actual bytes,
    # and EXECUTE THAT PATH; never allow this exception for source/artifacts.
    executable = reject_links(os.path.realpath(argv[0])); require(executable.is_file(), "missing executable: " + argv[0])
    exe_sha, exe_bytes = sha_file(executable)
    result = {"label": label, "argv": argv, "actualExecutable": str(executable), "cwd": str(cwd),
              "executableSha256": exe_sha, "executableBytes": exe_bytes,
              "exit": None, "stdoutSha256": None, "stderrSha256": None, "status": "NotRun"}
    out = logs / (label + ".stdout.bin")
    err = logs / (label + ".stderr.bin")
    try:
        with out.open("xb") as stdout, err.open("xb") as stderr:
            proc = subprocess.Popen([str(executable), *argv[1:]], cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                                    stdout=stdout, stderr=stderr, start_new_session=(os.name != "nt"),
                                    creationflags=(subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0))
            deadline = time.monotonic() + timeout
            try:
                while True:
                    try:
                        result["exit"] = proc.wait(timeout=.1)
                        break
                    except subprocess.TimeoutExpired:
                        if time.monotonic() < deadline and out.stat().st_size <= MAX_LOG and err.stat().st_size <= MAX_LOG:
                            continue
                        result["status"] = "Blocked"
                        kill_tree(proc)
                        result["exit"] = proc.wait()
                        break
            except BaseException:
                kill_tree(proc)
                proc.wait()
                raise
    except OSError as e:
        result["status"] = "NotRun"
        raise Blocked("command launch failure: " + type(e).__name__) from e
    for field, p in (("stdoutSha256", out), ("stderrSha256", err)):
        result[field], n = sha_file(p)
        result[field[:-6] + "Bytes"] = n
        require(n <= MAX_LOG, "command log size exceeded: " + label)
    if result["status"] == "NotRun":
        result["status"] = "Passed" if result["exit"] == 0 else "Failed"
    return result


def check_sha(value, desc):
    require(isinstance(value, str) and SHA.fullmatch(value), "missing/invalid reviewed " + desc)
    return value


def clean_copy(src, dst):
    require(not dst.exists(), "copy destination exists")
    def copy(s, d):
        reject_links(s)
        shutil.copyfile(s, d, follow_symlinks=False)
    shutil.copytree(src, dst, ignore=shutil.ignore_patterns(".git"),
                    symlinks=False, copy_function=copy)


def main(args):
    bridge = reject_links(args.bridge)
    core = reject_links(args.core)
    golden = reject_links(args.golden)
    out = reject_links(args.output, require_existing=False)
    for tree in (bridge, core, golden):
        require(tree.is_dir(), "missing approved checkout")
        require(not within(out, tree) and not within(tree, out), "output overlaps source checkout")
    require(out.parent == Path(tempfile.gettempdir()), "output must be new direct child of host TEMP")
    require(not out.exists(), "output root must be fresh")
    require(GIT.fullmatch(args.bridge_sha or "") and GIT.fullmatch(args.core_sha or ""), "reviewed exact SHA required")
    require(git(bridge, ["rev-parse", "HEAD"]) == args.bridge_sha, "wrong Bridge SHA")
    require(git(core, ["rev-parse", "HEAD"]) == args.core_sha, "wrong Core SHA")
    require(not git(core, ["status", "--porcelain=v1", "--untracked-files=all"]),
            "dirty pinned Core checkout; HEAD does not describe worktree bytes")
    require(not git(bridge, ["status", "--porcelain=v1", "--untracked-files=all"]),
            "dirty pinned Bridge checkout; HEAD does not describe worktree bytes")
    check_sha(args.closure_sha, "Csc closure SHA")
    check_sha(args.roots_sha, "roots manifest SHA")
    require(re.fullmatch(r"11\.0\.[0-9A-Za-z.+-]+", args.sdk), "explicit .NET 11 SDK version required")
    require(args.scope in ("fixture", "golden"), "unsupported evidence scope")
    roots = reject_links(args.roots)
    require(sha_file(roots)[0] == args.roots_sha, "reviewed roots manifest byte mismatch")
    before = inventory(golden)
    require(before["head"] == args.golden_sha, "wrong Golden HEAD")
    require(args.scope != "golden" or not before["status"], "dirty Golden checkout")
    # No execution may take place in the Golden checkout, including dotnet restore.
    require(args.scope != "golden" or all((args.observer_argv, args.observer_argv_sha, args.observer_exe_sha, args.graph_sha, args.observation_sha)), "real R1-01 observed command and reviewed graph/observation/exec pins missing")
    require(args.scope != "golden" or all([args.pe, args.pdb, args.pe_sha, args.pdb_sha,
                                               args.facts_argv, args.facts_argv_sha, args.facts_exe_sha, args.facts, args.facts_sha]),
            "real PE/PDB and compiler-method facts receipts are missing")
    receipt = {"schema": "hybridcpu.hardwarebridge.cross-repo-replay/v1", "scope": args.scope,
               "disposition": "Blocked", "bridgeSha": args.bridge_sha, "coreSha": args.core_sha,
               "goldenHead": args.golden_sha, "sdkPin": args.sdk,
               "reviewedClosureSha256": args.closure_sha, "rootsSha256": args.roots_sha,
               "goldenBefore": before, "goldenAfter": None, "sourceUnchanged": None,
               "stages": {k: "NotRun" for k in ("R1-01-observer", "R1-02-capture", "R1-03-pe-pdb",
                                                "R2-01-stage", "R2-02-core-reader")},
               "commands": [], "blocker": None}
    out.mkdir(mode=0o700)
    logs = out / "logs"; logs.mkdir()
    env = dict(os.environ)
    env.update({"TMP": str(out.parent), "TEMP": str(out.parent), "TMPDIR": str(out.parent),
                "DOTNET_CLI_HOME": str(out / "dotnet-home"),
                "NUGET_PACKAGES": str(out / "nuget-packages"),
                "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1"})
    try:
        receipt["bridgeBefore"] = inventory(bridge)
        receipt["coreBefore"] = inventory(core)
        (out / "global.json").write_text(json.dumps({"sdk": {"version": args.sdk,
            "rollForward": "disable", "allowPrerelease": True}}), encoding="utf-8")
        dotnet = reject_links(args.dotnet)
        sdk_probe = run("dotnet-version", [str(dotnet), "--version"], out, logs, args.timeout, env)
        receipt["commands"].append(sdk_probe)
        require(sdk_probe["status"] == "Passed" and
                (logs / "dotnet-version.stdout.bin").read_text().strip() == args.sdk,
                "wrong or missing pinned .NET SDK")
        isolated = out / "isolated-source"; clean_copy(golden, isolated)
        # Never silently treat a pre-written graph/observation as a Csc execution receipt.
        if args.observer_argv:
            cmd = json_file(args.observer_argv)
            require(type(cmd) is list and cmd and all(type(x) is str for x in cmd), "observer argv shape")
            require(not any(str(golden) in x for x in cmd), "observer must not target Golden source")
            require(sha_file(args.observer_argv)[0] == check_sha(args.observer_argv_sha, "observer argv"), "observer command changed")
            require(sha_file(cmd[0])[0] == check_sha(args.observer_exe_sha, "observer executable"), "observer binary changed")
            if args.scope == "golden":
                require(not any(Path(p).exists() for p in (args.graph, args.observation, args.pe, args.pdb)), "observer outputs exist before execution (stale witness)")
            observed = run("R1-01-observer", cmd, isolated, logs, args.timeout, env)
            receipt["commands"].append(observed)
            require(observed["status"] == "Passed", "actual R1-01 Csc observation command did not pass")
            receipt["stages"]["R1-01-observer"] = "Passed"
        else:
            receipt["stages"]["R1-01-observer"] = "NotRun"
            require(args.scope == "fixture", "missing real Golden observer")
        graph = reject_links(args.graph); obs = reject_links(args.observation)
        if args.scope == "golden":
            require(within(graph, out) and within(obs, out), "observer outputs must reside under external root")
            require(sha_file(graph)[0] == check_sha(args.graph_sha, "graph"), "graph witness differs from pin")
            require(sha_file(obs)[0] == check_sha(args.observation_sha, "actual Csc observation"), "Csc argv/options witness differs from reviewed pin")
        python = reject_links(sys.executable)
        # Legacy producer requires a NEW direct child of TEMP. Move into the
        # single evidence root only after successful byte-pinned staging.
        capture_tmp = out.parent / (out.name + "-capture")
        require(not capture_tmp.exists(), "capture sibling already exists")
        capture = out / "segmented-capture"
        capture_cmd = [str(python), str(EVAL / "segmented_csc_capture_v2.py"), "capture",
                       "--graph", str(graph), "--observation", str(obs),
                       "--golden-root", str(golden), "--output", str(capture_tmp),
                       "--timeout-seconds", str(args.timeout)]
        result = run("R1-02-capture", capture_cmd, out, logs, args.timeout, env)
        receipt["commands"].append(result)
        require(result["status"] == "Passed", "segmented capture process failed")
        index = json_file(capture_tmp / "index.json", 16 * 1024 * 1024)
        require(index.get("closureRootSha256") == args.closure_sha, "reviewed Csc closure digest mismatch")
        capture_tmp.rename(capture)
        receipt["stages"]["R1-02-capture"] = "Passed"
        receipt["captureIndexSha256"] = sha_file(capture / "index.json")[0]
        # Ordered producer claims are *unqualified*, never independent admission.
        require(type(index.get("entries")) is list and len(index["entries"]) <= 8192, "unbounded capture entries")
        receipt["unqualifiedCaptureRows"] = [
            {"ordinal": i, "kind": r["kind"], "role": r["role"],
             "logicalPathSha256": hashlib.sha256(r["logicalPath"].encode("utf-8")).hexdigest(),
             "sha256": r["sha256"], "bytes": r["bytes"],
             "chunks": [{"sha256": ch["sha256"], "bytes": ch["bytes"]} for ch in r["chunks"]]}
            for i, r in enumerate(index["entries"])]
        if args.scope == "golden":
            for p, digest in ((args.pe, args.pe_sha), (args.pdb, args.pdb_sha)):
                check_sha(digest, "PE/PDB SHA")
                path = reject_links(p)
                require(within(path, out), "PE/PDB must be isolated build outputs")
                require(sha_file(path)[0] == digest, "stale/wrong PE/PDB bytes")
            factcmd = json_file(args.facts_argv)
            require(type(factcmd) is list and factcmd, "PE/PDB facts command missing")
            require(sha_file(args.facts_argv)[0] == check_sha(args.facts_argv_sha, "facts command"), "facts invocation changed")
            require(sha_file(factcmd[0])[0] == check_sha(args.facts_exe_sha, "facts executable"), "facts binary changed")
            require(not Path(args.facts).exists(), "compiler facts existed before execution")
            result = run("R1-03-pe-pdb", factcmd, isolated, logs, args.timeout, env)
            receipt["commands"].append(result)
            require(result["status"] == "Passed", "real PE/PDB facts command failed")
            for p, digest in ((args.pe, args.pe_sha), (args.pdb, args.pdb_sha)):
                require(sha_file(p)[0] == digest, "PE/PDB bytes changed during facts replay")
            require(within(reject_links(args.facts), out) and
                    sha_file(args.facts)[0] == check_sha(args.facts_sha, "compiler facts"),
                    "compiler facts payload was not pinned")
            receipt["stages"]["R1-03-pe-pdb"] = "Passed"
        else:
            receipt["stages"]["R1-03-pe-pdb"] = "NotRun"
        stage = out / "core-workspace"
        stage_tmp = out.parent / (out.name + "-stage")
        require(not stage_tmp.exists(), "stage sibling already exists")
        cmd = [str(python), str(EVAL / "stage_core_v2.py"), "stage", "--bundle", str(capture),
               "--reviewed-capture", args.closure_sha, "--roots", str(roots),
               "--reviewed-roots", args.roots_sha, "--output", str(stage_tmp), "--timeout", str(args.timeout)]
        result = run("R2-01-stage", cmd, out, logs, args.timeout, env)
        receipt["commands"].append(result)
        require(result["status"] == "Passed", "canonical stage process failed")
        stage_tmp.rename(stage)
        receipt["stages"]["R2-01-stage"] = "Passed"
        index_path = stage / "bridge-index.json"
        stage_index = json_file(index_path)
        require(stage_index["Schema"] == "circuitweave.bridge-source-closure-index/v2", "wrong Core schema")
        digest = sha_file(index_path)[0].upper()
        closure = stage_index["ClosureSha256"]
        receipt["coreIndexSha256"] = digest
        receipt["coreClosureSha256"] = closure
        receipt["rootSelectors"] = stage_index["Roots"]
        receipt["coreIndexChunks"] = [
            {"ordinal": i, "path": d["Path"], "sha256": d["Sha256"],
             "bytes": d["Bytes"], "rows": d["Rows"]}
            for i, d in enumerate(stage_index["Chunks"])]
        # Only Core's genuine importer can decide acceptance/refusal of staged bytes.
        # Compile the disposable test host ONCE, never inside the pinned Core checkout.
        stage_before = byte_tree_inventory(stage)
        receipt["stagedBefore"] = stage_before
        build_core = out / "core-build"; clean_copy(core, build_core)
        host = out / "core-reader-host"; clean_copy(HOST, host)
        project = host / "CoreReaderHost.csproj"
        project.write_text(project.read_text().replace(
            "__CORE_FRONTEND_PROJECT__",
            (build_core / "src/CircuitWeave.Frontend.Roslyn/CircuitWeave.Frontend.Roslyn.csproj").as_posix()), encoding="utf-8")
        built = run("R2-02-build-core-reader",
                    [str(dotnet), "build", str(project), "--configuration", "Release", "--nologo"],
                    out, logs, args.timeout, env)
        receipt["commands"].append(built)
        require(built["status"] == "Passed", "disposable Core reader host build failed")
        assembly = reject_links(host / "bin" / "Release" / "net11.0" / "CoreReaderHost.dll")
        require(assembly.is_file(), "missing compiled CoreReaderHost assembly")
        assembly_sha = sha_file(assembly)[0]
        receipt["coreReaderAssemblySha256"] = assembly_sha
        receipt["stages"]["R2-02-core-reader"] = "Failed"  # until positive and every required negative verify
        cmd = [str(dotnet), str(assembly), str(index_path), str(stage), closure, digest]
        result = run("R2-02-core-reader", cmd, out, logs, args.timeout, env)
        receipt["commands"].append(result)
        require(result["status"] == "Passed", "Core reader rejected actual Bridge workspace")
        response = json_file(out / "core-reader-result.json")
        require(response.get("state") == "Candidate" and
                response.get("code") == "CWINT03_V2_UNQUALIFIED_CLOSURE" and
                response.get("indexSha256") == digest and response.get("closureSha256") == closure,
                "Core reader did not return pinned unqualified candidate receipt")
        if args.scope == "fixture":
            require(isinstance(response.get("rows"), int) and response["rows"] >= 1001,
                    "Core reader did not import all 1001+ staged fixture rows")
        receipt["coreReader"] = response
        receipt["negativeCoreMatrix"] = []
        if args.scope == "fixture":
            # A negative is meaningful ONLY after actual Core Candidate, using
            # separate isolated copies. A process error or stale result is NOT a
            # successful rejection. Every diagnostic code must match pinned Core.
            cases = out / "r2-02-negative"; cases.mkdir()
            for name in MUTATIONS:
                case = cases / name; case.mkdir()
                workspace = case / "core-workspace"
                clean_copy(stage, workspace)
                index_pin, closure_pin, expected_code = prepare_fixture_mutation(workspace, name)
                require(sha_file(assembly)[0] == assembly_sha, "Core host binary drift")
                negative = run("R2-02-negative-" + name,
                               [str(dotnet), str(assembly), str(workspace / "bridge-index.json"),
                                str(workspace), closure_pin, index_pin],
                               out, logs, args.timeout, env)
                receipt["commands"].append(negative)
                verdict = json_file(case / "core-reader-result.json")
                require(negative["status"] == "Failed" and negative["exit"] == 1 and
                        verdict.get("state") != "Candidate" and
                        verdict.get("code") == expected_code,
                        "Core negative mismatch: " + name + " expected " + expected_code +
                        " got " + str(verdict.get("code")) + " / exit " + str(negative["exit"]))
                receipt["negativeCoreMatrix"].append({
                    "mutation": name, "expectedCode": expected_code,
                    "actualCode": verdict["code"], "actualState": verdict["state"],
                    "coreExecutableSha256": assembly_sha, "expectedRefusal": "Passed"})
        receipt["stagedAfter"] = byte_tree_inventory(stage)
        require(receipt["stagedBefore"] == receipt["stagedAfter"],
                "Core reader or negative test modified original staged workspace")
        require(sha_file(assembly)[0] == assembly_sha, "Core host assembly changed after replay")
        receipt["stages"]["R2-02-core-reader"] = "Passed"
        receipt["disposition"] = "CandidateTransportOnly" if args.scope == "golden" else "FixtureTransportOnly"
    except BaseException as ex:
        receipt["blocker"] = str(ex)[:2000]
        if isinstance(ex, KeyboardInterrupt):
            receipt["blocker"] = "cancelled"
        if not isinstance(ex, (Blocked, KeyboardInterrupt, OSError, ValueError, RuntimeError)):
            receipt["blocker"] = type(ex).__name__ + ": " + receipt["blocker"]
    finally:
        # Avoid leaking partial producer outputs into the shared temporary root.
        for fragment in (out.parent / (out.name + "-capture"), out.parent / (out.name + "-stage")):
            if fragment.exists() and fragment.is_dir() and not fragment.is_symlink():
                shutil.rmtree(fragment)
        receipt["bridgeAfter"] = inventory(bridge)
        receipt["coreAfter"] = inventory(core)
        if (receipt.get("bridgeBefore") != receipt["bridgeAfter"] or
                receipt.get("coreBefore") != receipt["coreAfter"]):
            receipt["disposition"] = "Blocked"
            receipt["blocker"] = "pinned Bridge/Core checkout byte/Git inventory changed"
        after = inventory(golden)
        receipt["goldenAfter"] = after
        receipt["sourceUnchanged"] = before == after
        if not receipt["sourceUnchanged"]:
            receipt["disposition"] = "Blocked"
            receipt["blocker"] = "Golden no-write content/git inventory changed"
        (out / "receipt.json").write_bytes(canonical(receipt))
        print(json.dumps({"scope": args.scope, "disposition": receipt["disposition"],
                          "stages": receipt["stages"], "sourceUnchanged": receipt["sourceUnchanged"],
                          "blocker": receipt["blocker"]}, sort_keys=True))
    return 0 if receipt["disposition"] in ("FixtureTransportOnly", "CandidateTransportOnly") else 2


def cli():
    p = argparse.ArgumentParser(description=__doc__)
    for name in ("bridge", "bridge-sha", "core", "core-sha", "golden", "golden-sha",
                 "output", "graph", "observation", "closure-sha", "roots", "roots-sha",
                 "sdk", "dotnet"):
        p.add_argument("--" + name, required=True)
    p.add_argument("--scope", required=True, choices=("fixture", "golden"))
    p.add_argument("--observer-argv")
    p.add_argument("--observer-argv-sha")
    p.add_argument("--observer-exe-sha")
    p.add_argument("--observation-sha")
    p.add_argument("--graph-sha")
    p.add_argument("--pe"); p.add_argument("--pdb"); p.add_argument("--pe-sha")
    p.add_argument("--pdb-sha"); p.add_argument("--facts-argv"); p.add_argument("--facts")
    p.add_argument("--facts-sha"); p.add_argument("--facts-argv-sha")
    p.add_argument("--facts-exe-sha"); p.add_argument("--timeout", type=int, default=900)
    a = p.parse_args()
    try:
        return main(a)
    except (Blocked, OSError, ValueError, subprocess.TimeoutExpired) as e:
        print("BLOCKED cross-repo replay preflight: " + str(e), file=sys.stderr)
        return 2

if __name__ == "__main__":
    sys.exit(cli())
