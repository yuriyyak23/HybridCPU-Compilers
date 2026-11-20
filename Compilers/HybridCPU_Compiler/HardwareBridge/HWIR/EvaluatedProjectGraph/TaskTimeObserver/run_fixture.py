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
import time
import copy

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))
import task_csc_observation as observer
import R1_01_LinuxExecutionWitness as r1
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


def inventory(root):
    rows = {}
    for p in sorted(root.rglob("*")):
        if p.is_symlink():
            raise RuntimeError("linked fixture input forbidden: " + str(p))
        if p.is_file():
            rows[p.relative_to(root).as_posix()] = sha(p)
    return rows


def command(label, args, cwd, env, receipts, *, trace_prefix=None, strace_executable=None):
    out = receipts / (label + ".stdout.log")
    err = receipts / (label + ".stderr.log")
    if trace_prefix is not None:
        if not strace_executable:
            r1.refuse("strace executable is unavailable; cannot run positive v2")
        actual_args = [str(strace_executable), *r1.strace_args(trace_prefix, args)]
    else:
        actual_args = args
    started = time.monotonic()
    with out.open("xb") as stdout, err.open("xb") as stderr:
        proc = subprocess.run(actual_args, cwd=str(cwd), env=env, stdout=stdout, stderr=stderr,
                              timeout=240, check=False)
    duration = round(time.monotonic() - started, 3)
    print(label + ": exit=" + str(proc.returncode) + " durationSeconds=" + str(duration) +
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
    source_manifest_before = inventory(original)
    # The repo is immutable; all dotnet restore/build and generator output are in TEMP.
    with tempfile.TemporaryDirectory(prefix="hb-csc-tasktime-") as directory:
        temp = Path(directory)
        receipts = Path(os.environ.get("HB_CSC_RECEIPTS_DIR", str(temp / "receipts")))
        if receipts.exists() or receipts.is_symlink():
            raise RuntimeError("receipts output must be new")
        receipts.mkdir(parents=True)
        (receipts / "R1_01_toy_source_before.json").write_text(
            json.dumps({"schema": "hybridcpu.hardwarebridge.toy-no-write-inventory/v1",
                        "aggregateSha256": source_before,
                        "files": source_manifest_before}, sort_keys=True))
        baseline = temp / "baseline"
        isolated = temp / "isolated"
        shutil.copytree(original, baseline, symlinks=False)
        shutil.copytree(original, isolated, symlinks=False)
        if snapshot(baseline) != snapshot(isolated) or snapshot(baseline) != source_before:
            raise RuntimeError("baseline copy hash mismatch")
        # The repo intentionally tracks GeneratedAnswer.g.cs. Remove ONLY the
        # disposable copy before compilation; its committed bytes can never
        # masquerade as freshly emitted Roslyn generator output.
        stale_generated = isolated / "Consumer" / "generated"
        if stale_generated.is_symlink() or not stale_generated.is_dir():
            r1.refuse("expected committed generator directory missing/linked")
        committed_generated = [p for p in stale_generated.rglob("*.cs") if p.is_file()]
        if not committed_generated:
            r1.refuse("tracked stale generator test input missing")
        shutil.rmtree(stale_generated)
        print("discarded tracked generated toy files from isolated copy=" + str(len(committed_generated)))
        env = os.environ.copy()
        env["DOTNET_CLI_HOME"] = str(temp / "dotnet-home")
        env["NUGET_PACKAGES"] = str(temp / "nuget")
        env["DOTNET_NOLOGO"] = "1"
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
        env["MSBUILDDISABLENODEREUSE"] = "1"
        env["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0"
        if sys.platform != "linux":
            r1.refuse("positive OS witness only implemented on Linux; Windows NotRun")
        strace = shutil.which("strace")
        if not strace:
            r1.refuse("strace unavailable on runner; no OS process attestation")
        strace = str(Path(strace).resolve(strict=True))
        strace_sha = sha(strace)
        strace_version = command("strace-version", [strace, "--version"], temp, env, receipts).splitlines()[0]
        print("OS tracer sha256=" + strace_sha + " version=" + strace_version)
        dotnet = shutil.which("dotnet")
        if not dotnet:
            r1.refuse("dotnet tool unavailable")
        dotnet = str(Path(dotnet).resolve(strict=True))
        version = command("dotnet-version", [dotnet, "--version"], temp, env, receipts).strip()
        if version != SDK:
            raise RuntimeError("SDK version not pinned: " + version)
        sdk_info = command("dotnet-info", [dotnet, "--info"], temp, env, receipts)
        bases = re.findall(r"(?m)^\s*Base Path:\s*(\S+)\s*$", sdk_info)
        if len(bases) != 1:
            raise RuntimeError("unambiguous actual SDK Base Path missing")
        compiler = Path(bases[0]) / "Roslyn" / "bincore" / "csc"
        managed = compiler.parent / "csc.dll"
        v2.safe_path(str(compiler))
        v2.safe_path(str(managed))
        version_text = command("csc-version", [str(compiler), "/version"], temp, env, receipts).strip()
        compiler_sha_before = sha(compiler)
        managed_sha_before = sha(managed)
        print("actual csc apphost SHA256=" + compiler_sha_before + " paired csc.dll SHA256=" + managed_sha_before + " version=" + version_text)
        logger_project = HERE / "CscTaskLogger.csproj"
        command("build-observer", [
            dotnet, "build", str(logger_project), "-c", "Release",
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
        trace_dir = temp / "os-trace"
        trace_dir.mkdir()
        trace_prefix = trace_dir / "trace"
        build_argv = [
            dotnet, "build", str(project), "-c", "Debug", "-m:1", "-nr:false",
            "--artifacts-path", str(temp / "fixture-artifacts"),
            "-p:UseSharedCompilation=false", "-p:EmitCompilerGeneratedFiles=true",
            "-l:HardwareBridge.CscObservation.CscTaskLogger," + str(logger_dll)
        ]
        command("build-two-project-fixture", build_argv, temp, env, receipts,
                trace_prefix=trace_prefix, strace_executable=strace)
        if sha(strace) != strace_sha or sha(compiler) != compiler_sha_before or sha(managed) != managed_sha_before:
            r1.refuse("tracer/Csc apphost/managed Roslyn executable SHA drifted")
        shutil.copytree(trace_dir, receipts / "os-trace")
        print("live-task-events SHA256=" + sha(task_events))
        shutil.copy2(task_events, receipts / "task-events.json")
        events = observer.read_events(str(task_events))
        if len(events) != 2:
            raise RuntimeError("fixture requires exactly two actual Csc task events")
        # This is an approved, newly created toy fixture with no confidential inputs.
        # Show exact live task-command prefixes for investigating unsupported
        # MSBuild task conventions; full raw bytes remain in the uploaded receipt.
        for idx, event in enumerate(events):
            print("toy live Csc event " + str(idx) + " command prefix=" +
                  repr(event["CommandLine"][:512]), flush=True)
            import shlex
            tokens = shlex.split(event["CommandLine"])
            switches = sorted({t.split(":", 1)[0].lower() for t in tokens
                               if re.match(r"^/[A-Za-z][A-Za-z0-9+\-]*(?::|$)", t)})
            print("toy Csc observed option names: " + ",".join(switches), flush=True)
        output_pins = []
        artifacts = temp / "fixture-artifacts"
        for file in sorted(artifacts.rglob("*")):
            if file.suffix.lower() not in (".dll", ".pdb") or not file.is_file():
                continue
            r1.clean_path(file)
            if file.stat().st_size > 128 * 1024 * 1024 or len(output_pins) >= 64:
                r1.refuse("emitted PE/PDB byte/count budget exceeded")
            output_pins.append({"kind": "pe" if file.suffix.lower() == ".dll" else "pdb",
                                "path": str(file), "sha256": sha(file), "bytes": file.stat().st_size})
        r1.verify_output_pins(output_pins)
        print("observed emitted PE/PDB pins=" + str(len(output_pins)))
        output = Path(tempfile.gettempdir()) / ("hb-task-capture-" + temp.name)
        graph = Path(tempfile.gettempdir()) / ("hb-task-graph-" + temp.name + ".json")
        obs = Path(tempfile.gettempdir()) / ("hb-task-obs-" + temp.name + ".json")
        companion_path = Path(tempfile.gettempdir()) / ("hb-r1-01-exec-" + temp.name + ".json")
        try:
            root = observer.observe(
                str(task_events), str(baseline), str(isolated),
                str(baseline / "Consumer" / "Consumer.csproj"),
                str(compiler), str(graph), str(obs), str(output),
                build_artifacts_dir=str(temp / "fixture-artifacts"), host_cwd=str(temp),
                trace_dir=str(trace_dir), launch_argv=build_argv,
                compiler_sha_before=compiler_sha_before, companion_path=str(companion_path),
                emitted_outputs=output_pins,
                build_stdout_path=str(receipts / "build-two-project-fixture.stdout.log"),
                build_stderr_path=str(receipts / "build-two-project-fixture.stderr.log"))
            if v2.verify(str(output)) != root:
                raise RuntimeError("capture index replay identity mismatch")
            print("task-v2 graphSHA256=" + sha(graph) + " observationSHA256=" + sha(obs))
            print("task-v2 closureRootSha256=" + root + " indexSHA256=" + sha(output / "index.json"))
            shutil.copy2(graph, receipts / "graph.json")
            shutil.copy2(obs, receipts / "observation.json")
            shutil.copy2(companion_path, receipts / "R1_01_os_execution_receipt.json")
            os_receipt = v2.load(str(companion_path), v2.MAX_OBSERVATION)["osExecution"]
            task_vectors = {e["Id"]: [str(compiler), *observer.exact_argv(e["CommandLine"], compiler)]
                            for e in events}
            proof_args = dict(launch_cwd=temp, launch_argv=build_argv,
                              compiler=compiler, event_argv=task_vectors,
                              expected_compiler_sha=compiler_sha_before)
            r1.gate_receipt(os_receipt, trace_dir, **proof_args)
            mutated = copy.deepcopy(os_receipt)
            first = next(iter(mutated["processes"].values()))
            first["cwd"] = str(temp)
            assert_reject("actual child cwd", lambda: r1.gate_receipt(mutated, trace_dir, **proof_args))
            altered = copy.deepcopy(os_receipt)
            altered["compilerSha256"] = "0" * 64
            assert_reject("OS compiler SHA", lambda: r1.gate_receipt(altered, trace_dir, **proof_args))
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
            # Copying emitted PE/PDB content with the same filename is never enough.
            output_file = Path(output_pins[0]["path"])
            output_bytes = output_file.read_bytes()
            try:
                output_file.write_bytes(b"R1-01 mutated emitted output")
                assert_reject("emitted PE/PDB copy", lambda: r1.verify_output_pins(output_pins))
            finally:
                output_file.write_bytes(output_bytes)
            r1.verify_output_pins(output_pins)
            # Final inventory is also checked in the cleanup block on failure.
            print("toy transport/OS-mutation gates completed")
        finally:
            source_after = snapshot(original)
            baseline_after = snapshot(baseline)
            after_manifest = inventory(original)
            (receipts / "R1_01_toy_source_after.json").write_text(
                json.dumps({"schema": "hybridcpu.hardwarebridge.toy-no-write-inventory/v1",
                            "aggregateSha256": source_after,
                            "files": after_manifest}, sort_keys=True))
            print("toy-original-before SHA256=" + source_before +
                  " after SHA256=" + source_after +
                  " baselineAfter SHA256=" + baseline_after)
            if (source_after != source_before or baseline_after != source_before
                    or after_manifest != source_manifest_before
                    or inventory(baseline) != source_manifest_before):
                raise RuntimeError("toy original/baseline source no-write inventory drift")
            graph.unlink(missing_ok=True)
            obs.unlink(missing_ok=True)
            companion_path.unlink(missing_ok=True)
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
