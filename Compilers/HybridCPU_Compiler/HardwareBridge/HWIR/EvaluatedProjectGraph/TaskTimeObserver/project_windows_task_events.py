"""Project live MSBuild Csc task events using Windows command-line parsing.

This preserves the task command tokens for later candidate input inventory. It
does not claim OS argv, working directory, file reads, input-byte completeness,
or trusted provenance. In particular, it refuses response-file arguments.
"""
import argparse
import ctypes
import ctypes.wintypes
import hashlib
import json
import os
from pathlib import Path
import re
import sys

SCHEMA = "hybridcpu.hardwarebridge.msbuild-csc-task-events/v1"
PROJECTION_SCHEMA = "hybridcpu.hardwarebridge.windows-task-command-projection/v1"
MAX_EVENTS = 32
MAX_COMMAND = 262144
MAX_TOKENS = 8194
MAX_OUTPUT = 16 * 1024 * 1024


class Refused(ValueError):
    pass


def no_duplicate_members(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise Refused("duplicate JSON member: " + key)
        result[key] = value
    return result


def parse_windows_command_line(command):
    if os.name != "nt":
        raise Refused("Windows command-line projection requires Windows")
    shell32 = ctypes.WinDLL("shell32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    shell32.CommandLineToArgvW.argtypes = [ctypes.wintypes.LPCWSTR,
                                            ctypes.POINTER(ctypes.c_int)]
    shell32.CommandLineToArgvW.restype = ctypes.POINTER(ctypes.wintypes.LPWSTR)
    kernel32.LocalFree.argtypes = [ctypes.c_void_p]
    kernel32.LocalFree.restype = ctypes.c_void_p
    count = ctypes.c_int()
    pointer = shell32.CommandLineToArgvW(command, ctypes.byref(count))
    if not pointer:
        raise Refused("CommandLineToArgvW failed: " + str(ctypes.get_last_error()))
    try:
        if count.value < 1 or count.value > MAX_TOKENS:
            raise Refused("empty or oversized Windows argv")
        return [pointer[i] for i in range(count.value)]
    finally:
        kernel32.LocalFree(ctypes.cast(pointer, ctypes.c_void_p))


def classify(token):
    lower = token.lower()
    if token.startswith("@"):
        raise Refused("response-file bytes were not captured by this projector")
    for prefix, kind in (("/reference:", "reference"), ("/r:", "reference"),
                         ("/analyzer:", "analyzer"), ("/analyzerconfig:", "analyzer-config"),
                         ("/additionalfile:", "additional-file"), ("/sourcelink:", "source-link"),
                         ("/appconfig:", "app-config"), ("/embed:", "embedded"),
                         ("/out:", "pe-output"), ("/pdb:", "pdb-output"),
                         ("/refout:", "refout-output"), ("/generatedfilesout:", "generated-output-dir")):
        if lower.startswith(prefix):
            return kind
    if not token.startswith(("/", "-")) and lower.endswith(".cs"):
        return "source"
    return "switch-or-value"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--events", required=True)
    parser.add_argument("--events-sha256", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9a-fA-F]{64}", args.events_sha256):
        raise Refused("external SHA-256 pin is required")
    source = Path(args.events).resolve(strict=True)
    output = Path(args.output).resolve(strict=False)
    if not source.is_file() or output == source or not output.is_absolute():
        raise Refused("invalid input/output path")
    raw = source.read_bytes()
    digest = hashlib.sha256(raw).hexdigest()
    if digest != args.events_sha256.lower():
        raise Refused("task-event bytes differ from external SHA-256 pin")
    if len(raw) > MAX_OUTPUT:
        raise Refused("task-event envelope exceeds size bound")
    document = json.loads(raw.decode("utf-8-sig", errors="strict"),
                          object_pairs_hook=no_duplicate_members)
    if not isinstance(document, dict) or set(document) != {"schema", "disposition", "error", "events"}:
        raise Refused("unexpected task-event envelope")
    if document["schema"] != SCHEMA or document["disposition"] != "unqualified-software-observation" or document["error"] is not None:
        raise Refused("unsupported or failed task-event observation")
    events = document["events"]
    if not isinstance(events, list) or not 1 <= len(events) <= MAX_EVENTS:
        raise Refused("missing or excessive Csc task events")
    seen = set()
    projected = []
    total_chars = 0
    for event in events:
        required = {"Id", "ProjectFile", "StartUtc", "FinishUtc", "CommandLine", "Succeeded", "CommandEvents"}
        if not isinstance(event, dict) or set(event) != required:
            raise Refused("unexpected Csc event fields")
        if (not isinstance(event["Id"], str) or not event["Id"] or event["Id"] in seen or
                not isinstance(event["ProjectFile"], str) or not os.path.isabs(event["ProjectFile"]) or
                not isinstance(event["StartUtc"], str) or not isinstance(event["FinishUtc"], str) or
                not isinstance(event["CommandLine"], str) or not event["CommandLine"] or
                event["Succeeded"] is not True or event["CommandEvents"] != 1):
            raise Refused("duplicate identity or incomplete/failed Csc event")
        seen.add(event["Id"])
        total_chars += len(event["CommandLine"])
        if len(event["CommandLine"]) > MAX_COMMAND or total_chars > MAX_OUTPUT:
            raise Refused("Csc command text exceeds bound")
        argv = parse_windows_command_line(event["CommandLine"])
        compiler = argv[0]
        if not os.path.isabs(compiler) or Path(compiler).name.lower() not in ("csc.exe", "csc.dll"):
            raise Refused("task command does not start with an absolute Roslyn Csc executable")
        arguments = argv[1:]
        if any("\x00" in token or "\r" in token or "\n" in token for token in argv):
            raise Refused("control character in parsed command")
        projected.append({
            "taskId": event["Id"], "projectFile": event["ProjectFile"],
            "taskStartUtc": event["StartUtc"], "taskFinishUtc": event["FinishUtc"],
            "compilerToken": compiler, "compilerTokenSha256": hashlib.sha256(compiler.encode("utf-8")).hexdigest(),
            "commandLineSha256": hashlib.sha256(event["CommandLine"].encode("utf-8")).hexdigest(),
            "argvSha256": hashlib.sha256(json.dumps(argv, ensure_ascii=False, separators=(",", ":")).encode("utf-8")).hexdigest(),
            "argv": [{"ordinal": i, "token": token, "kind": classify(token)}
                     for i, token in enumerate(arguments)],
            "workingDirectory": {"status": "NotRecorded", "value": None},
            "executionClaim": "TaskEventOnly"
        })
    result = {
        "schema": PROJECTION_SCHEMA,
        "disposition": "unqualified-software-observation",
        "inputEventsSha256": digest,
        "parser": "Windows shell32 CommandLineToArgvW",
        "argvMeaning": "Projection of MSBuild TaskCommandLineEventArgs; not OS process argv.",
        "commands": projected,
        "claims": {"Execution": "NotEstablished", "Provenance": "NotEstablished", "Admission": "NotAdmitted"}
    }
    encoded = (json.dumps(result, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
    if len(encoded) > MAX_OUTPUT:
        raise Refused("projected command inventory exceeds bound")
    output.parent.mkdir(parents=True, exist_ok=True)
    if any(parent.is_symlink() for parent in (output.parent, *output.parent.parents)):
        raise Refused("linked output path component")
    with output.open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"status": "Projected", "events": len(projected),
                      "projectionSha256": hashlib.sha256(encoded).hexdigest(),
                      "execution": "NotEstablished", "provenance": "NotEstablished",
                      "admission": "NotAdmitted"}, separators=(",", ":")))


if __name__ == "__main__":
    try:
        main()
    except (OSError, UnicodeError, json.JSONDecodeError, Refused) as error:
        print("REFUSED: " + str(error), file=sys.stderr)
        raise SystemExit(2)
