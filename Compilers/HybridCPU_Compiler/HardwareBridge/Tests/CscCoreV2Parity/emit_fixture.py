"""Create a *synthetic* HB-INT-02 -> Core v2 transport from PR14 for .NET parity tests.

No compiler, Golden runtime, simulation or hardware execution. Output is a
new direct OS-TEMP child and is retained for the separately executed C# host.
"""
import hashlib
from pathlib import Path
import sys

root = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root / "HWIR" / "EvaluatedProjectGraph"))
from test_segmented_csc_capture_v2 import ClosureTests
import project_csc_to_core_v2 as producer


def main():
    if len(sys.argv) != 2:
        raise ValueError("Expected exactly one new absolute OS-TEMP output path.")
    fixture = ClosureTests("test_positive_capture_and_byte_replay")
    fixture.setUp()
    try:
        source_hash = hashlib.sha256(fixture.original.read_bytes()).hexdigest().upper()
        capture, _ = fixture.run_capture()
        roots = fixture.tmp / "explicit-roots.json"
        roots.write_bytes(producer.encode([
            {"TypeName": "Fixture", "MethodName": "F", "MetadataToken": None,
             "TypeArguments": [], "MethodArguments": []}
        ]))
        digest = producer.project(capture, str(roots), sys.argv[1])
        if producer.verify_projection(sys.argv[1], capture) != digest or (
            hashlib.sha256(fixture.original.read_bytes()).hexdigest().upper() != source_hash
        ):
            raise ValueError("Synthetic source bytes changed or staged transport is not replayable.")
        output = Path(sys.argv[1])
        print("SYNTHETIC-ONLY HB-INT-02 .NET PARITY FIXTURE, not Csc execution")
        print("CoreClosureSha256=" + digest +
              " CoreIndexSha256=" + producer.sha((output / "index.json").read_bytes()) +
              " GoldenFixtureSha256BeforeAfter=" + source_hash)
    finally:
        fixture.doCleanups()
        fixture.tearDown()


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError, TypeError) as error:
        print("BLOCKED synthetic fixture: " + str(error), file=sys.stderr)
        sys.exit(2)
