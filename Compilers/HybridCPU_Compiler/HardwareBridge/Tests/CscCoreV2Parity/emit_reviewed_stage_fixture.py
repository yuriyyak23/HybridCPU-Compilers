"""Emit real HB02 Python capture -> stage_core_v2 release-wire synthetic fixture.
No Csc, Golden runtime or Core importer is executed. Reviewer pins are explicit.
"""
from pathlib import Path
import hashlib
import json
import shutil
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "HWIR" / "EvaluatedProjectGraph"))
from test_segmented_csc_capture_v2 import ClosureTests
import segmented_csc_capture_v2 as c
import stage_core_v2 as stage


def main():
    if len(sys.argv) != 3 or int(sys.argv[2]) not in (1, 64, 65, 1001):
        raise ValueError("expected NEW absolute TEMP target and source count 1|64|65|1001")
    output, count = sys.argv[1], int(sys.argv[2])
    fixture = ClosureTests("test_positive_capture_and_byte_replay")
    fixture.setUp()
    try:
        # Exclude the default generator fixture, so exactly 'count' primary
        # compiler source rows can be asserted independently of extra metadata.
        fixture.generated.unlink()
        fixture.observation["projects"][0]["generatorSources"] = []
        primary = fixture.observation["projects"][0]
        # Exact raw Unicode and CRLF are source bytes, not JSONL framing.
        # This fixture never runs a compiler or touches the real Golden checkout.
        unicode_source = "// Ω 漢 🚀\r\nclass A {}\r\n".encode("utf-8")
        fixture.original.write_bytes(unicode_source)
        fixture.source.write_bytes(unicode_source)
        primary["sources"][0]["sha256"] = c.sha(unicode_source)
        for i in range(1, count):
            original = fixture.golden / ("P%04d.cs" % i)
            copied = fixture.copy / original.name
            original.write_bytes(("// primary fixture %d\n" % i).encode())
            copied.write_bytes(original.read_bytes())
            fixture.graph["nodes"][0]["compileFiles"].append(str(original))
            primary["argv"].append(str(copied))
            primary["sources"].append({
                "path": str(copied), "logicalPath": "project/" + original.name,
                "sha256": c.sha(copied.read_bytes()), "origin": "evaluated",
                "originalPath": str(original)})
        if count == 65:
            # Explicit second Csc command to exercise captured owned-body role:
            # never automatically promote it to source or qualification.
            owned_original = fixture.golden / "Owned.cs"
            owned_original.write_bytes(b"// synthetic owned body, no verification\n")
            owned_project = fixture.golden / "Owned.csproj"
            owned_project.write_bytes(b"<Project/>")
            owned_copy = fixture.copy / "Owned.cs"
            owned_copy.write_bytes(owned_original.read_bytes())
            nid = "Owned|Configuration=Debug"
            fixture.graph["nodes"].append({
                "id": nid, "project": str(owned_project),
                "compileFiles": [str(owned_original)],
                "globalProperties": {"Configuration": "Debug"},
                "projectAssets": None})
            primary_csc = primary["compiler"]
            fixture.observation["projects"].append({
                "nodeId": nid, "role": "owned-body",
                "workingDirectory": str(fixture.copy), "compiler": dict(primary_csc),
                "projectSha256": c.sha(owned_project.read_bytes()),
                "filePins": [],
                "argv": ["/target:library", "/langversion:preview",
                         "/define:FEATURE", "/nullable:enable", "/checked-",
                         "/out:obj/Owned.dll", str(owned_copy)],
                "argvSha256": "",
                "sources": [{"path": str(owned_copy),
                             "logicalPath": "project/Owned.cs",
                             "sha256": c.sha(owned_copy.read_bytes()),
                             "origin": "evaluated",
                             "originalPath": str(owned_original)}],
                "generatorSources": []})
        fixture.save()
        original_pins = {
            str(path): c.sha(path.read_bytes()) for path in fixture.golden.rglob("*")
            if path.is_file()
        }
        captured, capture_pin = fixture.run_capture()
        review = fixture.tmp / "reviewed-roots.json"
        review.write_bytes(c.canonical({"Schema": stage.ROOTS, "Roots": [
            {"TypeName": "A", "MethodName": "Step", "MetadataToken": None,
             "TypeArguments": [], "MethodArguments": []}]}))
        root_pin = c.sha(review.read_bytes())
        closure = stage.stage(captured, capture_pin, review, root_pin, output)
        if stage.verify(output, capture_pin, root_pin) != closure:
            raise ValueError("staged Core v2 replay failed")
        mapping = json.loads((Path(output) / "capture-map.json").read_bytes())
        primary_count = sum(x["Kind"] == "csc-source" and x["SourceRole"] == "primary"
                            for x in mapping["Rows"])
        owned_count = sum(x["Kind"] == "csc-source" and x["SourceRole"] == "owned-body"
                          for x in mapping["Rows"])
        if primary_count != count or owned_count != (1 if count == 65 else 0):
            raise ValueError("wrong source/owned-body role projection")
        if any(c.sha(Path(path).read_bytes()) != pin for path, pin in original_pins.items()):
            raise ValueError("synthetic original source mutated during capture/staging")
        print("SYNTHETIC-ONLY capture->reviewed stage; no Golden/Csc/CPU execution")
        print("Primary=" + str(primary_count) + " OwnedBody=" + str(owned_count))
        print("HB02CapturedClosureSha256=" + capture_pin +
              " RootManifestSha256=" + root_pin)
        print("CoreIndexSha256=" + stage.sha((Path(output) / "bridge-index.json").read_bytes()) +
              " CoreClosureSha256=" + closure)
    finally:
        fixture.doCleanups()
        fixture.tearDown()


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, TypeError) as error:
        print("BLOCKED release-stage synthetic fixture: " + str(error), file=sys.stderr)
        sys.exit(2)
