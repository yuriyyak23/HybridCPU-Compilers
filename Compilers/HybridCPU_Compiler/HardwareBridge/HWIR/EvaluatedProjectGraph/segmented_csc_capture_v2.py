"""HB-INT-02: bounded, observational Csc byte-closure transport. Never executes MSBuild/Csc.

Input observations MUST be collected in a separately approved, isolated build. This
module checks their bytes and structure; it does not attest an actual compiler run.
"""
import argparse
import hashlib
import json
import os
import re
from pathlib import Path
import shutil
import stat
import sys
import tempfile
import time

SCHEMA = "hybridcpu.hardwarebridge.csc-closure-index/v2"
OBS_SCHEMA = "hybridcpu.hardwarebridge.csc-observation/v2"
GRAPH_SCHEMA = "hardwarebridge.evaluated-project-graph/v2"
CHUNK_SIZE = 1024 * 1024
MAX_FILE = 128 * CHUNK_SIZE
MAX_FILES = 20000
MAX_BYTES = 8 * 1024 * CHUNK_SIZE
MAX_INDEX = 16 * CHUNK_SIZE
MAX_OBSERVATION = 8 * CHUNK_SIZE


def fail(message):
    raise ValueError(message)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("utf-8")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def pairs_unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            fail("duplicate JSON property: " + key)
        result[key] = value
    return result


def _file_state(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_size,
            info.st_mtime_ns, info.st_ctime_ns)


def _is_reparse(info):
    return stat.S_ISLNK(info.st_mode) or bool(
        getattr(info, "st_file_attributes", 0) &
        getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0))


def path_key(path):
    return os.path.normcase(os.path.abspath(os.fspath(path))).casefold()


def safe_path(path, *, file=True):
    raw = os.fspath(path) if isinstance(path, (str, os.PathLike)) else None
    if not isinstance(raw, str) or not raw or not os.path.isabs(raw):
        fail("absolute path required")
    if "\x00" in raw or os.path.normpath(raw) != raw:
        fail("noncanonical/traversing absolute path")
    components = re.split(r"[\\/]" if os.name == "nt" else r"/", raw)
    if any(part in (".", "..") for part in components):
        fail("noncanonical/traversing absolute path")
    if os.name == "nt" and any(p.endswith((" ", ".")) for p in components if p):
        fail("ambiguous Windows path")
    p = Path(raw)
    for ancestor in (p, *p.parents):
        try:
            info = ancestor.lstat()
        except (FileNotFoundError, NotADirectoryError):
            fail("missing input/chunk: " + str(ancestor))
        if _is_reparse(info) or getattr(ancestor, "is_junction", lambda: False)():
            fail("symlink/reparse path: " + str(ancestor))
    info = p.lstat()
    if (file and not stat.S_ISREG(info.st_mode)) or (not file and not stat.S_ISDIR(info.st_mode)):
        fail("unexpected input type: " + str(p))
    if file and not 0 <= info.st_size <= MAX_FILE:
        fail("oversized compiler input: " + str(p))
    return p


def stable_open(path):
    p = safe_path(path)
    before = p.lstat()
    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
    fd = os.open(p, flags)
    try:
        if _file_state(os.fstat(fd)) != _file_state(before):
            fail("file replaced between check and open")
        return p, os.fdopen(fd, "rb"), before
    except BaseException:
        os.close(fd)
        raise


def ensure_stable(p, stream, before):
    if (_file_state(os.fstat(stream.fileno())) != _file_state(before) or
            _file_state(safe_path(p).lstat()) != _file_state(before)):
        fail("concurrent file mutation/replacement: " + str(p))


def read_bounded(path, budget):
    p, stream, before = stable_open(path)
    with stream:
        raw = stream.read(budget + 1)
        if len(raw) > budget:
            fail("oversized input during bounded read")
        ensure_stable(p, stream, before)
    return raw


def strict_json(data):
    def refuse_constant(token):
        fail("nonstandard JSON constant: " + token)
    return json.loads(data, object_pairs_hook=pairs_unique,
                      parse_constant=refuse_constant)


def inside(path, root):
    try:
        Path(path).relative_to(root)
        return True
    except ValueError:
        return False


def logical(value):
    if not isinstance(value, str) or len(value) > 512 or "\\" in value or ":" in value or any(ord(c) < 32 for c in value):
        fail("invalid logical path")
    parts = value.split("/")
    if not parts or any(not p or p in (".", "..") or p.strip() != p for p in parts):
        fail("traversing/noncanonical logical path")
    return value


def load(path, max_size):
    return strict_json(read_bounded(path, max_size))


def hash_file(path):
    p, stream, before = stable_open(path)
    digest = hashlib.sha256()
    total = 0
    with stream:
        while block := stream.read(CHUNK_SIZE):
            total += len(block)
            if total > MAX_FILE:
                fail("oversized file while reading")
            digest.update(block)
        ensure_stable(p, stream, before)
    return digest.hexdigest(), total


def pins_file(path, expected):
    h, size = hash_file(path)
    if h != expected:
        fail("changed/stale file hash: " + str(path))
    return size


def command_files(argv, cwd):
    if not isinstance(argv, list) or not 1 <= len(argv) <= 8192 or any(not isinstance(x, str) or not x or "\x00" in x or len(x) > 8192 for x in argv):
        fail("invalid full Csc argument vector")
    if any(t.startswith("@") for t in argv):
        fail("unexpanded response file; exact Csc argv unavailable")
    required = ("/langversion:", "/define:", "/nullable:", "/target:", "/out:")
    for prefix in required:
        if sum(t.lower().startswith(prefix) for t in argv) != 1:
            fail("missing/duplicate explicit Csc option: " + prefix)
    if not any(t.lower() == "/target:library" for t in argv):
        fail("unsupported Csc output kind")
    # Deny-by-default: unknown file-valued switches could hide unpinned bytes.
    file_input = (("/reference:", "reference"), ("/r:", "reference"),
                  ("/analyzer:", "analyzer"), ("/analyzerconfig:", "analyzer-config"),
                  ("/additionalfile:", "additional-file"), ("/sourcelink:", "source-link"),
                  ("/appconfig:", "app-config"))
    output_options = ("/out:", "/pdb:", "/refout:", "/doc:", "/errorlog:")
    scalar_prefixes = ("/langversion:", "/define:", "/nullable:", "/target:",
                       "/debug:", "/platform:", "/warn:", "/nowarn:", "/warnaserror:",
                       "/warnaserror+:", "/warnaserror-:",
                       "/features:", "/pathmap:", "/usings:", "/moduleassemblyname:",
                       "/main:", "/baseaddress:", "/filealign:", "/preferreduilang:",
                       "/instrument:", "/checksumalgorithm:", "/errorendlocation:")
    scalar_flags = {"/errorreport:prompt", "/noconfig", "/fullpaths", "/utf8output", "/nostdlib", "/nostdlib+",
                    "/nostdlib-", "/checked", "/checked+", "/checked-", "/unsafe",
                    "/unsafe+", "/unsafe-", "/optimize", "/optimize+", "/optimize-",
                    "/deterministic", "/deterministic+", "/deterministic-", "/parallel+",
                    "/parallel-", "/publicsign+", "/publicsign-", "/warnaserror+",
                    "/warnaserror-", "/debug", "/debug+", "/highentropyva+", "/highentropyva-"}
    sources = []
    artifacts = []
    generator_dir = None
    for token in argv:
        lower = token.lower()
        # Paths have to be admitted as source tokens, including POSIX absolute /tmp/A.cs.
        if lower.endswith(".cs") and (not token.startswith(("/", "-")) or
                (os.path.isabs(token) and not re.match(r"^/[a-z][a-z0-9+-]*:", lower))):
            sources.append(str(safe_path(str(Path(token) if os.path.isabs(token) else cwd / token))))
            continue
        kind = next((k for prefix, k in file_input if lower.startswith(prefix)), None)
        if kind:
            path = token.split(":", 1)[1]
            if kind == "reference" and "=" in path:
                fail("reference aliases require a separate versioned contract")
            p = str(Path(path) if os.path.isabs(path) else cwd / path)
            artifacts.append((kind, str(safe_path(p))))
        elif lower.startswith("/generatedfilesout:"):
            if generator_dir is not None:
                fail("duplicate generatedfilesout")
            path = token.split(":", 1)[1]
            generator_dir = str(safe_path(str(Path(path) if os.path.isabs(path) else cwd / path), file=False))
        elif any(lower.startswith(prefix) for prefix in output_options):
            # Outgoing files are not read here; their destinations still must be isolated.
            path = token.split(":", 1)[1]
            out = Path(path) if os.path.isabs(path) else cwd / path
            if not inside(Path(os.path.abspath(out)), Path(os.path.abspath(tempfile.gettempdir()))):
                fail("Csc output path is not isolated in TEMP")
        elif lower in scalar_flags or any(lower.startswith(prefix) for prefix in scalar_prefixes):
            pass  # byte-independent scalars still retained in full argv digest
        else:
            fail("unsupported/unclassified Csc argument: " + token[:80])
    if not sources or len({path_key(x) for x in sources}) != len(sources):
        fail("missing/duplicate Csc source arguments")
    if len({(kind, path_key(path)) for kind, path in artifacts}) != len(artifacts):
        fail("duplicate Csc file arguments")
    if any(x[0] == "analyzer" for x in artifacts) and generator_dir is None:
        fail("generator/analyzer output capture missing")
    return sources, artifacts, generator_dir


def build_inputs(graph_path, observation_path, golden_root):
    graph_bytes = read_bounded(graph_path, 4 * CHUNK_SIZE)
    obs_bytes = read_bounded(observation_path, MAX_OBSERVATION)
    graph = strict_json(graph_bytes)
    obs = strict_json(obs_bytes)
    if not isinstance(graph, dict) or not isinstance(obs, dict):
        fail("graph/observation must be JSON objects")
    if graph.get("schema") != GRAPH_SCHEMA or obs.get("schema") != OBS_SCHEMA or obs.get("graphSha256") != sha(graph_bytes):
        fail("graph/Csc observation version or identity mismatch")
    root = safe_path(golden_root, file=False)
    graph_nodes = {n["id"]: n for n in graph["nodes"]}
    if not graph_nodes or len(graph_nodes) != len(graph["nodes"]) or len(graph_nodes) > 100:
        fail("ambiguous/oversized evaluated graph")
    projects = obs.get("projects")
    if not isinstance(projects, list) or len(projects) != len(graph_nodes):
        fail("Csc observations do not cover the graph")
    rows = []
    commands = []
    originals = []
    seen_nodes = set()

    def add(node_id, kind, role, logical_path, path, expected, original=None):
        logical(logical_path)
        if role not in ("primary", "owned-body", "metadata-only"):
            fail("unknown ownership role")
        if kind not in ("csc-source", "generator-source", "reference", "analyzer", "analyzer-config", "additional-file", "source-link", "app-config", "csc-binary", "project", "assets"):
            fail("unknown input kind")
        if kind not in ("csc-source", "generator-source") and role != "metadata-only":
            fail("metadata input promoted to executable body")
        if not isinstance(expected, str) or len(expected) != 64 or any(c not in "0123456789abcdef" for c in expected):
            fail("invalid input SHA-256 pin")
        size = pins_file(path, expected)
        if original is not None:
            original_path = safe_path(original)
            if not inside(original_path, root) or hash_file(original_path) != (expected, size):
                fail("Golden original and isolated Csc source bytes differ")
            if os.path.samefile(original_path, path):
                fail("Csc source is a hardlink/alias of Golden original, not isolated")
            originals.append((str(original_path), expected, size))
        rows.append({"nodeId": node_id, "kind": kind, "role": role, "logicalPath": logical_path,
                     "path": str(path), "sha256": expected, "bytes": size})

    for project in projects:
        nid = project["nodeId"]
        if nid in seen_nodes or nid not in graph_nodes:
            fail("duplicate/unknown Csc project identity")
        seen_nodes.add(nid)
        node = graph_nodes[nid]
        role = project["role"]
        if role not in ("primary", "owned-body", "metadata-only") or (node["project"] == graph["root"]) != (role == "primary"):
            fail("inconsistent root/owned/metadata project role")
        cwd = safe_path(project["workingDirectory"], file=False)
        temp_root = Path(os.path.abspath(tempfile.gettempdir()))
        if not inside(cwd, temp_root) or cwd == temp_root:
            fail("Csc observation must use an isolated TEMP project directory")
        if inside(cwd, root):
            fail("Csc build observation points inside Golden tree")
        argv = project["argv"]
        if sha(canonical(argv)) != project["argvSha256"]:
            fail("changed Csc option/argument vector")
        sources, artifacts, generator_dir = command_files(argv, cwd)
        if any(not inside(Path(src), temp_root) for src in sources) or (generator_dir is not None and not inside(Path(generator_dir), temp_root)):
            fail("Csc sources/generator output must be isolated under TEMP")
        out = next(t.split(":", 1)[1] for t in argv if t.lower().startswith("/out:"))
        output_path = Path(out) if os.path.isabs(out) else cwd / out
        if inside(Path(os.path.abspath(output_path)), root):
            fail("Csc output path points into Golden tree")
        if len(sources) != len(project["sources"]):
            fail("missing or extra Csc source map")
        source_map = {str(safe_path(s["path"])): s for s in project["sources"]}
        if (len(source_map) != len(project["sources"])
                or len({path_key(p) for p in source_map}) != len(source_map)
                or len({path_key(p) for p in sources}) != len(sources)
                or set(source_map) != set(sources)):
            fail("Csc source map differs from argv or contains aliases")
        expected_originals = {str(safe_path(p)) for p in node["compileFiles"]}
        if len(expected_originals) != len(node["compileFiles"]) or len(
                {path_key(p) for p in expected_originals}) != len(expected_originals):
            fail("duplicate evaluated Compile path")
        seen_originals = set()
        for src in sources:
            s = source_map[src]
            origin = s["origin"]
            if origin == "evaluated":
                original = str(s["originalPath"])
                if original not in expected_originals or original in seen_originals:
                    fail("unexpected/duplicate evaluated source")
                seen_originals.add(original)
            elif origin == "msbuild-generated" and s.get("originalPath") is None:
                original = None
            else:
                fail("unsupported source origin")
            add(nid, "csc-source", role, s["logicalPath"], src, s["sha256"], original)
        if seen_originals != expected_originals:
            fail("Csc invocation missing evaluated Compile items")
        generated = project["generatorSources"]
        if not isinstance(generated, list) or len(generated) > 5000:
            fail("invalid generated-source inventory")
        if generator_dir is None and generated:
            fail("generator outputs without generatedfilesout")
        if generator_dir is not None:
            actual = set()
            for folder, dirs, files in os.walk(generator_dir, followlinks=False):
                for d in dirs:
                    safe_path(str(Path(folder) / d), file=False)
                for f in files:
                    path = Path(folder) / f
                    safe_path(str(path))
                    if path.suffix == ".cs":
                        actual.add(str(path))
                        if len(actual) > 5000:
                            fail("excessive generator-emitted source inventory")
            declared = {str(safe_path(s["path"])) for s in generated}
            if (len(declared) != len(generated)
                    or len({path_key(p) for p in declared}) != len(declared)
                    or actual != declared):
                fail("missing/extra emitted generator source")
        for s in generated:
            add(nid, "generator-source", role, s["logicalPath"], s["path"], s["sha256"])
        file_pins = project["filePins"]
        if not isinstance(file_pins, list) or len(file_pins) != len(artifacts):
            fail("missing Csc reference/analyzer byte pins")
        pin_map = {(x["kind"], str(safe_path(x["path"]))): x["sha256"] for x in file_pins}
        if (len(pin_map) != len(file_pins) or len(pin_map) != len(artifacts)
                or len({(kind, path_key(path)) for kind, path in pin_map}) != len(pin_map)
                or set(pin_map) != set(artifacts)):
            fail("changed Csc input paths/file pin identities")
        for kind, path in artifacts:
            artifact_hash = pin_map[(kind, path)]
            name = Path(path).name
            add(nid, kind, "metadata-only", "metadata/" + kind + "/" + logical(name) + "/" + artifact_hash, path, artifact_hash)
        compiler = project["compiler"]
        add(nid, "csc-binary", "metadata-only", "tools/csc", compiler["path"], compiler["sha256"])
        project_path = str(safe_path(node["project"]))
        if node["project"] == graph["root"] and graph.get("rootProjectSha256") and graph["rootProjectSha256"] != project["projectSha256"]:
            fail("root evaluated project changed since graph observation")
        add(nid, "project", "metadata-only", "msbuild/project", project_path, project["projectSha256"])
        assets = node.get("projectAssets")
        if assets:
            add(nid, "assets", "metadata-only", "msbuild/assets", assets["path"], assets["sha256"])
        commands.append({"nodeId": nid, "projectRole": role, "argvSha256": project["argvSha256"],
                         "argv": argv, "globalProperties": node["globalProperties"],
                         "workingDirectory": str(cwd), "generatorOutputDirectory": generator_dir})
    if len(rows) > MAX_FILES:
        fail("source closure exceeds file count budget")
    rows.sort(key=lambda x: (x["nodeId"], x["kind"], x["logicalPath"]))
    keys = [(r["nodeId"].casefold(), r["kind"], r["logicalPath"].casefold()) for r in rows]
    if len(set(keys)) != len(keys):
        fail("duplicate ordered closure identity")
    if sum(r["bytes"] for r in rows) > MAX_BYTES:
        fail("closure byte budget exceeded")
    commands.sort(key=lambda x: x["nodeId"])
    return rows, commands, originals, sha(graph_bytes), sha(obs_bytes)


def capture(graph, observation, golden_root, out_dir, after_copy=None, *, timeout_seconds=300):
    if not isinstance(timeout_seconds, (int, float)) or not 0 < timeout_seconds <= 3600:
        fail("invalid bounded capture timeout")
    deadline = time.monotonic() + timeout_seconds
    def check_timeout():
        if time.monotonic() >= deadline:
            fail("capture timeout")
    output = Path(os.path.abspath(out_dir))
    temp = Path(os.path.abspath(tempfile.gettempdir()))
    if output.parent != temp or output.exists() or output.is_symlink():
        fail("output must be a NEW direct TEMP child directory")
    safe_path(str(output.parent), file=False)
    rows, commands, originals, graph_sha, obs_sha = build_inputs(graph, observation, golden_root)
    check_timeout()
    output.mkdir(mode=0o700)
    try:
        chunk_no = 0
        for row in rows:
            check_timeout()
            file_hash = hashlib.sha256()
            parts = []
            source_path, source, original_state = stable_open(row["path"])
            with source:
                while block := source.read(CHUNK_SIZE):
                    check_timeout()
                    name = "chunk-%08d.bin" % chunk_no
                    chunk_no += 1
                    with (output / name).open("xb") as sink:
                        sink.write(block)
                    parts.append({"name": name, "bytes": len(block), "sha256": sha(block)})
                    file_hash.update(block)
                ensure_stable(source_path, source, original_state)
            if file_hash.hexdigest() != row["sha256"] or sum(p["bytes"] for p in parts) != row["bytes"]:
                fail("input mutated while capturing " + row["logicalPath"])
            row["chunks"] = parts
        if after_copy is not None:  # test hook, not exposed by CLI
            after_copy()
        check_timeout()
        for row in rows:
            check_timeout()
            if hash_file(row["path"]) != (row["sha256"], row["bytes"]):
                fail("input mutated after capture " + row["logicalPath"])
        for original, digest, length in originals:
            check_timeout()
            if hash_file(original) != (digest, length):
                fail("Golden source mutated during capture")
        payload = {"schema": SCHEMA, "disposition": "unqualified-software-observation", "graphSha256": graph_sha,
                   "observationSha256": obs_sha, "chunkSizeLimit": CHUNK_SIZE, "commands": commands, "entries": rows}
        encoded = canonical(payload)
        index = {**payload, "closureRootSha256": sha(encoded)}
        index_bytes = canonical(index) + b"\n"
        if len(index_bytes) > MAX_INDEX:
            fail("v2 index exceeds budget")
        with (output / "index.json").open("xb") as stream:
            stream.write(index_bytes)
        check_timeout()
        verify(output)
        check_timeout()
        return index["closureRootSha256"]
    except BaseException:
        shutil.rmtree(output)
        raise


def verify(out_dir):
    base = safe_path(os.path.abspath(out_dir), file=False)
    index_path = base / "index.json"
    index = load(str(index_path), MAX_INDEX)
    expected_root = index.pop("closureRootSha256")
    if index.get("schema") != SCHEMA or index.get("disposition") != "unqualified-software-observation" or sha(canonical(index)) != expected_root:
        fail("unknown/tampered v2 closure root")
    if index.get("chunkSizeLimit") != CHUNK_SIZE:
        fail("unsupported chunk limit")
    commands = index["commands"]
    if not isinstance(commands, list) or len(commands) > 100 or not commands:
        fail("missing/oversized Csc observation commands")
    command_ids = [c["nodeId"] for c in commands]
    if command_ids != sorted(command_ids) or len(set(command_ids)) != len(command_ids):
        fail("duplicate/reordered Csc commands")
    for command in commands:
        if sha(canonical(command["argv"])) != command["argvSha256"]:
            fail("changed Csc option in segmented index")
    command_roles = {x["nodeId"]: x["projectRole"] for x in commands}
    rows = index["entries"]
    if not isinstance(rows, list) or len(rows) > MAX_FILES:
        fail("invalid file inventory")
    keys = [(x["nodeId"], x["kind"], x["logicalPath"]) for x in rows]
    if keys != sorted(keys) or len(set((a.casefold(), b, c.casefold()) for a, b, c in keys)) != len(keys):
        fail("duplicate/reordered closure entries")
    expected_chunks = set()
    next_chunk = 0
    total_bytes = 0
    for row in rows:
        logical(row["logicalPath"])
        if row["nodeId"] not in command_roles or (row["kind"] in ("csc-source", "generator-source") and row["role"] != command_roles[row["nodeId"]]):
            fail("invalid project/source role link")
        if row["role"] not in ("primary", "owned-body", "metadata-only") or (row["kind"] not in ("csc-source", "generator-source") and row["role"] != "metadata-only"):
            fail("metadata promoted to executable source")
        h = hashlib.sha256()
        size = 0
        for part in row["chunks"]:
            name = "chunk-%08d.bin" % next_chunk
            next_chunk += 1
            if part["name"] != name or not 0 < part["bytes"] <= CHUNK_SIZE:
                fail("missing/reordered/noncanonical chunk")
            blob = read_bounded(str(base / name), CHUNK_SIZE)
            if len(blob) != part["bytes"] or sha(blob) != part["sha256"]:
                fail("truncated/changed chunk")
            h.update(blob)
            size += len(blob)
            expected_chunks.add(name)
        if size != row["bytes"] or h.hexdigest() != row["sha256"]:
            fail("changed file digest")
        total_bytes += size
        if total_bytes > MAX_BYTES:
            fail("oversized closure")
    actual_files = {p.name for p in base.iterdir()}
    if actual_files != expected_chunks | {"index.json"}:
        fail("extra/missing closure chunk")
    return expected_root


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    cap = sub.add_parser("capture")
    for flag in ("graph", "observation", "golden-root", "output"):
        cap.add_argument("--" + flag, required=True)
    cap.add_argument("--timeout-seconds", type=float, default=300)
    check = sub.add_parser("verify")
    check.add_argument("--output", required=True)
    args = parser.parse_args()
    if args.command == "capture":
        result = capture(args.graph, args.observation, args.golden_root, args.output, timeout_seconds=args.timeout_seconds)
    else:
        result = verify(args.output)
    print("HB-INT-02 unqualified software closure SHA256=" + result)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, TypeError, OSError, json.JSONDecodeError) as error:
        print("HB-INT-02 Blocked: " + str(error), file=sys.stderr)
        sys.exit(2)
