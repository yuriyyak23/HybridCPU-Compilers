import argparse
import hashlib
import json
import os
import re
import subprocess
from pathlib import Path


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def fail(msg):
    raise SystemExit(msg)


def parse_ports(rtl):
    m = re.search(r"\bmodule\s+([A-Za-z_][A-Za-z0-9_$]*)\s*\((.*?)\)\s*;", rtl, re.S)
    if not m:
        fail("Could not locate CIRCT Verilog module declaration")
    ports = []
    direction = None
    width = 1
    for raw in m.group(2).splitlines():
        line = re.sub(r"//.*", "", raw).strip().rstrip(",")
        if not line:
            continue
        dm = re.match(r"(input|output)\s*(?:\[(\d+)\s*:\s*(\d+)\])?\s*([A-Za-z_][A-Za-z0-9_$]*)$", line)
        if dm:
            direction = dm.group(1)
            width = 1 if not dm.group(2) else abs(int(dm.group(2))-int(dm.group(3)))+1
            name = dm.group(4)
        else:
            nm = re.match(r"([A-Za-z_][A-Za-z0-9_$]*)$", line)
            if not nm or direction is None:
                fail(f"Cannot parse RTL port line: {raw!r}")
            name = nm.group(1)
        ports.append((direction, name, width))
    inputs = [(n, w) for d, n, w in ports if d == "input"]
    outputs = [(n, w) for d, n, w in ports if d == "output"]
    if not inputs or not outputs or len({n for _, n, _ in ports}) != len(ports):
        fail("RTL interface did not yield unique input and output ports")
    return m.group(1), inputs, outputs
def main():
    ap = argparse.ArgumentParser()
    for arg in ("mlir", "rtl", "vectors", "out", "circt-opt", "iverilog", "vvp"):
        ap.add_argument("--" + arg, required=True, type=Path)
    ap.add_argument("--source-pin", action="append", default=[], type=Path)
    a = ap.parse_args()
    rows = [json.loads(line) for line in a.vectors.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    if not rows:
        fail("No JSONL vectors")
    for i, row in enumerate(rows):
        if set(row) != {"scenario", "inputs", "expected", "origin"}:
            fail(f"row {i}: required keys are scenario, inputs, expected, origin")
        if not all(isinstance(row[k], dict) for k in ("inputs", "expected")):
            fail(f"row {i}: inputs/expected must be objects")
    rtl = a.rtl.read_text(encoding="utf-8-sig")
    top, inputs, outputs = parse_ports(rtl)
    in_names = {n for n, _ in inputs}
    out_names = {n for n, _ in outputs}
    for i, row in enumerate(rows):
        if set(row["inputs"]) != in_names or set(row["expected"]) != out_names:
            fail(f"row {i} {row['scenario']}: port key mismatch; inputs={sorted(row['inputs'])} outputs={sorted(row['expected'])}")
        for name, width in inputs + outputs:
            value = row["inputs" if (name, width) in inputs else "expected"][name]
            if not isinstance(value, int) or value < 0 or value >= (1 << width):
                fail(f"row {i} {row['scenario']}: {name}={value!r} does not fit width {width}")

    hex_path = a.out / "trap-carrier-rtl-vectors.hex"
    tb_path = a.out / "trap-carrier-rtl.tb.sv"
    sim_path = a.out / "trap-carrier-rtl.vvp"
    log_path = a.out / "trap-carrier-rtl.log"
    observation_path = a.out / "trap-carrier-rtl-observations.json"
    tb = ["module tb;", "integer fd, i, scanned, mismatches;"]
    for n, w in inputs:
        tb.append(f"reg [{w-1}:0] {n};" if w > 1 else f"reg {n};")
    for n, w in outputs:
        tb.append(f"wire [{w-1}:0] {n};")
        tb.append(f"reg [{w-1}:0] exp_{n};" if w > 1 else f"reg exp_{n};")
    tb.append(f"{top} dut (" + ", ".join(f".{n}({n})" for n, _ in inputs + outputs) + ");")
    tb += ["initial begin", "  mismatches = 0;", f'  fd = $fopen("{hex_path.as_posix()}", "r");', '  if (fd == 0) $fatal(1, "vector file open failed");', f"  for (i = 0; i < {len(rows)}; i = i + 1) begin"]
    scanvars = [n for n, _ in inputs] + ["exp_" + n for n, _ in outputs]
    tb.append(f'    scanned = $fscanf(fd, "' + " ".join(["%h"] * len(scanvars)) + '\\n", ' + ", ".join(scanvars) + ");")
    tb += [f"    if (scanned != {len(scanvars)}) $fatal(1, \"vector scan failed row=%0d scanned=%0d\", i, scanned);", "    #1;"]
    for n, _ in outputs:
        tb.append(f'    if ({n} !== exp_{n}) begin $display("MISMATCH row=%0d port={n} expected=%h actual=%h", i, exp_{n}, {n}); mismatches = mismatches + 1; end')
    tb += ["  end", '  $display("RESULT rows=%0d mismatches=%0d", i, mismatches);', '  $finish;', "end", "endmodule", ""]
    tb_path.write_text("\n".join(tb), encoding="utf-8")
    with hex_path.open("w", encoding="ascii", newline="\n") as f:
        for row in rows:
            vals = [row["inputs"][n] for n, _ in inputs] + [row["expected"][n] for n, _ in outputs]
            f.write(" ".join(format(v, "x") for v in vals) + "\n")
    env = dict(os.environ)
    ibin = a.iverilog.parent
    env["PATH"] = str(ibin) + os.pathsep + str(ibin.parent / "lib") + os.pathsep + env.get("PATH", "")
    comp = subprocess.run([str(a.iverilog), "-g2012", "-s", "tb", "-o", str(sim_path), str(a.rtl), str(tb_path)], capture_output=True, text=True, env=env)
    if comp.returncode:
        fail("Icarus compile failed:\n" + comp.stdout + comp.stderr)
    sim = subprocess.run([str(a.vvp), str(sim_path)], capture_output=True, text=True, env=env)
    log_path.write_text(comp.stdout + comp.stderr + sim.stdout + sim.stderr, encoding="utf-8")
    if sim.returncode:
        fail("Icarus simulation failed:\n" + sim.stdout + sim.stderr)
    m = re.search(r"RESULT rows=(\d+) mismatches=(\d+)", sim.stdout)
    if not m or int(m.group(1)) != len(rows):
        fail("Icarus did not report expected row count:\n" + sim.stdout)
    mismatches = int(m.group(2))
    observations = {"rows": len(rows), "mismatches": mismatches, "mismatchLines": [x for x in sim.stdout.splitlines() if x.startswith("MISMATCH ")], "simulationOutput": sim.stdout.strip()}
    observation_path.write_text(json.dumps(observations, indent=2) + "\n", encoding="utf-8")
    circt_version = subprocess.check_output([str(a.circt_opt), "--version"], text=True, stderr=subprocess.STDOUT).strip()
    receipt = {
        "schema": "circuitweave.trap-carrier-comb-rtl-receipt/v1",
        "status": "PASS_VECTOR_CHECK" if mismatches == 0 else "FAIL_VECTOR_MISMATCH",
        "claimLevel": "CombinationalProjectionOnly",
        "clockResetStallClaim": False,
        "scope": "CIRCT-emitted combinational TrapEntryEvent projection; does not model retirement state, pipeline timing, stalls, replay, or hardware equivalence",
        "module": top,
        "rows": len(rows), "mismatches": mismatches,
        "ports": {"inputs": inputs, "outputs": outputs},
        "inputs": {"mlirSha256": sha(a.mlir), "rtlSha256": sha(a.rtl), "vectorsSha256": sha(a.vectors)},
        "sourcePins": [{"path": str(p), "sha256": sha(p)} for p in a.source_pin],
        "artifacts": {p.name: sha(p) for p in (a.mlir, a.rtl, a.vectors, hex_path, tb_path, sim_path, log_path, observation_path)},
        "tools": {"circtOpt": {"path": str(a.circt_opt), "sha256": sha(a.circt_opt), "version": circt_version}, "iverilog": {"path": str(a.iverilog), "sha256": sha(a.iverilog)}, "vvp": {"path": str(a.vvp), "sha256": sha(a.vvp)}},
        "simulationOutput": sim.stdout.strip()
    }
    receipt_path = a.out / "trap-carrier-rtl-receipt.json"
    receipt_path.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(sim.stdout.strip())
    print(f"Receipt: {receipt_path}")
    if mismatches:
        fail(f"{mismatches} RTL vector mismatch(es); see {observation_path}")

if __name__ == "__main__":
    main()





