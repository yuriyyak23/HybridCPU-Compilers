import json
import hashlib
import os
import re
import subprocess
import sys
from pathlib import Path

rtl_path, vectors_path, tb_path, sim_path, vvp_path, mlir_path, hwir_path, circt_opt, iverilog_arg = map(Path, sys.argv[1:10])
rtl = rtl_path.read_text(encoding="utf-8")
rows = [json.loads(line) for line in vectors_path.read_text(encoding="utf-8").splitlines() if line]

header = rtl.split(");", 1)[0]
header = re.sub(r"//[^\n]*", "", header)
ports = []
direction = None
width = 1
for piece in header.split(","):
    match = re.search(r"\b(input|output)\s*(\[[^]]+\])?\s*([A-Za-z_][A-Za-z0-9_]*)\s*$", piece)
    if match:
        direction = match.group(1)
        width = 1 if not match.group(2) else int(match.group(2).split(":")[0][1:]) + 1
        name = match.group(3)
    else:
        match = re.search(r"([A-Za-z_][A-Za-z0-9_]*)\s*$", piece)
        if not match or direction is None:
            raise SystemExit(f"Cannot parse RTL port declaration: {piece!r}")
        name = match.group(1)
    ports.append((direction, name, width))

inputs = [(n, w) for d, n, w in ports if d == "input"]
outputs = [(n, w) for d, n, w in ports if d == "output"]
if not inputs or not outputs:
    raise SystemExit("RTL top module has no parsed input/output ports")

hex_path = vectors_path.with_suffix(".rtl.hex")
lines = ["module tb;", "integer fd, i, scanned;", ""]
for name, width in inputs:
    lines.append(f"reg [{width-1}:0] {name};" if width > 1 else f"reg {name};")
for name, width in outputs:
    lines.append(f"wire [{width-1}:0] {name};" if width > 1 else f"wire {name};")
    lines.append(f"reg [{width-1}:0] exp_{name};" if width > 1 else f"reg exp_{name};")
lines.append("vector_segment_read_lifecycle dut (" + ", ".join(f".{n}({n})" for _, n, _ in ports) + ");")
fmt = " ".join(["%h"] * (len(inputs) + len(outputs))) + "\\n"
vars_ = ", ".join([n for n, _ in inputs] + ["exp_" + n for n, _ in outputs])
lines += ["initial begin", f'  fd = $fopen("{hex_path.as_posix()}", "r");', '  if (fd == 0) $fatal(1, "vector file open failed");',
          f"  for (i = 0; i < {len(rows)}; i = i + 1) begin", f'    scanned = $fscanf(fd, "{fmt}", {vars_});',
          f'    if (scanned != {len(inputs)+len(outputs)}) $fatal(1, "vector scan failed at %0d: %0d", i, scanned);', "    #1;"]
for name, _ in outputs:
    lines.append(f'    if ({name} !== exp_{name}) $fatal(1, "mismatch row=%0d port={name} expected=%h actual=%h", i, exp_{name}, {name});')
lines += ["  end", f'  $display("PASS vector-segment lifecycle RTL vectors=%0d inputs=%0d outputs=%0d", i, {len(inputs)}, {len(outputs)});', "  $finish;", "end", "endmodule", ""]
tb_path.write_text("\n".join(lines), encoding="utf-8")
with hex_path.open("w", encoding="ascii", newline="\n") as stream:
    for row in rows:
        vals = [row["inputs"][n] for n, _ in inputs] + [row["outputs"][n] for n, _ in outputs]
        stream.write(" ".join(format(v, "x") for v in vals) + "\n")
iverilog = iverilog_arg
tool_bin = str(iverilog.parent)
env = dict(os.environ)
env["PATH"] = tool_bin + os.pathsep + str(iverilog.parent.parent / "lib") + os.pathsep + env.get("PATH", "")
compile_result = subprocess.run([str(iverilog), "-g2012", "-s", "tb", "-o", str(sim_path), str(rtl_path), str(tb_path)], check=True, capture_output=True, text=True, env=env)
simulation = subprocess.run([str(vvp_path), str(sim_path)], check=True, capture_output=True, text=True, env=env)
log_path = sim_path.with_suffix(".log")
log_path.write_text(compile_result.stdout + compile_result.stderr + simulation.stdout + simulation.stderr, encoding="utf-8")
if f"PASS vector-segment lifecycle RTL vectors={len(rows)}" not in simulation.stdout:
    raise SystemExit("Icarus simulation did not report the expected vector count")
def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()
circt_version = subprocess.check_output([str(circt_opt), "--version"], text=True, stderr=subprocess.STDOUT)
circt_version = next((line.strip() for line in circt_version.splitlines() if "CIRCT" in line), circt_version.splitlines()[0])
receipt = {
    "schema": "circuitweave.vector-segment-read-rtl-receipt/v1",
    "status": "PASS_STRUCTURAL_LIFECYCLE_RTL_VS_INDEPENDENT_ORACLE",
    "trust": "StructuralOnly",
    "vectors": len(rows),
    "inputPorts": len(inputs),
    "outputPorts": len(outputs),
    "comparison": "CIRCT emitted combinational lifecycle projection vs independently generated software-oracle event vectors; no active/Golden runtime comparison or physical sequential controller",
    "designHwirSha256": sha(hwir_path),
    "artifacts": {p.name: sha(p) for p in (mlir_path, hwir_path, rtl_path, vectors_path, hex_path, tb_path, sim_path, log_path)},
    "tools": {
        "circtOpt": {"path": str(circt_opt), "sha256": sha(circt_opt), "version": circt_version},
        "iverilog": {"path": str(iverilog), "sha256": sha(iverilog)},
        "vvp": {"path": str(vvp_path), "sha256": sha(vvp_path)},
    },
    "simulationOutput": simulation.stdout.strip(),
}
receipt_path = sim_path.with_name("vector-segment-read-rtl-receipt.json")
receipt_path.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
print(receipt["simulationOutput"])
print(f"RTL receipt: {receipt_path}")
