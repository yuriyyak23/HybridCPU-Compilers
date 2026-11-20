"""R1-01 bounded Linux strace/ptrace witness for a real, isolated Csc task.

Not a trusted remote attestation. A trace is collected by a separate OS process,
not MSBuild. Cwd is reconstructed from fork/clone and successful chdir syscalls;
unsupported syscalls, truncation, ancestry or response files BLOCK the v2 gate.
Only the approved two-project disposable TEMP fixture may run this collector.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import tempfile

SCHEMA = "hybridcpu.hardwarebridge.csc-os-execution-receipt/v1"
MAX_TRACE_FILES = 2048
MAX_TRACE_BYTES = 32 * 1024 * 1024
MAX_ARGV_BYTES = 262144
MAX_ARGS = 8192
MAX_EVENTS = 200000
PID_FILE = re.compile(r"^trace\.([1-9][0-9]{0,9})$")
LINE = re.compile(r"^(\d{10,12}\.\d{6}) (.*)$")
C_QUOTED = r'"(?:\\.|[^"\\])*"'
EXEC = re.compile(r"^execve\((" + C_QUOTED + r"), (\[[^\]]*\]), .+\) = 0$")
CHDIR = re.compile(r"^chdir\((" + C_QUOTED + r")\) = 0$")
FORK = re.compile(r"(?:clone3?|fork|vfork)\(.*\)\s+=\s+([1-9][0-9]{0,9})$")
EXIT = re.compile(r"^\+\+\+ exited with (\d+) \+\+\+$")


class Blocked(ValueError):
    """Typed inability to prove an OS-level execution claim."""


def refuse(reason):
    raise Blocked("R1-01 OS witness Blocked: " + reason)


def digest(path):
    h = hashlib.sha256()
    with Path(path).open("rb") as stream:
        while block := stream.read(1024 * 1024):
            h.update(block)
    return h.hexdigest()


def clean_path(path, *, directory=False):
    p = Path(os.path.abspath(path))
    if not p.is_absolute():
        refuse("non-absolute path")
    for item in (p, *p.parents):
        try:
            mode = item.lstat().st_mode
        except OSError:
            refuse("missing path or symlink ancestor")
        if stat.S_ISLNK(mode):
            refuse("symlink in evidence path")
    mode = p.stat().st_mode
    if not (stat.S_ISDIR(mode) if directory else stat.S_ISREG(mode)):
        refuse("wrong evidence type")
    return p


def temp_child(path):
    path = clean_path(path, directory=True)
    temp = clean_path(tempfile.gettempdir(), directory=True)
    if not path.is_relative_to(temp) or path == temp:
        refuse("evidence outside approved TEMP child")
    return path


def strace_args(trace_prefix, command):
    """Use exactly this immutable syscall subset, no environment/secret dumps."""
    if not command or any(not x or '\x00' in x for x in command):
        refuse("invalid launcher argv")
    return ["-f", "-ff", "-ttt", "-s", "262144", "-o", str(trace_prefix),
            "-e", "trace=execve,execveat,chdir,fchdir,clone,clone3,fork,vfork",
            "--", *command]


def _decode_json(value, role):
    try:
        return json.loads(value)
    except (ValueError, TypeError):
        refuse("unparseable or truncated strace " + role)


def _parse_trace(trace_dir):
    root = temp_child(trace_dir)
    files = sorted(root.iterdir())
    if not files or len(files) > MAX_TRACE_FILES:
        refuse("missing or excessive traced processes")
    all_events = []
    file_hashes = []
    total_bytes = 0
    for file in files:
        match = PID_FILE.fullmatch(file.name)
        if match is None:
            refuse("unexpected trace file")
        file = clean_path(file)
        total_bytes += file.stat().st_size
        if total_bytes > MAX_TRACE_BYTES:
            refuse("trace byte budget exceeded")
        file_hashes.append({"file": file.name, "sha256": digest(file), "bytes": file.stat().st_size})
        pid = int(match.group(1))
        try:
            lines = file.read_text(encoding="utf-8", errors="strict").splitlines()
        except UnicodeError:
            refuse("trace is not UTF-8")
        pending = None
        for row in lines:
            m = LINE.fullmatch(row)
            if not m:
                refuse("unparsed syscall row / missing timestamp")
            stamp, payload = m.groups()
            if payload.endswith(" <unfinished ...>"):
                if pending is not None:
                    refuse("nested unfinished strace syscall")
                pending = (stamp, payload[:-len(" <unfinished ...>")])
                continue
            if payload.startswith("<... "):
                resumed = re.fullmatch(r"<\.\.\. (\w+) resumed>(.*)", payload)
                if pending is None or resumed is None or not pending[1].startswith(resumed.group(1) + "("):
                    refuse("unmatched resumed strace syscall")
                stamp, partial = pending
                payload = partial + resumed.group(2)
                pending = None
            if payload.startswith("execveat(") or payload.startswith("fchdir("):
                refuse("execveat/fchdir unsupported without independent fd tracking")
            if payload.startswith("execve(") and not payload.endswith("= 0"):
                continue
            if payload.startswith("execve("):
                ex = EXEC.fullmatch(payload)
                if not ex or len(payload) > MAX_ARGV_BYTES:
                    refuse("truncated/ambiguous execve argv")
                exe = _decode_json(ex.group(1), "executable")
                argv = _decode_json(ex.group(2), "argv")
                if not isinstance(exe, str) or not isinstance(argv, list) or not (1 <= len(argv) <= MAX_ARGS) or any(not isinstance(x, str) or not x or '\x00' in x for x in argv):
                    refuse("invalid OS argv")
                all_events.append((stamp, pid, "exec", (exe, argv)))
            elif payload.startswith("chdir("):
                c = CHDIR.fullmatch(payload)
                if c:
                    name = _decode_json(c.group(1), "chdir")
                    if not isinstance(name, str) or not name or '\x00' in name:
                        refuse("invalid chdir path")
                    all_events.append((stamp, pid, "chdir", name))
                elif payload.endswith("= 0"):
                    refuse("ambiguous successful chdir")
            elif any(x in payload for x in ("clone(", "clone3(", "fork(", "vfork(")):
                child = FORK.search(payload)
                if child:
                    all_events.append((stamp, pid, "fork", (int(child.group(1)), "CLONE_FS" in payload)))
                elif re.search(r"\)\s+=\s+[1-9][0-9]*$", payload):
                    refuse("unparsed successful process creation")
            else:
                e = EXIT.fullmatch(payload)
                if e:
                    all_events.append((stamp, pid, "exit", int(e.group(1))))
                elif "+++ killed by" in payload or "+++ exited" in payload:
                    refuse("signalled/ambiguous process exit")
            if len(all_events) > MAX_EVENTS:
                refuse("syscall event budget exceeded")
        if pending is not None:
            refuse("incomplete syscall at trace EOF")
    return all_events, file_hashes, {int(PID_FILE.fullmatch(x.name).group(1)) for x in files}


def verify(trace_dir, *, launch_cwd, launch_argv, compiler, event_argv, expected_compiler_sha):
    """Reparse raw trace, reconstruct Csc child cwd, bind one OS child per task.

    event_argv: {MSBuild task identity: exact full argv [csc, /options...]}.
    Compiling event data is *never* itself used as process-level proof.
    """
    temp_child(launch_cwd)
    root_cwd = str(clean_path(launch_cwd, directory=True))
    compiler_path = str(clean_path(compiler))
    if digest(compiler_path) != expected_compiler_sha:
        refuse("compiler SHA changed since pre-execution pin")
    events, traces, pids = _parse_trace(trace_dir)
    if not isinstance(launch_argv, list) or not launch_argv or not all(isinstance(x, str) for x in launch_argv):
        refuse("launcher argv missing")
    if not isinstance(event_argv, dict) or not event_argv or len(event_argv) > 32:
        refuse("task-event vector missing")
    births = {}
    executions = {}
    exits = {}
    for stamp, pid, kind, value in events:
        if kind == "fork":
            child, shared = value
            if child in births or child == pid:
                refuse("ambiguous duplicated process ancestry")
            births[child] = (pid, stamp, shared)
        elif kind == "exec":
            executions.setdefault(pid, []).append((stamp, value))
        elif kind == "exit":
            if pid in exits:
                refuse("duplicate process exits")
            exits[pid] = (stamp, value)
    if any(child not in pids or parent not in pids for child, (parent, _, _) in births.items()):
        refuse("incomplete fork ancestry")
    roots = pids - births.keys()
    if len(roots) != 1:
        refuse("untraced/ambiguous process parent")
    root = next(iter(roots))
    if root not in executions:
        refuse("launcher OS execve absent")
    if executions[root][0][1][1] != launch_argv:
        refuse("OS launcher argv mismatches actual requested subprocess")
    if str(clean_path(executions[root][0][1][0])) != str(clean_path(launch_argv[0])):
        refuse("launcher executable differs from pinned path")

    # CLONE_FS aliases the same kernel fs_struct for parent and child. Replay
    # the ordered syscalls in groups; a normal fork copies its cwd at that time.
    fs_group = {root: root}
    group_cwd = {root: root_cwd}
    witnessed_cwd = {}
    csc_times = {stamp for rows in executions.values()
                 for stamp, (exe, _) in rows if os.path.abspath(exe) == compiler_path}
    if any(kind == "chdir" and stamp in csc_times for stamp, _, kind, _ in events):
        refuse("concurrent chdir and Csc execve with ambiguous timestamp order")
    priority = {"fork": 0, "chdir": 1, "exec": 2, "exit": 3}
    for stamp, pid, kind, value in sorted(events, key=lambda item: (item[0], priority[item[2]], item[1])):
        if kind == "fork":
            child, shared = value
            if pid not in fs_group or child in fs_group:
                refuse("OS child activity before observable fork / duplicate PID")
            if shared:
                fs_group[child] = fs_group[pid]
            else:
                fs_group[child] = child
                group_cwd[child] = group_cwd[fs_group[pid]]
        elif kind == "chdir":
            if pid not in fs_group:
                refuse("unowned chdir process")
            group = fs_group[pid]
            candidate = Path(value) if os.path.isabs(value) else Path(group_cwd[group]) / value
            group_cwd[group] = str(clean_path(candidate, directory=True))
        elif kind == "exec":
            if pid not in fs_group:
                refuse("OS process without parent proof")
            current = group_cwd[fs_group[pid]]
            witnessed_cwd[(pid, stamp)] = current
            # Linux execve unshares CLONE_FS. Preserve the inherited cwd,
            # but subsequent chdir in this process must not change siblings.
            private_group = ("exec", pid, stamp)
            fs_group[pid] = private_group
            group_cwd[private_group] = current

    csc = []
    for pid, rows in executions.items():
        for stamp, (exe, argv) in rows:
            if os.path.abspath(exe) != compiler_path:
                continue
            if pid not in exits or exits[pid][1] != 0:
                refuse("actual Csc process failed or has no successful exit")
            if any(x.startswith("@") for x in argv):
                refuse("response-file bytes were not captured at open/read time")
            if argv[0] != exe:
                refuse("unexpected execve argv[0] for Csc")
            actual_cwd = witnessed_cwd.get((pid, stamp))
            if actual_cwd is None:
                refuse("missing Csc cwd in bounded ordered syscall replay")
            csc.append({"pid": pid, "time": stamp, "executable": exe,
                        "argv": argv, "cwd": actual_cwd,
                        "exitTime": exits[pid][0], "exitCode": exits[pid][1]})
    if len(csc) != len(event_argv):
        refuse("actual Csc process count != live MSBuild Csc task count")
    matched = {}
    used = set()
    for identity, expected in event_argv.items():
        candidates = [row for row in csc if row["argv"] == expected and row["pid"] not in used]
        if len(candidates) != 1:
            refuse("OS argv / MSBuild task mismatch, replay or duplicate child")
        matched[identity] = candidates[0]
        used.add(candidates[0]["pid"])
    if len(used) != len(csc):
        refuse("unowned OS Csc execution")
    return {"schema": SCHEMA, "disposition": "unqualified-software-observation",
            "platform": "linux-strace-ptrace", "traceFiles": traces,
            "launchCwd": root_cwd, "launcherPid": root,
            "processes": matched, "compilerSha256": digest(compiler_path)}


def verify_output_pins(rows):
    if not isinstance(rows, list) or not rows:
        refuse("missing actual compiler PE/PDB outputs")
    roles = set()
    for row in rows:
        if not isinstance(row, dict) or set(row) != {"kind", "path", "sha256", "bytes"}:
            refuse("invalid emitted-output inventory")
        if row["kind"] not in ("pe", "pdb") or not isinstance(row["bytes"], int):
            refuse("invalid emitted output type")
        file = clean_path(row["path"])
        if file.stat().st_size != row["bytes"] or digest(file) != row["sha256"]:
            refuse("emitted PE/PDB copy or source bytes drift")
        roles.add(row["kind"])
    if roles != {"pe", "pdb"}:
        refuse("both PE and PDB evidence required")


def gate_receipt(receipt, trace_dir, **kwargs):
    """An independently rederived trace must exactly equal the presented receipt."""
    rebuilt = verify(trace_dir, **kwargs)
    if rebuilt != receipt:
        refuse("companion receipt mutation / changed CWD / trace drift")
    return rebuilt
