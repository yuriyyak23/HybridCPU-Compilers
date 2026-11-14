"""Compare an isolated diagnostic build's C# compiler inputs with an evaluated graph.

This consumes a diagnostic log rather than running a build or opening the source
tree for writing. Log text is caller-supplied and is not an execution attestation.
"""
import hashlib
import json
import os
import re
import sys
import tempfile
from pathlib import Path

graph_path, log_path, copied_root, output = map(Path, sys.argv[1:])
if output.exists() or output.resolve().parent != Path(tempfile.gettempdir()).resolve():
    raise ValueError("output must be a new direct child of TEMP")
if copied_root.resolve().parent != Path(tempfile.gettempdir()).resolve():
    raise ValueError("isolated checkout must be a direct child of TEMP")
graph_bytes = graph_path.read_bytes()
graph = json.loads(graph_bytes)
if graph["schema"] != "hardwarebridge.evaluated-project-graph/v2":
    raise ValueError("unsupported project graph")
desktop = Path(os.environ["USERPROFILE"]) / "Desktop"
roots = [(desktop / "HybridCPU ISE", copied_root / "HybridCPU ISE"),
         (desktop / "HybridCPU Compilers", copied_root / "HybridCPU Compilers")]

def key(path):
    return os.path.normcase(os.path.abspath(path))

def copied(path):
    source = Path(path)
    for original, clone in roots:
        try:
            return clone / source.relative_to(original)
        except ValueError:
            continue
    raise ValueError("project/source outside approved copied roots: " + str(source))

cache = {}
def digest(path):
    path = Path(path).resolve()
    identity = key(path)
    if identity not in cache:
        # Generated names can exceed the legacy Windows MAX_PATH limit.
        io_path = Path("\\\\?\\" + str(path)) if os.name == "nt" and not str(path).startswith("\\\\?\\") else path
        if not io_path.is_file() or io_path.stat().st_size > 128 * 1024 * 1024:
            raise ValueError("missing or oversized compiler input: " + str(path))
        cache[identity] = hashlib.sha256(io_path.read_bytes()).hexdigest()
    return cache[identity]

nodes = {}
for node in graph["nodes"]:
    name = node["properties"]["assemblyName"] + ".dll"
    if name.lower() in nodes:
        raise ValueError("ambiguous assembly name: " + name)
    nodes[name.lower()] = node

results = []
seen = set()
with log_path.open(encoding="utf-8-sig", errors="strict") as log:
    for line in log:
        if "csc.exe /noconfig" not in line:
            continue
        command = line.split("csc.exe ", 1)[1].strip()
        # The diagnostic Csc command prints arguments using Windows quoting.
        tokens = [token.replace('"', '') for token in re.findall(r'(?:[^\s"]|"[^"]*")+', command)]
        out = [t[5:] for t in tokens if t.startswith("/out:")]
        defines = [t[8:] for t in tokens if t.startswith("/define:")]
        if len(out) != 1 or len(defines) != 1:
            raise ValueError("ambiguous compiler command")
        if "/target:library" not in tokens:
            raise ValueError("only library compilations are supported")
        name = Path(out[0]).name.lower()
        if name not in nodes or name in seen:
            raise ValueError("unexpected or repeated compiler output: " + name)
        seen.add(name)
        node = nodes[name]
        project_dir = copied(node["project"]).parent
        actual = [Path(t) if Path(t).is_absolute() else project_dir / t for t in tokens if t.lower().endswith(".cs") and not t.startswith("/")]
        actual_map = {key(p): p for p in actual}
        expected_map = {key(copied(p)): Path(p) for p in node["compileFiles"]}
        if len(actual_map) != len(actual) or len(expected_map) != len(node["compileFiles"]):
            raise ValueError("duplicate compiler source path")
        missing = sorted(set(expected_map) - set(actual_map))
        generated = sorted(set(actual_map) - set(expected_map))
        changed = [str(p) for k, p in expected_map.items() if k in actual_map and digest(p) != digest(actual_map[k])]
        actual_defines = set(defines[0].split(";"))
        evaluation_defines = set(filter(None, node["properties"]["defineConstants"].split(";")))
        def inputs(prefix):
            result = []
            for value in (t[len(prefix):] for t in tokens if t.startswith(prefix)):
                # Preserve correctness until semantic replay supports reference aliases.
                if prefix == "/reference:" and "=" in value:
                    raise ValueError("aliased references are not yet supported")
                path = value
                full = Path(path) if Path(path).is_absolute() else project_dir / path
                result.append({"path": str(full.resolve()), "sha256": digest(full)})
            return result
        generated_option = [t[len("/generatedfilesout:"):] for t in tokens if t.startswith("/generatedfilesout:")]
        if len(generated_option) > 1:
            raise ValueError("ambiguous generator output directory")
        generated_output = []
        if generated_option:
            directory = Path(generated_option[0])
            directory = directory if directory.is_absolute() else project_dir / directory
            directory = directory.resolve()
            directory.relative_to(copied_root.resolve())
            if directory.exists():
                if directory.is_symlink():
                    raise ValueError("linked generator output directory")
                for path in sorted(directory.rglob("*.cs")):
                    path.resolve().relative_to(directory)
                    if path.is_symlink() or len(generated_output) >= 5000:
                        raise ValueError("linked or excessive generator outputs")
                    generated_output.append({"path": str(path.resolve()), "sha256": digest(path)})
        results.append({"project": node["project"], "assembly": name,
            "commandSha256": hashlib.sha256(command.encode()).hexdigest(),
            "evaluatedSources": len(expected_map), "compilerSources": len(actual_map),
            "missingEvaluatedSources": missing, "changedCopiedSources": changed,
            "compilerSourceFiles": [{"path": str(p.resolve()), "sha256": digest(p), "originalPath": str(expected_map[key(p)]) if key(p) in expected_map else None} for p in actual],
            "additionalCompilerSources": [{"path": str(actual_map[k].resolve()), "sha256": digest(actual_map[k])} for k in generated],
            "emittedGeneratorOutput": generated_output, "generatorOutputRequested": bool(generated_option),
            "compilerDefines": sorted(actual_defines), "missingEvaluatedDefines": sorted(evaluation_defines - actual_defines),
            "languageVersion": next((t[13:] for t in tokens if t.startswith("/langversion:")), None),
            "nullable": next((t[10:] for t in tokens if t.startswith("/nullable:")), "disable"),
            "allowUnsafe": "/unsafe+" in tokens, "checkOverflow": "/checked+" in tokens,
            "references": inputs("/reference:"), "analyzers": inputs("/analyzer:"), "analyzerConfigs": inputs("/analyzerconfig:")})
if seen != set(nodes):
    raise ValueError("diagnostic log does not include every evaluated project")
report = {"schema": "hardwarebridge.isolated-csc-audit/v1", "disposition": "observation-unqualified",
          "graphSha256": hashlib.sha256(graph_bytes).hexdigest(), "diagnosticLogSha256": digest(log_path),
          "projects": results,
          "scope": "Caller-supplied diagnostic commands from a copied tree; hashes and evaluated source membership are checked. Does not attest execution, prove generator completeness or establish semantic/RTL equivalence."}
with output.open("x", encoding="utf-8") as stream:
    json.dump(report, stream, indent=2)
print(f"Observed Csc inputs for {len(results)} projects; no build or write to source tree.")
