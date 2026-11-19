"""Approved TEMP-only two-project real Csc event fixture. NO Golden invocation."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import traceback

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))
import task_csc_observation as observer
import segmented_csc_capture_v2 as v2

SDK = "11.0.100"


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def snapshot(root):
    digest = hashlib.sha256()
    for p in sorted(root.rglob("*")):
        if p.is_symlink():
            raise RuntimeError("linked fixture input forbidden: " + str(p))
        if p.is_file():
            digest.update(p.relative_to(root).as_posix().encode("utf-8"))
            digest.update(bytes.fromhex(sha(p)))
    return digest.hexdigest()


def command(label, args, cwd, env, receipts):
    out = receipts / (label + ".stdout.log")
    err = receipts / (label + ".stderr.log")
    with out.open("xb") as stdout, err.open("xb") as stderr:
        proc = subprocess.run(args, cwd=str(cwd), env=env, stdout=stdout, stderr=stderr,
                              timeout=240, check=False)
    print(label + ": exit=" + str(proc.returncode) +
          " stdoutSHA256=" + sha(out) + " stderrSHA256=" + sha(err), flush=True)
    if proc.returncode:
        print("FAILURE (last 5000 stdout/stderr bytes, no secrets in isolated toy):", file=sys.stderr)
        print(out.read_text(errors="replace")[-5000:], file=sys.stderr)
        print(err.read_text(errors="replace")[-5000:], file=sys.stderr)
        raise RuntimeError(label + " actual command failed")
    return out.read_text(errors="replace")


def assert_reject(label, f):
    try:
        f()
    except (ValueError, KeyError, TypeError, OSError) as ex:
        print("negative " + label + ": Rejected (" + str(ex)[:120] + ")")
        return
    raise RuntimeError("mutation admitted: " + label)


def run():
    repo_root = HERE.parents[4]  # Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/TaskTimeObserver
    original = HERE / "Fixture"
    if not original.is_dir():
        raise RuntimeError("approved two-project fixture absent")
    source_before = snapshot(original)
    # The repo is immutable; all dotnet restore/build and generator output are in TEMP.
    with tempfile.TemporaryDirectory(prefix="hb-csc-tasktime-") as directory:
        temp = Path(directory)
        receipts = Path(os.environ.get("HB_CSC_RECEIPTS_DIR", str(temp / "receipts")))
        if receipts.exists() or receipts.is_symlink():
            raise RuntimeError("receipts output must be new")
        receipts.mkdir(parents=True)
        baseline = temp / "baseline"
        isolated = temp / "isolated"
        shutil.copytree(original, baseline, symlinks=False)
        shutil.copytree(original, isolated, symlinks=False)
        if snapshot(baseline) != snapshot(isolated) or snapshot(baseline) != source_before:
            raise RuntimeError("baseline copy hash mismatch")
        env = os.environ.copy()
        env["DOTNET_CLI_HOME"] = str(temp / "dotnet-home")
        env["NUGET_PACKAGES"] = str(temp / "nuget")
        env["DOTNET_NOLOGO"] = "1"
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
        env["MSBUILDDISABLENODEREUSE"] = "1"
        env["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0"
        version = command("dotnet-version", ["dotnet", "--version"], temp, env, receipts).strip()
        if version != SDK:
            raise RuntimeError("SDK version not pinned: " + version)
        sdk_info = command("dotnet-info", ["dotnet", "--info"], temp, env, receipts)
        bases = re.findall(r"(?m)^\s*Base Path:\s*(\S+)\s*$", sdk_info)
        if len(bases) != 1:
            raise RuntimeError("unambiguous actual SDK Base Path missing")
        compiler = Path(bases[0]) / "Roslyn" / "bincore" / "csc.dll"
        v2.safe_path(str(compiler))
        version_text = command("csc-version", ["dotnet", str(compiler), "/version"], temp, env, receipts).strip()
        print("csc.dll SHA256=" + sha(compiler) + " version=" + version_text)
        logger_project = HERE / "CscTaskLogger.csproj"
        command("build-observer", [
            "dotnet", "build", str(logger_project), "-c", "Release",
            "--artifacts-path", str(temp / "logger-artifacts"),
            "-p:UseSharedCompilation=false", "-m:1", "-nr:false"
        ], temp, env, receipts)
        candidates = [p for p in (temp / "logger-artifacts").rglob("CscTaskLogger.dll")
                      if "bin" in p.parts and "ref" not in p.parts and "refint" not in p.parts]
        if len(candidates) != 1:
            raise RuntimeError("ambiguous logger assembly outputs")
        logger_dll = candidates[0]
        print("logger SHA256=" + sha(logger_dll))
        task_events = temp / "task-events.json"
        env["HB_CSC_OBSERVER_LOG"] = str(task_events)
        project = isolated / "Consumer" / "Consumer.csproj"
        command("build-two-project-fixture", [
            "dotnet", "build", str(project), "-c", "Debug", "-m:1", "-nr:false",
            "--artifacts-path", str(temp / "fixture-artifacts"),
            "-p:UseSharedCompilation=false", "-p:EmitCompilerGeneratedFiles=true",
            "-l:HardwareBridge.CscObservation.CscTaskLogger," + str(logger_dll)
        ], temp, env, receipts)
        print("live-task-events SHA256=" + sha(task_events))
        shutil.copy2(task_events, receipts / "task-events.json")
        events = observer.read_events(str(task_events))
        if len(events) != 2:
            raise RuntimeError("fixture requires exactly two actual Csc task events")
        output = Path(tempfile.gettempdir()) / ("hb-task-capture-" + temp.name)
        graph = Path(tempfile.gettempdir()) / ("hb-task-graph-" + temp.name + ".json")
        obs = Path(tempfile.gettempdir()) / ("hb-task-obs-" + temp.name + ".json")
        try:
            root = observer.observe(
                str(task_events), str(baseline), str(isolated),
                str(baseline / "Consumer" / "Consumer.csproj"),
                str(compiler), str(graph), str(obs), str(output))
            if v2.verify(str(output)) != root:
                raise RuntimeError("capture index replay identity mismatch")
            print("task-v2 graphSHA256=" + sha(graph) + " observationSHA256=" + sha(obs))
            print("task-v2 closureRootSha256=" + root + " indexSHA256=" + sha(output / "index.json"))
            shutil.copy2(graph, receipts / "graph.json")
            shutil.copy2(obs, receipts / "observation.json")
            shutil.copy2(output / "index.json", receipts / "index.json")
            # Independently staged byte replay and negative mutation gates.
            target = isolated / "Consumer" / "Consumer.cs"
            original_bytes = target.read_bytes()
            try:
                target.write_bytes(b"// mutated\n")
                assert_reject("source bytes", lambda: v2.build_inputs(str(graph), str(obs), str(baseline)))
            finally:
                target.write_bytes(original_bytes)
            data = v2.load(str(obs), v2.MAX_OBSERVATION)
            data["projects"][0]["argv"].append("/define:MUTATED")
            changed = Path(tempfile.gettempdir()) / ("hb-task-mutated-" + temp.name + ".json")
            changed.write_bytes(v2.canonical(data))
            try:
                assert_reject("Csc argv", lambda: v2.build_inputs(str(graph), str(changed), str(baseline)))
            finally:
                changed.unlink()
            data = v2.load(str(obs), v2.MAX_OBSERVATION)
            if not any(p["generatorSources"] for p in data["projects"]):
                raise RuntimeError("source generator generated no captured bytes")
            generated = Path(next(x["path"] for p in data["projects"] for x in p["generatorSources"]))
            generated_bytes = generated.read_bytes()
            try:
                generated.write_bytes(b"// mutated generated source\n")
                assert_reject("generated bytes", lambda: v2.build_inputs(str(graph), str(obs), str(baseline)))
            finally:
                generated.write_bytes(generated_bytes)
            data = v2.load(str(obs), v2.MAX_OBSERVATION)
            if not any(p["filePins"] for p in data["projects"]):
                raise RuntimeError("real reference/analyzer pins absent")
            next(p for p in data["projects"] if p["filePins"])["filePins"][0]["sha256"] = "0" * 64
            changed.write_bytes(v2.canonical(data))
            try:
                assert_reject("reference/analyzer pin", lambda: v2.build_inputs(str(graph), str(changed), str(baseline)))
            finally:
                changed.unlink()
            if snapshot(original) != source_before or snapshot(baseline) != source_before:
                raise RuntimeError("fixture original/baseline mutated")
            print("isolated-fixture-source-no-write SHA256=" + source_before)
        finally:
            graph.unlink(missing_ok=True)
            obs.unlink(missing_ok=True)
            shutil.rmtree(output, ignore_errors=True)
    # Golden checkout was NOT present, built, or modified.
    print("Golden-no-write=NotRun (no Golden checkout; fixture only)")


if __name__ == "__main__":
    try:
        run()
    except (Exception, KeyboardInterrupt) as ex:
        print("Task-time fixture Failed/Blocked: " + str(ex), file=sys.stderr)
        traceback.print_exc()
        sys.exit(2)
