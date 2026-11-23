"""Validate a bounded Windows Csc task/process/response-file observation bundle.

The bundle is only an unqualified software observation. A sampled process row
and copied response bytes do not prove that Csc read those bytes. No working
directory is inferred from the project path or build launcher.
"""
import argparse
import ctypes
import ctypes.wintypes
from datetime import datetime, timedelta
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys

SCHEMA = "hybridcpu.hardwarebridge.windows-csc-task-rsp-bundle/v1"
TASK_SCHEMA = "hybridcpu.hardwarebridge.msbuild-csc-task-events/v1"
PROCESS_SCHEMA = "hybridcpu.windows-csc-process-poll/v1"
CROSSWALK_SCHEMA = "hybridcpu.csc-task-response-argument-crosswalk/v1"
RECEIPT_SCHEMA = "hybridcpu.golden-windows-csc-process-and-response-capture/v1"
EXPECTED_PROCESSES = 5
MAX_JSON_BYTES = 16 * 1024 * 1024
MAX_RESPONSE_BYTES = 2 * 1024 * 1024
MAX_TOTAL_RESPONSE_BYTES = 8 * 1024 * 1024
MAX_COMMAND_CHARS = 262144
MAX_TOKENS = 8194
MAX_TOTAL_TOKENS = 40000
SHA = re.compile(r"^[0-9a-fA-F]{64}$")
OID = re.compile(r"^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$")


class Refused(ValueError):
    pass


def fail(message):
    raise Refused(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def _pairs_unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            fail("duplicate JSON member: " + key)
        result[key] = value
    return result


def parse_windows_command_line(command):
    """Parse Win32 command-line text; this is not a Roslyn RSP grammar claim."""
    if os.name != "nt":
        fail("Windows task/RSP bundle validation requires Windows")
    if not isinstance(command, str) or not command or len(command) > MAX_COMMAND_CHARS:
        fail("empty or oversized Windows command line")
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
        fail("CommandLineToArgvW failed: " + str(ctypes.get_last_error()))
    try:
        if count.value < 1 or count.value > MAX_TOKENS:
            fail("empty or oversized Windows argv")
        values = [pointer[i] for i in range(count.value)]
    finally:
        kernel32.LocalFree(ctypes.cast(pointer, ctypes.c_void_p))
    if any(not token or any(ch in token for ch in ("\x00", "\r", "\n")) for token in values):
        fail("empty token or control character in Windows argv")
    return values


def _safe_file(path, max_bytes):
    if not isinstance(path, str) or not path or not os.path.isabs(path):
        fail("absolute input path required")
    p = Path(path)
    for ancestor in (p, *p.parents):
        try:
            info = ancestor.lstat()
        except OSError:
            fail("missing bundle input: " + str(ancestor))
        if ancestor.is_symlink() or (getattr(info, "st_file_attributes", 0) &
                                     getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)):
            fail("linked/reparse bundle input: " + str(ancestor))
    if not p.is_file() or p.stat().st_size > max_bytes:
        fail("bundle input is not a bounded regular file: " + str(p))
    return p


def _read_pinned_json(path, expected_sha, label):
    if not isinstance(expected_sha, str) or not SHA.fullmatch(expected_sha):
        fail("external SHA-256 pin required for " + label)
    p = _safe_file(path, MAX_JSON_BYTES)
    raw = p.read_bytes()
    actual = digest(raw)
    if actual != expected_sha.lower():
        fail(label + " bytes differ from external SHA-256 pin")
    try:
        obj = json.loads(raw.decode("utf-8-sig", errors="strict"),
                         object_pairs_hook=_pairs_unique)
    except (UnicodeError, json.JSONDecodeError) as error:
        fail(label + " is invalid UTF-8 JSON: " + str(error))
    if not isinstance(obj, dict):
        fail(label + " JSON root must be an object")
    return p, raw, obj, actual


def _expect_keys(obj, expected, label):
    if not isinstance(obj, dict) or set(obj) != set(expected):
        fail(label + " fields differ from the versioned contract")


def _norm_win(path):
    if not isinstance(path, str) or not path or not os.path.isabs(path):
        fail("absolute Windows path required")
    return os.path.normcase(os.path.abspath(path)).casefold()


def _safe_dir(path):
    if not isinstance(path, str) or not path or not os.path.isabs(path):
        fail("absolute directory path required")
    p = Path(path)
    for ancestor in (p, *p.parents):
        try:
            info = ancestor.lstat()
        except OSError:
            fail("missing bundle directory: " + str(ancestor))
        if ancestor.is_symlink() or (getattr(info, "st_file_attributes", 0) &
                                     getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)):
            fail("linked/reparse bundle directory: " + str(ancestor))
    if not p.is_dir():
        fail("bundle path is not a directory")
    return p.resolve(strict=True)


def _same_win(left, right):
    return _norm_win(left) == _norm_win(right)


def _load_response(response_root, relative_name, expected_bytes, expected_sha):
    if not isinstance(relative_name, str) or not relative_name or Path(relative_name).name != relative_name:
        fail("response copy must be a simple file name")
    if not re.fullmatch(r"csc-[1-9][0-9]{0,9}\.rsp", relative_name):
        fail("response copy name is not bound to a Csc PID")
    root = _safe_dir(response_root)
    p = root / relative_name
    p = _safe_file(str(p), MAX_RESPONSE_BYTES)
    if not p.resolve(strict=True).is_relative_to(root):
        fail("response copy escapes response root")
    raw = p.read_bytes()
    if len(raw) != expected_bytes or digest(raw) != expected_sha.lower():
        fail("response copy length/SHA-256 differs from process capture")
    try:
        text = raw.decode("utf-8-sig", errors="strict")
    except UnicodeError:
        fail("response copy is not valid UTF-8")
    tokens = parse_windows_command_line(text)
    if any(token.startswith("@") for token in tokens):
        fail("nested response-file token is unsupported")
    return raw, tokens


def validate(events_path, events_sha, process_path, process_sha,
             crosswalk_path, crosswalk_sha, receipt_path, receipt_sha,
             response_root, expected_commit, expected_tree, compiler_path, compiler_sha):
    for label, value in (("Golden commit", expected_commit), ("tracked tree", expected_tree),
                         ("compiler", compiler_sha)):
        pattern = OID if label in ("Golden commit", "tracked tree") else SHA
        if not isinstance(value, str) or not pattern.fullmatch(value):
            fail("externally pinned identity/hash required for " + label)
    compiler_file = _safe_file(compiler_path, 16 * 1024 * 1024)
    if digest(compiler_file.read_bytes()) != compiler_sha.lower():
        fail("compiler file bytes differ from external compiler SHA-256 pin")

    _, events_raw, events, events_actual = _read_pinned_json(events_path, events_sha, "task events")
    _, process_raw, process, process_actual = _read_pinned_json(process_path, process_sha, "process observation")
    _, cross_raw, crosswalk, cross_actual = _read_pinned_json(crosswalk_path, crosswalk_sha, "response/task crosswalk")
    _, receipt_raw, receipt, receipt_actual = _read_pinned_json(receipt_path, receipt_sha, "capture receipt")

    _expect_keys(events, ("schema", "disposition", "error", "events"), "task-event envelope")
    if (events["schema"] != TASK_SCHEMA or events["disposition"] != "unqualified-software-observation" or
            events["error"] is not None or not isinstance(events["events"], list) or
            len(events["events"]) != EXPECTED_PROCESSES):
        fail("task-event envelope is failed, unsupported or not exactly five events")
    _expect_keys(process, ("schema", "pinnedHead", "trackedTreeBefore", "workingTreeBefore", "buildExit",
                           "projectRoot", "sdk", "pollIntervalMilliseconds", "observedCscProcesses",
                           "observedCscCount", "taskEventsCaptured", "trackedTreeAfter", "headAfter",
                           "workingTreeAfter", "processObservation"), "process-observation envelope")
    if (process["schema"] != PROCESS_SCHEMA or process["buildExit"] != 0 or
            process["pinnedHead"] != expected_commit or process["headAfter"] != expected_commit or
            process["trackedTreeBefore"] != expected_tree or process["trackedTreeAfter"] != expected_tree or
            process["workingTreeBefore"] is not None or process["workingTreeAfter"] is not None or
            process["taskEventsCaptured"] is not True or process["observedCscCount"] != EXPECTED_PROCESSES or
            not isinstance(process["observedCscProcesses"], list) or
            len(process["observedCscProcesses"]) != EXPECTED_PROCESSES):
        fail("process observation does not match successful exact pinned five-process capture")

    _expect_keys(receipt, ("schema", "status", "pinnedGoldenCommit", "trackedTreeBefore", "trackedTreeAfter",
                           "workingTreeAfter", "sdk", "buildExit", "taskEventFileSha256", "taskEventCount",
                           "successfulTaskEventCount", "processObservationMethod", "processCount", "processes",
                           "claims", "limits", "responseTaskCrosswalk", "captureFiles"), "receipt envelope")
    if (receipt["schema"] != RECEIPT_SCHEMA or receipt["status"] != "ObservedLocalUnqualified" or
            receipt["pinnedGoldenCommit"] != expected_commit or receipt["trackedTreeBefore"] != expected_tree or
            receipt["trackedTreeAfter"] != expected_tree or receipt["workingTreeAfter"] is not None or
            receipt["buildExit"] != 0 or receipt["taskEventCount"] != EXPECTED_PROCESSES or
            receipt["successfulTaskEventCount"] != EXPECTED_PROCESSES or receipt["processCount"] != EXPECTED_PROCESSES or
            not isinstance(receipt["processes"], list) or len(receipt["processes"]) != EXPECTED_PROCESSES):
        fail("receipt is failed, unsupported or does not bind the exact five-process pinned run")
    claims = receipt["claims"]
    if not isinstance(claims, dict) or claims.get("ActualCscProcessObserved") is not True or \
            claims.get("ActualResponseFileBytesCaptured") is not True or \
            claims.get("ResponseTokensCrosswalkedToTaskEvent") is not True or \
            claims.get("TaskExecutionSucceeded") is not True or \
            claims.get("CscFileReadBuffersObserved") is not False or \
            claims.get("IndependentCoreAdmission") is not False or claims.get("TrustedHandoff") is not False:
        fail("receipt claims do not preserve observed/unqualified evidence boundary")
    if receipt["taskEventFileSha256"] != events_actual:
        fail("receipt task-event digest does not match pinned task events")

    _expect_keys(crosswalk, ("schema", "parser", "taskEventsSha256", "processObservationSha256",
                             "crosswalkCount", "allOrderedArgumentsEqual", "crosswalks", "limitations"),
                 "crosswalk envelope")
    if (crosswalk["schema"] != CROSSWALK_SCHEMA or crosswalk["taskEventsSha256"] != events_actual or
            crosswalk["processObservationSha256"] != process_actual or
            crosswalk["crosswalkCount"] != EXPECTED_PROCESSES or crosswalk["allOrderedArgumentsEqual"] is not True or
            not isinstance(crosswalk["crosswalks"], list) or len(crosswalk["crosswalks"]) != EXPECTED_PROCESSES):
        fail("crosswalk is stale, incomplete or not an exact ordered match")
    rc = receipt["responseTaskCrosswalk"]
    if not isinstance(rc, dict) or rc.get("sha256") != cross_actual or rc.get("crosswalkCount") != EXPECTED_PROCESSES or \
            rc.get("allOrderedArgumentsEqual") is not True:
        fail("receipt does not bind the pinned crosswalk")

    event_by_id = {}
    for event in events["events"]:
        _expect_keys(event, ("Id", "ProjectFile", "StartUtc", "FinishUtc", "CommandLine", "Succeeded", "CommandEvents"),
                     "task event")
        if (not isinstance(event["Id"], str) or not event["Id"] or event["Id"] in event_by_id or
                not isinstance(event["ProjectFile"], str) or not os.path.isabs(event["ProjectFile"]) or
                not isinstance(event["CommandLine"], str) or not event["CommandLine"] or
                event["Succeeded"] is not True or event["CommandEvents"] != 1):
            fail("duplicate, failed or incomplete task event")
        event_by_id[event["Id"]] = event

    proc_by_pid = {}
    receipt_by_pid = {}
    for row in process["observedCscProcesses"]:
        _expect_keys(row, ("observedUtc", "pid", "parentPid", "processName", "executablePath", "commandLine",
                           "responseFilePath", "responseCopy", "responseBytes", "responseSha256", "responseCaptureError"),
                     "sampled process row")
        pid = row["pid"]
        if not isinstance(pid, int) or pid <= 0 or pid in proc_by_pid or row["processName"].lower() != "csc.exe" or \
                row["responseCaptureError"] is not None:
            fail("duplicate PID or invalid sampled Csc process row")
        proc_by_pid[pid] = row
    for row in receipt["processes"]:
        _expect_keys(row, ("pid", "parentPid", "processName", "executablePath", "executableSha256", "commandLine",
                           "responsePath", "responseCopy", "responseLength", "responseSha256"), "receipt process row")
        pid = row["pid"]
        if not isinstance(pid, int) or pid <= 0 or pid in receipt_by_pid:
            fail("duplicate/invalid receipt process PID")
        if row["executableSha256"].lower() != compiler_sha.lower():
            fail("Csc executable SHA differs from external compiler pin")
        if not _same_win(row["executablePath"], str(compiler_file)):
            fail("Csc executable path differs from external compiler path pin")
        receipt_by_pid[pid] = row
    if set(proc_by_pid) != set(receipt_by_pid):
        fail("sampled process and receipt PID sets differ")

    cross_by_pid = {}
    cross_by_event = set()
    for row in crosswalk["crosswalks"]:
        _expect_keys(row, ("pid", "responseSha256", "outputToken", "taskEventId", "projectFile", "taskSucceeded",
                           "taskArgumentCount", "responseArgumentCount", "orderedArgumentsEqual", "differences"),
                     "crosswalk row")
        pid, event_id = row["pid"], row["taskEventId"]
        if (not isinstance(pid, int) or pid in cross_by_pid or event_id not in event_by_id or event_id in cross_by_event or
                row["taskSucceeded"] is not True or row["orderedArgumentsEqual"] is not True or
                row["differences"] != [] or row["taskArgumentCount"] != row["responseArgumentCount"]):
            fail("crosswalk has duplicate, missing, failed or unequal task/process relation")
        cross_by_pid[pid] = row
        cross_by_event.add(event_id)
    if set(cross_by_pid) != set(proc_by_pid) or cross_by_event != set(event_by_id):
        fail("task, process and response crosswalk identities are not exactly 5-by-5-by-5")

    response_root_path = _safe_dir(response_root)
    expected_copy_names = {f"csc-{pid}.rsp" for pid in proc_by_pid}
    try:
        response_entries = list(response_root_path.iterdir())
    except OSError:
        fail("response directory cannot be enumerated")
    if len(response_entries) != EXPECTED_PROCESSES or {entry.name for entry in response_entries} != expected_copy_names:
        fail("response directory has missing or extra response copies")
    for entry in response_entries:
        _safe_file(str(entry), MAX_RESPONSE_BYTES)
    total_response = 0
    total_tokens = 0
    command_rows = []
    seen_rsp_paths = set()
    for pid in sorted(proc_by_pid):
        sampled, receipt_process, cross = proc_by_pid[pid], receipt_by_pid[pid], cross_by_pid[pid]
        if (sampled["parentPid"] != receipt_process["parentPid"] or sampled["processName"] != receipt_process["processName"] or
                not _same_win(sampled["executablePath"], receipt_process["executablePath"]) or
                sampled["commandLine"] != receipt_process["commandLine"] or
                not _same_win(sampled["responseFilePath"], receipt_process["responsePath"]) or
                Path(sampled["responseCopy"]).name != receipt_process["responseCopy"] or
                sampled["responseBytes"] != receipt_process["responseLength"] or
                sampled["responseSha256"].lower() != receipt_process["responseSha256"].lower() or
                cross["responseSha256"].lower() != receipt_process["responseSha256"].lower()):
            fail("sampled process, receipt and crosswalk disagree for PID " + str(pid))
        event = event_by_id[cross["taskEventId"]]
        if not _same_win(event["ProjectFile"], cross["projectFile"]):
            fail("task project path differs from crosswalk for PID " + str(pid))
        try:
            task_start = datetime.fromisoformat(event["StartUtc"].replace("Z", "+00:00"))
            task_finish = datetime.fromisoformat(event["FinishUtc"].replace("Z", "+00:00"))
            process_observed = datetime.fromisoformat(sampled["observedUtc"].replace("Z", "+00:00"))
        except (TypeError, ValueError):
            fail("invalid task/process observation timestamp")
        if (task_start.tzinfo is None or task_finish.tzinfo is None or process_observed.tzinfo is None or
                task_finish < task_start or process_observed < task_start - timedelta(seconds=2) or
                process_observed > task_finish + timedelta(seconds=2)):
            fail("sampled process observation is outside the matching Csc task interval")

        exe = parse_windows_command_line(sampled["commandLine"])
        task = parse_windows_command_line(event["CommandLine"])
        if len(exe) != 3 or exe[1].lower() != "/noconfig" or not exe[2].startswith("@"):
            fail("sampled Csc OS command must be exactly executable /noconfig @response")
        if Path(exe[0]).name.lower() != "csc.exe" or not _same_win(exe[0], sampled["executablePath"]):
            fail("sampled process argv executable path differs from observed image path")
        rsp_path = exe[2][1:]
        if not _same_win(rsp_path, sampled["responseFilePath"]) or _norm_win(rsp_path) in seen_rsp_paths:
            fail("duplicate or mismatched actual process response path")
        seen_rsp_paths.add(_norm_win(rsp_path))
        if len(task) < 3 or Path(task[0]).name.lower() != "csc.exe" or not _same_win(task[0], sampled["executablePath"]):
            fail("task event command does not use the observed pinned compiler")
        task_args = task[1:]
        if not task_args or task_args[0].lower() != "/noconfig":
            fail("task event is missing expected /noconfig prefix")
        task_args = task_args[1:]
        rsp_name = f"csc-{pid}.rsp"
        if receipt_process["responseCopy"] != rsp_name or Path(sampled["responseCopy"]).name != rsp_name:
            fail("response copy name is not uniquely bound to PID")
        sampled_copy = _safe_file(sampled["responseCopy"], MAX_RESPONSE_BYTES)
        if not sampled_copy.resolve(strict=True).is_relative_to(response_root_path) or sampled_copy.name != rsp_name:
            fail("sampled response copy path escapes supplied response root")
        raw, rsp_args = _load_response(str(response_root_path), rsp_name,
                                       receipt_process["responseLength"], receipt_process["responseSha256"])
        total_response += len(raw)
        total_tokens += len(rsp_args)
        if total_response > MAX_TOTAL_RESPONSE_BYTES or total_tokens > MAX_TOTAL_TOKENS:
            fail("bundle response/token budget exceeded")
        if (rsp_args != task_args or len(rsp_args) != cross["responseArgumentCount"] or
                len(task_args) != cross["taskArgumentCount"]):
            fail("captured response tokens differ in count/order from live task command")
        output_args = [token for token in rsp_args if token.lower().startswith("/out:")]
        if len(output_args) != 1 or output_args[0] != cross["outputToken"]:
            fail("crosswalk output token differs from the ordered Csc response arguments")
        command_rows.append({
            "pid": pid, "taskEventId": cross["taskEventId"], "projectFile": event["ProjectFile"],
            "processObservedUtc": sampled["observedUtc"],
            "compilerPath": sampled["executablePath"], "compilerSha256": compiler_sha.lower(),
            "osCommandLineSha256": digest(sampled["commandLine"].encode("utf-8")),
            "taskCommandLineSha256": digest(event["CommandLine"].encode("utf-8")),
            "responsePath": sampled["responseFilePath"], "responseCopy": rsp_name,
            "responseBytes": len(raw), "responseSha256": digest(raw),
            "argvSha256": digest(json.dumps(rsp_args, ensure_ascii=False, separators=(",", ":")).encode("utf-8")),
            "argv": rsp_args, "workingDirectory": {"status": "NotRecorded", "value": None},
            "executionClaim": "ObservedProcessWithResponseCopyNotReadProof"
        })

    result = {
        "schema": SCHEMA,
        "disposition": "unqualified-software-observation",
        "goldenCommit": expected_commit.lower(), "trackedTree": expected_tree.lower(),
        "taskEventsSha256": events_actual, "processObservationSha256": process_actual,
        "crosswalkSha256": cross_actual, "captureReceiptSha256": receipt_actual,
        "compilerPath": str(compiler_file), "compilerSha256": compiler_sha.lower(),
        "parser": "Win32 CommandLineToArgvW",
        "argumentRelation": "captured response tokens compared with MSBuild task command tokens; not Roslyn parser/read attestation",
        "commands": command_rows,
        "claims": {"CscProcessObserved": True, "ResponseBytesCopiedAndHashed": True,
                   "ResponseBytesConsumed": "NotEstablished", "WorkingDirectory": "NotRecorded",
                   "InputByteClosure": "NotEstablished", "Provenance": "NotAdmitted",
                   "IndependentCoreAdmission": "NotAdmitted", "HardwareVerification": "NotRun"}
    }
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("task-events", "process-observation", "crosswalk", "receipt"):
        parser.add_argument("--" + name, required=True)
        parser.add_argument("--" + name + "-sha256", required=True)
    parser.add_argument("--response-dir", required=True)
    parser.add_argument("--expected-golden-commit", required=True)
    parser.add_argument("--expected-tracked-tree", required=True)
    parser.add_argument("--compiler-path", required=True)
    parser.add_argument("--compiler-sha256", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args(argv)
    result = validate(args.task_events, args.task_events_sha256,
                      args.process_observation, args.process_observation_sha256,
                      args.crosswalk, args.crosswalk_sha256,
                      args.receipt, args.receipt_sha256, args.response_dir,
                      args.expected_golden_commit, args.expected_tracked_tree,
                      args.compiler_path, args.compiler_sha256)
    encoded = (json.dumps(result, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
    if len(encoded) > MAX_JSON_BYTES:
        fail("projected bundle exceeds output budget")
    output = Path(args.output)
    if not output.is_absolute():
        fail("absolute output path required")
    _safe_dir(str(output.parent))
    with output.open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"status": "ProjectedUnqualified", "commands": len(result["commands"]),
                      "outputSha256": digest(encoded), "responseBytesConsumed": "NotEstablished",
                      "provenance": "NotAdmitted", "independentCoreAdmission": "NotAdmitted"},
                     separators=(",", ":")))


if __name__ == "__main__":
    try:
        main()
    except (OSError, UnicodeError, json.JSONDecodeError, Refused) as error:
        print("REFUSED: " + str(error), file=sys.stderr)
        raise SystemExit(2)
