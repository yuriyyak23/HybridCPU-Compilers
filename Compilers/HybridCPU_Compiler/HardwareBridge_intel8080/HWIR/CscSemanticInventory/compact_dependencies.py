"""Create a bounded, unqualified Studio index of replayed source references."""
import hashlib
import json
import os
import sys
import tempfile
from pathlib import Path

inventory_path, replay_path, comparison_path, output = map(Path, sys.argv[1:])
if output.exists() or output.resolve().parent != Path(tempfile.gettempdir()).resolve():
    raise ValueError("output must be a new direct child of TEMP")
inventory_bytes, replay_bytes, comparison_bytes = (p.read_bytes() for p in (inventory_path, replay_path, comparison_path))
inventory, replay, comparison = map(json.loads, (inventory_bytes, replay_bytes, comparison_bytes))
sha = lambda b: hashlib.sha256(b).hexdigest()
if (inventory["schema"] != "circuitweave.source-coverage-index/v1" or
    replay["schema"] != "hardwarebridge.csc-semantic-inventory/v1" or
    comparison["schema"] != "hardwarebridge.semantic-inventory-comparison/v1" or
    comparison["priorInventorySha256"] != sha(inventory_bytes) or
    comparison["replayInventorySha256"] != sha(replay_bytes) or
    any(comparison["differences"].values()) or any(p["diagnostics"] for p in replay["projects"])):
    raise ValueError("semantic replay does not match a clean inventory comparison")
old = {(t["assembly"].casefold(), t["symbol"]): t for t in inventory["symbols"]}
if len(old) != len(inventory["symbols"]): raise ValueError("duplicate source type")
strings, indexes, rows = [], {}, []
def intern(value):
    if not isinstance(value, str) or not value or len(value) > 8192: raise ValueError("invalid reference text")
    if value not in indexes:
        indexes[value] = len(strings); strings.append(value)
    return indexes[value]
types, members, edges = set(), 0, 0
for project in replay["projects"]:
    assembly = project["assembly"].split(",", 1)[0]
    for typ in project["types"]:
        key = (assembly.casefold(), typ["symbol"])
        if key in types or key not in old: raise ValueError("unknown or duplicated replay type")
        types.add(key)
        prior = old[key]; expected = {m["id"] for m in prior["members"]}; actual = set()
        for member in typ["members"]:
            member_id = member["id"]
            if member_id in actual or member_id not in expected: raise ValueError("unknown or duplicated replay member")
            actual.add(member_id); members += 1; refs = []
            for edge in member["dependencies"]:
                location = edge["firstLocation"]
                if edge["kind"] not in ("invocation", "construction", "member-reference") or location["line"] < 1 or location["column"] < 1:
                    raise ValueError("invalid observed reference")
                refs.append([intern(edge["kind"]), intern(edge["target"]), intern(edge["targetAssembly"]), intern(location["file"]), location["line"], location["column"]]); edges += 1
            if refs:
                rows.append([intern(prior["assembly"]), intern(typ["symbol"]), intern(member_id), refs])
        if actual != expected: raise ValueError("incomplete replay members")
if types != old.keys() or len(types) != comparison["replayTypes"] or members != comparison["comparedMembers"]:
    raise ValueError("incomplete replay type or member coverage")
report = {"schema": "hardwarebridge.csc-compact-dependencies/v1", "disposition": "isolated-unqualified",
          "priorInventorySha256": sha(inventory_bytes), "replayInventorySha256": sha(replay_bytes),
          "comparisonSha256": sha(comparison_bytes), "graphSha256": replay["graphSha256"], "cscAuditSha256": replay["cscAuditSha256"],
          "typeCount": len(types), "memberCount": members, "edgeCount": edges, "strings": strings, "members": rows,
          "scope": "Observed isolated Roslyn references only; excludes implicit/dynamic dependencies and is not hardware connectivity or proof."}
encoded = json.dumps(report, ensure_ascii=True, separators=(",", ":")).encode()
if len(encoded) > 24 * 1024 * 1024: raise ValueError("compact replay exceeds bounded artifact size")
with output.open("xb") as stream: stream.write(encoded)
print(f"Indexed {len(types)} types, {members} members, {edges} references in {len(encoded)} bytes.")
