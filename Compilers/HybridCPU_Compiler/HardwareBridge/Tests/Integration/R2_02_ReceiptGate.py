#!/usr/bin/env python3
"""Fail-closed CI attestation of a *fixture-only* paired Bridge/Core replay receipt.

Checks evidence shape, not software facts or independent Core admissibility.
Only the pinned Core-owned importer can produce the underlying diagnostic.
"""
import argparse
import json
from pathlib import Path
import re
import sys

GIT = re.compile(r"[0-9a-f]{40}\Z")
SHA = re.compile(r"[0-9a-f]{64}\Z")
EXPECTED = {
    "input-bytes": "CWINT03_STALE_BYTES",
    "chunk-bytes": "CWINT03_V2_CHUNK_SHA",
    "omitted-chunk": "CWINT03_V2_IO",
    "duplicate-chunk": "CWINT03_V2_CHUNK_ORDER",
    "wrong-index-pin": "CWINT03_V2_INDEX_SHA",
    "duplicate-root": "CWINT03_V2_ROOT",
    "duplicate-json-key": "CWINT03_V2_SCHEMA",
    "wrong-role": "CWINT03_V2_ROLE",
}
CORE_DIAGNOSTIC = "CWINT03_V2_UNQUALIFIED_CLOSURE"


class Refusal(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise Refusal("R2_02_RECEIPT_" + message)


def pairs_unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "DUPLICATE_JSON")
        result[key] = value
    return result


def read_receipt(path):
    p = Path(path)
    require(p.is_file() and not p.is_symlink(), "FILE")
    require(0 < p.stat().st_size <= 16 * 1024 * 1024, "SIZE")
    return json.loads(p.read_bytes(), object_pairs_hook=pairs_unique)


def has_sha(value):
    return type(value) is str and SHA.fullmatch(value) is not None


def validate(doc, *, bridge_sha, core_sha, sdk):
    """Return a minimal safe verdict; never print source paths or raw diagnostics."""
    require(type(doc) is dict, "SHAPE")
    require(type(bridge_sha) is str and GIT.fullmatch(bridge_sha), "BRIDGE_PIN")
    require(type(core_sha) is str and GIT.fullmatch(core_sha), "CORE_PIN")
    require(doc.get("schema") == "hybridcpu.hardwarebridge.cross-repo-replay/v1", "SCHEMA")
    require(doc.get("scope") == "fixture" and doc.get("disposition") == "FixtureTransportOnly", "SCOPE")
    require(doc.get("bridgeSha") == bridge_sha and doc.get("coreSha") == core_sha, "CHECKOUT_DRIFT")
    require(doc.get("sdkPin") == sdk == "11.0.100-rc.2.26504.105", "SDK_PIN")
    require(doc.get("blocker") is None, "BLOCKER")
    require(doc.get("sourceUnchanged") is True, "SOURCE_DRIFT")
    for name in ("golden", "bridge", "core"):
        require(type(doc.get(name + "Before")) is dict and
                doc[name + "Before"] == doc.get(name + "After"), "INVENTORY_" + name.upper())
    require(type(doc.get("stagedBefore")) is dict and
            doc["stagedBefore"] == doc.get("stagedAfter"), "STAGE_DRIFT")
    stages = doc.get("stages")
    require(type(stages) is dict, "STAGE_SHAPE")
    for name in ("R1-02-capture", "R2-01-stage", "R2-02-core-reader"):
        require(stages.get(name) == "Passed", "NOTRUN_" + name)
    for name in ("R1-01-observer", "R1-03-pe-pdb"):
        require(stages.get(name) == "NotRun", "SYNTHETIC_SCOPE_" + name)
    require(all(has_sha(doc.get(key)) for key in ("captureIndexSha256", "coreReaderAssemblySha256")),
            "ASSEMBLY_PIN")
    require(type(doc.get("coreClosureSha256")) is str and
            SHA.fullmatch(doc["coreClosureSha256"].lower()) and
            type(doc.get("coreIndexSha256")) is str and
            SHA.fullmatch(doc["coreIndexSha256"].lower()), "INDEX_PIN")
    positive = doc.get("coreReader")
    require(type(positive) is dict and positive.get("state") == "Candidate" and
            positive.get("code") == CORE_DIAGNOSTIC and
            type(positive.get("rows")) is int and positive["rows"] >= 1001 and
            positive.get("indexSha256") == doc["coreIndexSha256"] and
            positive.get("closureSha256") == doc["coreClosureSha256"], "CORE_IMPORT")
    matrix = doc.get("negativeCoreMatrix")
    require(type(matrix) is list and len(matrix) == len(EXPECTED), "NEGATIVE_COUNT")
    seen = set()
    for case in matrix:
        require(type(case) is dict and type(case.get("mutation")) is str, "NEGATIVE_SHAPE")
        name = case["mutation"]
        require(name in EXPECTED and name not in seen, "NEGATIVE_ID")
        seen.add(name)
        require(case.get("expectedCode") == EXPECTED[name] and
                case.get("actualCode") == EXPECTED[name] and
                case.get("actualState") in ("Rejected", "Blocked") and
                case.get("expectedRefusal") == "Passed" and
                case.get("coreExecutableSha256") == doc["coreReaderAssemblySha256"],
                "NEGATIVE_DIAGNOSTIC")
    commands = doc.get("commands")
    require(type(commands) is list and len(commands) <= 32, "COMMAND_BUDGET")
    command_names = [x.get("label") for x in commands if type(x) is dict]
    names = ["dotnet-version", "R1-02-capture", "R2-01-stage", "R2-02-build-core-reader",
             "R2-02-core-reader", *("R2-02-negative-" + n for n in EXPECTED)]
    require(len(commands) == len(names) and command_names == names, "COMMAND_SET")
    for command in commands:
        require(type(command) is dict and
                all(has_sha(command.get(key)) for key in
                    ("executableSha256", "stdoutSha256", "stderrSha256")) and
                type(command.get("argv")) is list and len(command["argv"]) > 0,
                "COMMAND_EVIDENCE")
        negative = command["label"].startswith("R2-02-negative-")
        require(command.get("exit") == (1 if negative else 0) and
                command.get("status") == ("Failed" if negative else "Passed"), "COMMAND_EXIT")
    return {"status": "PairedCoreFixtureReceiptQualifiedForReview",
            "coreCode": CORE_DIAGNOSTIC, "sourceCountMin": 1001,
            "coreMutationRefusals": len(EXPECTED), "rtl": "NotRun", "golden": "NotRun"}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--receipt", required=True)
    p.add_argument("--bridge-sha", required=True)
    p.add_argument("--core-sha", required=True)
    p.add_argument("--sdk", required=True)
    args = p.parse_args()
    try:
        verdict = validate(read_receipt(args.receipt), bridge_sha=args.bridge_sha,
                           core_sha=args.core_sha, sdk=args.sdk)
    except (Refusal, OSError, json.JSONDecodeError, UnicodeDecodeError) as ex:
        # A static refusal code, never raw exception text which may contain private paths.
        print("BLOCKED: R2_02_RECEIPT_FAIL_CLOSED " + type(ex).__name__, file=sys.stderr)
        return 2
    print(json.dumps(verdict, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
