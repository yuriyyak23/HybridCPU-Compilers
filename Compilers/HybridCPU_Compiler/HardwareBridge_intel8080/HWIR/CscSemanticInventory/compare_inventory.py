"""Compare stable source identities in prior and isolated Roslyn inventories.

Generated paths are intentionally excluded from coordinate comparison because
they belong to separate isolated builds. Dependency counts are compared; edge
identity equality and semantic equivalence are not claimed.
"""
import hashlib
import json
import os
import sys
import tempfile
from pathlib import Path

old_path, new_path, output = map(Path, sys.argv[1:])
if output.exists() or output.resolve().parent != Path(tempfile.gettempdir()).resolve():
    raise ValueError("output must be a new direct child of TEMP")
old_bytes, new_bytes = old_path.read_bytes(), new_path.read_bytes()
old, new = json.loads(old_bytes), json.loads(new_bytes)
if old["schema"] != "circuitweave.source-coverage-index/v1" or new["schema"] != "hardwarebridge.csc-semantic-inventory/v1":
    raise ValueError("unsupported inventory schema")
if any(p["diagnostics"] for p in new["projects"]):
    raise ValueError("semantic replay has compiler errors")

def pathkey(path):
    return os.path.normcase(os.path.abspath(path))

old_types = {(t["assembly"].casefold(), t["symbol"]): t for t in old["symbols"]}
new_types = {(p["assembly"].split(",")[0].casefold(), t["symbol"]): t for p in new["projects"] for t in p["types"]}
if len(old_types) != len(old["symbols"]) or len(new_types) != sum(len(p["types"]) for p in new["projects"]):
    raise ValueError("duplicate semantic type identity")
stable = {pathkey(f["path"]) for p in new["projects"] for f in p["sourceFiles"] if f["origin"] == "evaluated-compile"}
counts = {"typesOnlyPrior": len(old_types.keys() - new_types.keys()), "typesOnlyReplay": len(new_types.keys() - old_types.keys()),
          "typeAccessibilityDifferences": 0, "typeAttributeDifferences": 0, "declarationCoordinateDifferences": 0,
          "declarationMemberDifferences": 0, "memberIdentityDifferences": 0, "memberMetadataDifferences": 0,
          "memberCoordinateDifferences": 0, "memberDependencyCountDifferences": 0}
examples = []
def mismatch(category, identity):
    counts[category] += 1
    if len(examples) < 1000:
        examples.append({"category": category, "identity": identity})

def coordinates(locations, prior):
    return {(pathkey(x["file"]), x["startLine" if prior else "line"], x["startColumn" if prior else "column"])
            for x in locations if pathkey(x["file"]) in stable}

def declarations(item, prior):
    result = {}
    for d in item["declarations"]:
        c = coordinates([d["location"]], prior)
        if c:
            result[next(iter(c))] = tuple(sorted(d["memberIds"]))
    return result

member_count = 0
for key in sorted(old_types.keys() & new_types.keys()):
    a, b = old_types[key], new_types[key]
    identity = "|".join(key)
    if a.get("accessibility") != b.get("accessibility"):
        mismatch("typeAccessibilityDifferences", identity)
    if sorted((x.get("type"), x.get("text")) for x in a.get("attributes", [])) != sorted((x.get("type"), x.get("text")) for x in b.get("attributes", [])):
        mismatch("typeAttributeDifferences", identity)
    ad, bd = declarations(a, True), declarations(b, False)
    if ad.keys() != bd.keys(): mismatch("declarationCoordinateDifferences", identity)
    if any(ad[c] != bd[c] for c in ad.keys() & bd.keys()): mismatch("declarationMemberDifferences", identity)
    am, bm = {m["id"]: m for m in a["members"]}, {m["id"]: m for m in b["members"]}
    if len(am) != len(a["members"]) or len(bm) != len(b["members"]): raise ValueError("duplicate member identity")
    if am.keys() != bm.keys(): mismatch("memberIdentityDifferences", identity)
    for mid in am.keys() & bm.keys():
        left, right = am[mid], bm[mid]; member_count += 1
        if any(left.get(field) != right.get(field) for field in ("kind", "accessibility", "isStatic")) or sorted(left.get("attributes", [])) != sorted(right.get("attributes", [])):
            mismatch("memberMetadataDifferences", identity + "|" + mid)
        if coordinates(left["locations"], True) != coordinates(right["locations"], False):
            mismatch("memberCoordinateDifferences", identity + "|" + mid)
        if left["observedDependencyCount"] != len(right["dependencies"]):
            mismatch("memberDependencyCountDifferences", identity + "|" + mid)

processor = new_types[("hybridcpu_ise", "global::YAKSys_Hybrid_CPU.Processor")]
report = {"schema": "hardwarebridge.semantic-inventory-comparison/v1", "disposition": "diagnostic-unqualified",
          "priorInventorySha256": hashlib.sha256(old_bytes).hexdigest(), "replayInventorySha256": hashlib.sha256(new_bytes).hexdigest(),
          "priorTypes": len(old_types), "replayTypes": len(new_types), "comparedMembers": member_count,
          "processorDeclarations": len(processor["declarations"]), "processorMembers": len(processor["members"]),
          "differences": counts, "examples": examples,
          "scope": "Identity, member metadata, non-generated source coordinates and dependency counts only; generated coordinates, dependency edge identity, missing implicit/dynamic edges and hardware elaboration are not proven."}
with output.open("x", encoding="utf-8") as stream:
    json.dump(report, stream, indent=2)
print("Compared", len(old_types), "prior and", len(new_types), "replayed types; differences:", counts)
