"""R2-03: run the immutable release stage_core_v2.py, then an actual built .NET codec DLL.
All mutating tests touch only newly created OS-TEMP staged fixtures. No real Golden/Csc/Core.
"""
import hashlib
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
import uuid

HERE = Path(__file__).resolve().parent
RELEASE = HERE.parents[1] / "HWIR" / "EvaluatedProjectGraph" / "stage_core_v2.py"
EMITTER = HERE / "emit_reviewed_stage_fixture.py"
CASES = (1, 64, 65, 1001)
# Independently reviewed immutable synthetic release-wire SHA256 pins from
# Windows/Linux .NET/Python byte-parity run 44836465436, after the Unicode CRLF
# fixture correction. Changing these requires explicit new fixture review.
# Do NOT discover expected pins from the bytes under validation.
FROZEN_ROOT_SHA = "CF02F40E130BF1F63A105962A2BFEA74BC0628B8CEC721B1C69F82EB26D4D9DD"
FROZEN_WIRE = {
    1: ("A8C40E8EAAC55B9DDBD78A56635D6567FDDFA277A7714F80BA02267202CB7131",
        "D7021F54A9BD835E933DB7C5D71463B67D135E72462256079CF1A40AD8A9319E"),
    64: ("5368902CD828E7CC2CA4F782B2D445624F5CBD608B14345D5ABA998389F4EF20",
         "826EDFB86D85322CDB821C6F849BA0F5BD134D24B3DD580FA38835AFC8B0BB4A"),
    65: ("2C97DB17A3579BB50A834C4524298DFDAE97EF56C44F24F27FEA749145E792C2",
         "2E39298B7F1B063A8ABE814033C1E7F568A67350C5231307CD0B36709BFCD885"),
    1001: ("5FF353C3293D50A1774AA62B1C0D35D5BDA20995DFE30E486533F62AC42C28D5",
           "27F42F514ABEB3B4B1B28E53515A8099C02D58A7AA8AF6EFEE6E1B2E0118962C"),
}


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def execute(label, argv, expected=0):
    started = time.monotonic()
    proc = subprocess.run(argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          timeout=300, check=False)
    duration = time.monotonic() - started
    merged = proc.stdout + proc.stderr
    print("R203_STEP %s exit=%d duration=%.3fs stdoutSHA=%s stderrSHA=%s" %
          (label, proc.returncode, duration, sha(proc.stdout), sha(proc.stderr)), flush=True)
    if (expected == 0 and proc.returncode != 0) or (
            expected == "refuse" and proc.returncode == 0):
        # Only synthetic test diagnostics; do not dump a private Golden source.
        raise RuntimeError("%s expected %s, received %d; detail=%s" %
                           (label, expected, proc.returncode,
                            merged.decode("utf-8", errors="replace")[-500:]))
    return proc.stdout.decode("utf-8", errors="strict")


def inventory(root):
    return {p.relative_to(root).as_posix(): sha(p.read_bytes())
            for p in sorted(root.rglob("*")) if p.is_file()}


def pins(text):
    return [re.search(name + r"=([0-9a-f]{64})", text).group(1)
            for name in ("HB02CapturedClosureSha256", "RootManifestSha256")]


def replace_exact(data, old, new):
    if old not in data:
        raise RuntimeError("mutation target not present in fixture")
    return data.replace(old, new, 1)


def mutations(out):
    index = out / "bridge-index.json"
    chunk = out / "bridge-index" / "chunk-0000.jsonl"
    mapped = out / "capture-map.json"
    roots = out / "roots-manifest.json"
    input_path = next(iter(sorted((out / "inputs").glob("*.cs"))))
    map_bytes = mapped.read_bytes()
    # Require two distinct primary paths before constructing a path collision.
    sources = list(re.finditer(rb'"Path":"(inputs/[0-9]{5}\.cs)"', map_bytes))
    if len(sources) < 2:
        raise RuntimeError("expected at least two staged source paths")
    second_path = b'"Path":"' + sources[1].group(1) + b'"'
    first_path = b'"Path":"' + sources[0].group(1) + b'"'
    raw_chunk = chunk.read_bytes()
    lines = raw_chunk.splitlines(keepends=True)
    if len(lines) < 2:
        raise RuntimeError("expected at least two rows in 65-source fixture")
    # Mutations here are not regenerated/checksummed: both validators must reject.
    return [
        ("bom", index, b"\xef\xbb\xbf" + index.read_bytes()),
        ("invalid-utf8", index, b"\xff" + index.read_bytes()),
        ("whitespace-index", index, index.read_bytes() + b" "),
        ("unknown-index-field", index, replace_exact(index.read_bytes(),
            b'{"Schema":', b'{"Extra":0,"Schema":')),
        ("duplicate-index-field", index, replace_exact(index.read_bytes(),
            b'{"Schema":', b'{"Schema":"bad","Schema":')),
        ("wrong-version", index, replace_exact(index.read_bytes(),
            b'closure-index/v2', b'closure-index/v3')),
        ("sha-case", index, re.sub(
            rb'(?<="ClosureSha256":")[A-F0-9]{64}',
            lambda match: match.group(0).lower(), index.read_bytes(), count=1)),
        ("chunk-corrupt", chunk, raw_chunk[:-1] + b"x"),
        ("jsonl-reordered", chunk, b"".join([lines[1], lines[0]] + lines[2:])),
        ("crlf", chunk, raw_chunk.replace(b"\n", b"\r\n")),
        ("duplicate-role", chunk, replace_exact(raw_chunk,
            b'"Role":', b'"Role":"source","Role":')),
        ("missing-chunk", chunk, None),
        ("unknown-map-field", mapped, replace_exact(map_bytes,
            b'{"Schema":', b'{"Extra":0,"Schema":')),
        ("duplicate-map-property", mapped, replace_exact(map_bytes,
            b'{"Schema":', b'{"Schema":"bad","Schema":')),
        ("unknown-map-schema", mapped, replace_exact(map_bytes,
            b'csc-to-core-map/v1', b'csc-to-core-map/v999')),
        ("staged-path-collision", mapped, replace_exact(map_bytes, second_path, first_path)),
        ("traversal-map", mapped, replace_exact(map_bytes,
            b'"Path":"inputs/', b'"Path":"../inputs/')),
        ("capture-oversize", mapped, re.sub(rb'(?<="Bytes":)[0-9]+',
            b'999999999', map_bytes, count=1)),
        ("pdb-role-promotion", mapped, replace_exact(map_bytes,
            b'"SourceRole":"metadata-only"', b'"SourceRole":"compiler-pdb"')),
        ("role-promotion", mapped, replace_exact(map_bytes,
            b'"SourceRole":"primary"', b'"SourceRole":"metadata-only"')),
        ("duplicate-roots-property", roots, replace_exact(roots.read_bytes(),
            b'{"Schema":', b'{"Schema":"bad","Schema":')),
        ("wrong-root", roots, replace_exact(roots.read_bytes(),
            b'"MethodName":"Step"', b'"MethodName":"Wrong"')),
        ("stale-original", input_path, input_path.read_bytes() + b"// changed\n"),
    ]


def main():
    if len(sys.argv) != 2:
        raise RuntimeError("usage: r203_release_wire_driver.py ABSOLUTE-BUILT-TEST-DLL")
    dll = Path(sys.argv[1]).resolve(strict=True)
    if not dll.is_file() or dll.suffix.lower() != ".dll" or "CscCoreV2Parity" not in dll.name:
        raise RuntimeError("actual CscCoreV2Parity compiled DLL required")
    if not RELEASE.is_file() or not EMITTER.is_file():
        raise RuntimeError("real Python release stager and fixture must exist")
    dotnet = os.environ.get("R203_DOTNET", "dotnet")
    execute("production-dotnet-matrix", [dotnet, str(dll), "--r203-wire-matrix"])
    # This refusal must happen before invoking a fictitious managed codec.
    missing = Path(tempfile.gettempdir()) / ("R2_03_missing-host-" + uuid.uuid4().hex + ".dll")
    execute("missing-output-dll", [sys.executable, str(__file__), str(missing)], "refuse")
    for count in CASES:
        out = Path(tempfile.gettempdir()) / ("hb-r203-stage-" + uuid.uuid4().hex)
        if out.exists():
            raise RuntimeError("output target already exists")
        try:
            text = execute("release-python-stage-%d" % count,
                           [sys.executable, str(EMITTER), str(out), str(count)])
            capture_pin, root_pin = pins(text)
            verify_argv = [sys.executable, str(RELEASE), "verify", "--output", str(out),
                           "--reviewed-capture", capture_pin, "--reviewed-roots", root_pin]
            managed_argv = [dotnet, str(dll), str(out)]
            execute("python-verify-%d" % count, verify_argv)
            execute("dotnet-verify-%d" % count, managed_argv)
            before = inventory(out)
            idx = (out / "bridge-index.json").read_bytes()
            descriptors = (out / "bridge-index").glob("chunk-*.jsonl")
            joined = b"".join(p.read_bytes() for p in sorted(descriptors))
            index_sha = sha(idx)
            closure_sha = sha(joined)
            actual_root_sha = sha((out / "roots-manifest.json").read_bytes())
            if (index_sha, closure_sha) != FROZEN_WIRE[count] or (
                    actual_root_sha != FROZEN_ROOT_SHA):
                raise RuntimeError(
                    "reviewed R2-03 frozen v2 wire pin mismatch: count=%d "
                    "index=%s expected=%s closure=%s expected=%s roots=%s expected=%s" %
                    (count, index_sha, FROZEN_WIRE[count][0], closure_sha,
                     FROZEN_WIRE[count][1], actual_root_sha, FROZEN_ROOT_SHA))
            print("R203_PARITY rows=%d indexSHA=%s concatenatedJSONLSHA=%s rootsSHA=%s" %
                  (count, index_sha, closure_sha, actual_root_sha),
                  flush=True)
            if inventory(out) != before:
                raise RuntimeError("read-only .NET parity check modified staged fixture")
            if count == 65:
                for label, target, changed in mutations(out):
                    original = target.read_bytes()
                    if changed == original:
                        raise RuntimeError("no-op mutation " + label)
                    try:
                        if changed is None:
                            target.unlink()
                        else:
                            target.write_bytes(changed)
                        execute("python-refuse-" + label, verify_argv, "refuse")
                        execute("dotnet-refuse-" + label, managed_argv, "refuse")
                    finally:
                        target.write_bytes(original)
                    if inventory(out) != before:
                        raise RuntimeError("failed to restore synthetic mutation: " + label)
                execute("python-after-mutation", verify_argv)
                execute("dotnet-after-mutation", managed_argv)
        finally:
            if out.exists():
                shutil.rmtree(out)
    print("PASS R2-03 actual Python release stage + production .NET bytes, refusal matrix; synthetic/unqualified.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired, AttributeError) as ex:
        print("FAIL R2-03: " + str(ex), file=sys.stderr)
        sys.exit(1)
