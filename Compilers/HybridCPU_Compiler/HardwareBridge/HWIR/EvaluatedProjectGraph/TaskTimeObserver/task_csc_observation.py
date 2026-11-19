"""Convert bound, live Csc MSBuild task events to existing unqualified v2.

NOT a compiler or a Golden proof. Only an approved isolated TEMP COPY is accepted.
An MSBuild TaskCommandLine event is evidence of a real task, not by itself an OS
execve/working-directory attestation. Every unsupported ambiguity fails closed.
External-observation imports in segmented_csc_capture_v2.py are unchanged.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shlex
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import segmented_csc_capture_v2 as v2

EVENT_SCHEMA = "hybridcpu.hardwarebridge.msbuild-csc-task-events/v1"
MAX_LOG = 4 * 1024 * 1024
MAX_COMMAND = 262144
MAX_TASKS = 32


def reject(message):
    raise ValueError(message)


def under(path, root):
    return path != root and path.is_relative_to(root)


def require_temp_dir(value):
    path = v2.safe_path(os.path.abspath(value), file=False)
    root = v2.safe_path(os.path.abspath(tempfile.gettempdir()), file=False)
    if not under(path, root):
        reject("all observer workspaces must be new isolated TEMP children")
    return path


def read_events(path):
    data = v2.load(str(path), MAX_LOG)
    if not isinstance(data, dict) or set(data) != {"schema", "disposition", "error", "events"}:
        reject("invalid task event envelope")
    if data["schema"] != EVENT_SCHEMA or data["disposition"] != "unqualified-software-observation":
        reject("unsupported event schema/disposition")
    if data["error"] is not None:
        reject("logger refused Csc observation: " + str(data["error"]))
    events = data["events"]
    if not isinstance(events, list) or not 1 <= len(events) <= MAX_TASKS:
        reject("missing/oversized actual Csc task set")
    seen = set()
    for event in events:
        if not isinstance(event, dict) or set(event) != {
                "Id", "ProjectFile", "StartUtc", "FinishUtc", "CommandLine",
                "Succeeded", "CommandEvents"}:
            reject("unexpected task event properties")
        identity = event["Id"]
        if not isinstance(identity, str) or not identity or identity in seen:
            reject("duplicate/missing Csc task execution identity")
        seen.add(identity)
        if event["Succeeded"] is not True or event["CommandEvents"] != 1:
            reject("Csc task failed or did not emit exactly one live command")
        for key in ("ProjectFile", "StartUtc", "FinishUtc", "CommandLine"):
            if not isinstance(event[key], str) or not event[key]:
                reject("incomplete Csc task execution event")
        if len(event["CommandLine"]) > MAX_COMMAND:
            reject("oversized Csc task command")
    return events


def exact_argv(command, compiler):
    # Deliberately restrict to simple POSIX-shell quoting without escapes.
    # Windows command-line parsing requires a separate reviewed implementation.
    if any(ch in command for ch in ("\x00", "\r", "\n", "\\", "'")):
        reject("unsupported shell quoting/escaping in observed Csc command")
    try:
        tokens = shlex.split(command, posix=True)
    except ValueError as ex:
        reject("ambiguous Csc command quoting: " + str(ex))
    if not tokens or len(tokens) > 8194:
        reject("missing/oversized observed Csc argv")
    indices = [i for i, x in enumerate(tokens) if Path(x).name.lower() in ("csc", "csc.dll")]
    if len(indices) != 1:
        reject("Csc tool path not independently bound to task command")
    index = indices[0]
    if Path(tokens[index]).resolve(strict=True) != compiler:
        reject("observed Csc command references unexpected compiler binary")
    if index > 2 or (index and Path(tokens[0]).name not in ("dotnet", "dotnet.exe")):
        reject("unsupported Csc host prefix")
    if index == 2 and tokens[1] != "exec":
        reject("unsupported dotnet exec prefix")
    argv = tokens[index + 1:]
    if any(x.startswith("@") for x in argv):
        reject("response file absent from task-time byte snapshot; expansion blocked")
    if not argv:
        reject("no Csc arguments on observed executable command")
    return argv


def pinned(path):
    return v2.hash_file(path)[0]


def relative_sanitized(path):
    value = path.as_posix()
    return v2.logical(value)


def observe(events_path, baseline_dir, isolated_dir, consumer_project,
            compiler_path, graph_path, observation_path, capture_dir=None, *,
            build_artifacts_dir=None, host_cwd=None):
    baseline = require_temp_dir(baseline_dir)
    isolated = require_temp_dir(isolated_dir)
    if baseline == isolated or under(baseline, isolated) or under(isolated, baseline):
        reject("source and isolated working copy must be disjoint")
    artifacts_root = require_temp_dir(build_artifacts_dir) if build_artifacts_dir else None
    launcher_cwd = require_temp_dir(host_cwd) if host_cwd else None
    if artifacts_root is None or launcher_cwd is None:
        reject("approved build artifact root and real host launcher cwd are required")
    if any(artifacts_root == x or under(artifacts_root, x) or under(x, artifacts_root)
           for x in (baseline, isolated)):
        reject("build artifacts root must be disjoint from source trees")
    compiler = v2.safe_path(os.path.abspath(compiler_path))
    if compiler.name not in ("csc", "csc.dll"):
        reject("only pinned SDK Roslyn Csc tool is supported")
    consumer = v2.safe_path(os.path.abspath(consumer_project))
    if not under(consumer, baseline):
        reject("root project must be an immutable baseline fixture copy")
    entries = read_events(events_path)
    graph_nodes = []
    observed = []
    seen_project = set()
    for event in entries:
        live_project = v2.safe_path(event["ProjectFile"])
        if not under(live_project, isolated):
            reject("Csc task escaped isolated source copy")
        relative_project = live_project.relative_to(isolated)
        original_project = v2.safe_path(str(baseline / relative_project))
        if pinned(live_project) != pinned(original_project):
            reject("project changed between baseline and actual Csc")
        if original_project in seen_project:
            reject("ambiguous project: multiple Csc task executions")
        seen_project.add(original_project)
        argv = exact_argv(event["CommandLine"], compiler)
        # For this subset require all actual Csc paths to be absolute, so an
        # MSBuild task event cannot imply an unproven process working directory.
        cwd = launcher_cwd
        # All Csc input/output file operands must be absolute, so successful
        # transport never depends on an assumed Csc process working directory.
        input_flags = ("/reference:", "/r:", "/analyzer:", "/analyzerconfig:",
                       "/additionalfile:", "/sourcelink:", "/appconfig:",
                       "/out:", "/pdb:", "/refout:", "/doc:", "/errorlog:",
                       "/generatedfilesout:")
        for token in argv:
            low = token.lower()
            prefix = next((x for x in input_flags if low.startswith(x)), None)
            if prefix and not os.path.isabs(token[len(prefix):]):
                reject("relative Csc file option without actual process cwd proof")
            if low.endswith(".cs") and not prefix and not os.path.isabs(token):
                reject("relative Csc source without actual process cwd proof")
        sources, artifacts, generator_dir = v2.command_files(argv, cwd)
        source_rows = []
        original_sources = []
        for value in sources:
            path = v2.safe_path(value)
            if under(path, isolated):
                relative = path.relative_to(isolated)
                original_path = baseline / relative
            elif under(path, artifacts_root):
                relative = path.relative_to(artifacts_root)
                original_path = None
            else:
                reject("Csc source outside approved isolated/project-generated roots")
            if original_path is not None and original_path.is_file():
                v2.safe_path(str(original_path))
                if pinned(path) != pinned(original_path):
                    reject("original source mutated after clone")
                original_sources.append(str(original_path))
                source_rows.append({"path": str(path), "logicalPath": "project/" + relative_sanitized(relative),
                                    "sha256": pinned(path), "origin": "evaluated",
                                    "originalPath": str(original_path)})
            else:
                source_rows.append({"path": str(path), "logicalPath": "msbuild/" + relative_sanitized(relative),
                                    "sha256": pinned(path), "origin": "msbuild-generated", "originalPath": None})
        generated = []
        if generator_dir is not None:
            generated_root = v2.safe_path(generator_dir, file=False)
            if not (under(generated_root, isolated) or under(generated_root, artifacts_root)):
                reject("generated outputs outside approved roots")
            for path in sorted(generated_root.rglob("*.cs")):
                v2.safe_path(str(path))
                generated.append({"path": str(path),
                                  "logicalPath": "generated/" + relative_sanitized(path.relative_to(generated_root)),
                                  "sha256": pinned(path)})
            if len(generated) > 5000:
                reject("generator output count exceeded")
        if any(kind == "analyzer" for kind, _ in artifacts) and not generated:
            reject("missing actual source-generator outputs; generator proof Blocked")
        node_id = str(original_project) + "|Configuration=Debug"
        graph_nodes.append({"id": node_id, "project": str(original_project),
                            "compileFiles": sorted(original_sources),
                            "globalProperties": {"Configuration": "Debug"},
                            "projectAssets": None})
        observed.append({"nodeId": node_id,
                         "role": "primary" if original_project == consumer else "metadata-only",
                         "workingDirectory": str(cwd),
                         "compiler": {"path": str(compiler), "sha256": pinned(compiler)},
                         "projectSha256": pinned(original_project),
                         "filePins": [{"kind": kind, "path": path, "sha256": pinned(path)}
                                      for kind, path in artifacts],
                         "argv": argv, "argvSha256": v2.sha(v2.canonical(argv)),
                         "sources": source_rows, "generatorSources": generated})
    if consumer not in seen_project:
        reject("root project did not execute Csc")
    graph_nodes.sort(key=lambda item: item["id"])
    observed.sort(key=lambda item: item["nodeId"])
    graph = {"schema": v2.GRAPH_SCHEMA, "root": str(consumer), "nodes": graph_nodes}
    graph_bytes = v2.canonical(graph)
    observation = {"schema": v2.OBS_SCHEMA, "graphSha256": v2.sha(graph_bytes), "projects": observed}
    graph_output = Path(os.path.abspath(graph_path))
    obs_output = Path(os.path.abspath(observation_path))
    for dest in (graph_output, obs_output):
        if dest.exists() or dest.is_symlink() or dest.parent != Path(tempfile.gettempdir()):
            reject("graph and observation output must be NEW direct TEMP files")
    graph_output.write_bytes(graph_bytes)
    obs_output.write_bytes(v2.canonical(observation))
    # v2 independently hashes staged bytes, matches source argv, and refuses
    # generated output/file pins and paths it cannot classify.
    v2.build_inputs(str(graph_output), str(obs_output), str(baseline))
    if capture_dir is not None:
        return v2.capture(str(graph_output), str(obs_output), str(baseline), capture_dir)
    return v2.sha(v2.canonical(observation))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("events", "baseline", "isolated", "consumer", "compiler",
                 "graph", "observation"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--capture")
    parser.add_argument("--build-artifacts", required=True)
    parser.add_argument("--host-cwd", required=True)
    a = parser.parse_args()
    print("Unqualified task-time Csc/v2 root=" + observe(
        a.events, a.baseline, a.isolated, a.consumer, a.compiler,
        a.graph, a.observation, a.capture,
        build_artifacts_dir=a.build_artifacts, host_cwd=a.host_cwd))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError, TypeError, json.JSONDecodeError) as exc:
        print("Task-time Csc observation Blocked: " + str(exc), file=sys.stderr)
        sys.exit(2)
