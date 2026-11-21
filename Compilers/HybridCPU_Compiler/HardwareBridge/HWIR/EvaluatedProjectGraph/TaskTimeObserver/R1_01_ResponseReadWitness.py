"""Fail-closed Linux task-time @response read proof (toy only, unqualified).

The witness reconstructs the bytes returned by read(2) from an FD opened by the
*same* traced Csc process, strictly between execve and exit. It never reads the
response pathname from the filesystem after execution, and never treats an
MSBuild CommandLine message as evidence that these bytes were consumed.

Only an intentionally tiny strace grammar is accepted. mmap/readv/pread/dup,
relative open, a missing EOF, or trace truncation are Blocked, not Passed.
This cannot establish an independently attested filesystem inode identity.
"""
import ast
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import tempfile

SCHEMA = "hybridcpu.hardwarebridge.r1-01-response-read/v1"
MAX_RESPONSE = 262144
MAX_LINES = 50000
MAX_TRACE = 64 * 1024 * 1024
MAX_READS = 512
TOY_CSC_SWITCHES = frozenset({
    "/analyzer", "/analyzerconfig", "/checked-", "/debug", "/debug+",
    "/define", "/deterministic+", "/errorreport", "/features",
    "/filealign", "/fullpaths", "/generatedfilesout",
    "/highentropyva+", "/langversion", "/noconfig", "/nostdlib+",
    "/nowarn", "/nullable", "/optimize-", "/out", "/reference",
    "/refout", "/target", "/unsafe-", "/utf8output", "/warn",
    "/warnaserror+", "/warnaserror-"
})
QUOTED = r'"(?:\\.|[^"\\])*"'
LINE = re.compile(r"^(\d{10,12}\.\d{6}) (.*)$")
OPEN = re.compile(r"^openat\(AT_FDCWD, (" + QUOTED + r"), (O_RDONLY(?:\|[A-Z0-9_]+)*)\) = (\d+)$")
READ = re.compile(r"^read\((\d+), (" + QUOTED + r"), (\d+)\) = (\d+)$")
CLOSE = re.compile(r"^close\((\d+)\) = (0|-1 .+)$")
ACCESS = re.compile(r"^(?:read|readv|pread64|preadv|preadv2|lseek|_llseek|mmap|mmap2|mremap|dup|dup2|dup3|fcntl|write|pwrite64|ftruncate|sendfile|splice)\((\d+)(?:,|\))")

class Blocked(ValueError):
    pass


def refuse(code):
    raise Blocked("R1_01_RSP_" + code)


def _literal(s):
    try:
        value = json.loads(s)
    except ValueError:
        refuse("MALFORMED_PATH")
    if not isinstance(value, str) or not value or '\x00' in value:
        refuse("MALFORMED_PATH")
    return value


def _bytes(s):
    try:
        # strace read() uses C escapes, not JSON: octal sequences must survive.
        value = ast.literal_eval('b' + s)
    except (ValueError, SyntaxError):
        refuse("UNDECODABLE_READ")
    if not isinstance(value, bytes):
        refuse("UNDECODABLE_READ")
    return value


def effective_tokens(raw):
    try:
        value = raw.decode('utf-8-sig', errors='strict')
    except UnicodeError:
        refuse("RSP_ENCODING")
    if '\x00' in value or '\r' in value or '\\' in value or "'" in value or '#' in value:
        refuse("UNSUPPORTED_RSP_GRAMMAR")
    if any(line.lstrip().startswith('@') for line in value.splitlines()):
        refuse("NESTED_RSP")
    try:
        tokens = shlex.split(value, posix=True, comments=False)
    except ValueError:
        refuse("UNSUPPORTED_RSP_GRAMMAR")
    if not tokens or len(tokens) > 8192 or any(x.startswith('@') for x in tokens):
        refuse("EMPTY_OR_NESTED_RSP")
    for token in tokens:
        if token.startswith('/') and not token.startswith('/tmp/'):
            if token.split(':', 1)[0].lower() not in TOY_CSC_SWITCHES:
                refuse("UNKNOWN_CSC_SWITCH")
    return tokens


def proof(trace_dir, *, pid, exec_time, exit_time, raw_argv, task_argv):
    """Return separate versioned proof; never mutate existing OS receipt v1."""
    if not isinstance(pid, int) or pid <= 0 or not isinstance(raw_argv, list) or not isinstance(task_argv, list):
        refuse("INVALID_CALL")
    args = [a for a in raw_argv[1:] if isinstance(a, str) and a.startswith('@')]
    if len(args) != 1 or raw_argv.count(args[0]) != 1:
        refuse("EXACTLY_ONE_RSP_REQUIRED")
    path = args[0][1:]
    if (not os.path.isabs(path) or '..' in Path(path).parts or not path.endswith('.rsp')
            or not Path(path).parent.name.startswith('MSBuildTemp')
            or Path(path).parent.parent != Path(tempfile.gettempdir())):
        refuse("UNTRUSTED_RSP_PATH")
    if len(raw_argv) > 8 or any(not isinstance(a, str) or '\x00' in a for a in raw_argv):
        refuse("UNEXPECTED_OS_ARGV")
    if not isinstance(exec_time, str) or not isinstance(exit_time, str) or exec_time >= exit_time:
        refuse("EXECUTION_WINDOW")
    trace_path = Path(trace_dir) / ("trace." + str(pid))
    if trace_path.is_symlink() or not trace_path.is_file() or trace_path.stat().st_size > MAX_TRACE:
        refuse("MISSING_OR_OVERSIZE_TRACE")
    try:
        lines = trace_path.read_text(encoding='utf-8').splitlines()
    except UnicodeError:
        refuse("TRACE_ENCODING")
    if len(lines) > MAX_LINES:
        refuse("TRACE_LINE_BUDGET")
    opened = False
    closed = False
    fd = None
    chunks = []
    saw_eof = False
    read_count = 0
    open_stamp = None
    end_stamp = None
    for line in lines:
        m = LINE.fullmatch(line)
        if m is None:
            refuse("TRACE_TRUNCATION")
        stamp, call = m.groups()
        if stamp <= exec_time or stamp >= exit_time:
            continue
        if '<unfinished ...>' in call or call.startswith('<...'):
            if any(x in call for x in (path, 'openat(', 'read(', 'close(')):
                refuse("SPLIT_SYSCALL")
            continue
        if path in call and call.startswith(('open(', 'openat2(', 'rename(', 'unlink(', 'link(', 'symlink(')):
            if not call.startswith('openat('):
                refuse("UNSUPPORTED_PATH_OPERATION")
        if call.startswith('openat(') and path in call:
            match = OPEN.fullmatch(call)
            if not match or _literal(match.group(1)) != path or opened:
                refuse("UNSAFE_OR_DUPLICATE_OPEN")
            fd = int(match.group(3))
            open_stamp = stamp
            opened = True
            continue
        if not opened or closed:
            continue
        match = ACCESS.match(call)
        if match and int(match.group(1)) == fd and not call.startswith('read('):
            refuse("UNSUPPORTED_FD_OPERATION")
        if call.startswith('read('):
            match = READ.fullmatch(call)
            if match is None:
                if call.startswith('read(' + str(fd) + ','):
                    refuse("TRUNCATED_OR_FAILED_READ")
                continue
            read_fd, encoded, wanted, returned = match.groups()
            if int(read_fd) != fd:
                continue
            if saw_eof or read_count >= MAX_READS:
                refuse("READ_AFTER_EOF_OR_BUDGET")
            read_count += 1
            data = _bytes(encoded)
            n = int(returned)
            if n != len(data) or n > int(wanted) or sum(map(len, chunks)) + n > MAX_RESPONSE:
                refuse("READ_LENGTH_OR_BYTE_BUDGET")
            if n == 0:
                saw_eof = True
            else:
                chunks.append(data)
            continue
        match = CLOSE.fullmatch(call) if call.startswith('close(') else None
        if match and int(match.group(1)) == fd:
            if match.group(2) != '0' or not saw_eof:
                refuse("CLOSE_BEFORE_EOF")
            closed = True
            end_stamp = stamp
            continue
        if call.startswith(('close(', 'openat(')) and not match and path in call:
            refuse("AMBIGUOUS_FILE_IO")
    if not opened or not closed or not saw_eof or not chunks:
        refuse("MISSING_OPEN_READ_EOF_CLOSE")
    data = b''.join(chunks)
    tokens = effective_tokens(data)
    expanded = [raw_argv[0]]
    for arg in raw_argv[1:]:
        if arg == '@' + path:
            expanded.extend(tokens)
        else:
            expanded.append(arg)
    if expanded != task_argv:
        refuse("OS_RSP_VS_MSBUILD_COMMAND_MISMATCH")
    return {"schema": SCHEMA, "disposition": "unqualified-software-observation",
            "pid": pid, "responsePath": path,
            "openTime": open_stamp, "closeTime": end_stamp,
            "responseSha256": hashlib.sha256(data).hexdigest(),
            "responseBytes": len(data), "readCalls": read_count,
            "expandedArgvSha256": hashlib.sha256(json.dumps(expanded, ensure_ascii=False, separators=(',', ':')).encode()).hexdigest()}
