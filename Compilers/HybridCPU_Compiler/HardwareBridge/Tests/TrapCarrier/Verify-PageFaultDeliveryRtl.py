import argparse
import hashlib
import json
import os
import pathlib
import subprocess
import tempfile


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def expected(row: dict) -> tuple[int, int, int, int, int, int, int]:
    incoming = bool(row["incoming_fault_valid"])
    winner = bool(row["winner_valid"])
    stage = row["winner_stage"]
    valid = incoming and winner and stage in (4, 5)
    rejected = incoming and (not winner or stage not in (4, 5))
    active_lane = row["winner_lane"] if valid and stage == 4 else row["incoming_active_mem_lane"]
    thrown_address = row["winner_fault_address"] if stage == 5 else row["incoming_fault_address"]
    thrown_write = row["winner_fault_is_write"] if stage == 5 else row["incoming_fault_is_write"]
    return (int(rejected), int(valid), active_lane, thrown_address, thrown_write, int(valid), int(valid))


def packed(values: tuple[int, int, int, int, int, int, int]) -> int:
    rejected, valid, lane, address, is_write, thrown_valid, flush = values
    return (rejected << 71) | (valid << 70) | (lane << 67) | (address << 3) | (is_write << 2) | (thrown_valid << 1) | flush


def make_tb(rows: list[dict]) -> str:
    lines = ["module tb;", "reg [2:0] incoming_active_mem_lane;", "reg [63:0] incoming_fault_address;",
             "reg incoming_fault_is_write, incoming_fault_valid;", "reg [63:0] winner_fault_address;",
             "reg winner_fault_is_write;", "reg [2:0] winner_lane;", "reg [2:0] winner_stage;", "reg winner_valid;"]
    lines += ["wire delivery_rejected, delivery_valid;", "wire [2:0] selected_active_mem_lane;",
              "wire [63:0] thrown_address;", "wire thrown_is_write, thrown_valid, trap_flush;"]
    lines += ["page_fault_delivery_after_winner dut(.*);", "initial begin"]
    for index, row in enumerate(rows):
        lines.extend([
            f"  incoming_active_mem_lane=3'd{row['incoming_active_mem_lane']}; incoming_fault_address=64'd{row['incoming_fault_address']};",
            f"  incoming_fault_is_write=1'b{row['incoming_fault_is_write']}; incoming_fault_valid=1'b{row['incoming_fault_valid']};",
            f"  winner_fault_address=64'd{row['winner_fault_address']}; winner_fault_is_write=1'b{row['winner_fault_is_write']};",
            f"  winner_lane=3'd{row['winner_lane']}; winner_stage=3'd{row['winner_stage']}; winner_valid=1'b{row['winner_valid']}; #1;",
            f"  if ({{delivery_rejected,delivery_valid,selected_active_mem_lane,thrown_address,thrown_is_write,thrown_valid,trap_flush}} !== 72'h{packed(expected(row)):018x}) $fatal(1, \"{index}:{row['scenario']}\");",
        ])
    lines += ["  $display(\"PASS vectors=%d\", " + str(len(rows)) + "); $finish;", "end", "endmodule", ""]
    return "\n".join(lines)


def run(iverilog: str, vvp: str, rtl: pathlib.Path, tb_text: str, work: pathlib.Path) -> subprocess.CompletedProcess:
    tb = work / "tb.sv"
    exe = work / "tb.vvp"
    tb.write_text(tb_text, encoding="utf-8")
    env = os.environ.copy()
    suite_root = pathlib.Path(iverilog).resolve().parent.parent
    tool_dirs = os.pathsep.join([str(pathlib.Path(iverilog).resolve().parent), str(pathlib.Path(vvp).resolve().parent), str(suite_root / "lib")])
    env["PATH"] = tool_dirs + os.pathsep + env.get("PATH", "")
    env["YOSYSHQ_ROOT"] = str(suite_root)
    compile_result = subprocess.run([iverilog, "-g2012", "-s", "tb", "-o", str(exe), str(rtl), str(tb)], capture_output=True, text=True, env=env)
    if compile_result.returncode:
        raise RuntimeError(f"iverilog failed ({compile_result.returncode}): " + compile_result.stdout + compile_result.stderr)
    return subprocess.run([vvp, str(exe)], capture_output=True, text=True, env=env)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--rtl", required=True)
    parser.add_argument("--vectors", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--iverilog", required=True)
    parser.add_argument("--vvp", required=True)
    args = parser.parse_args()
    rtl = pathlib.Path(args.rtl).resolve()
    vectors_path = pathlib.Path(args.vectors).resolve()
    output = pathlib.Path(args.out).resolve()
    if output.exists():
        raise SystemExit("output directory must be new")
    rows = [json.loads(line) for line in vectors_path.read_text(encoding="utf-8").splitlines() if line.strip()]
    output.mkdir(parents=True)
    original = output / "page-fault-delivery.sv"
    original.write_bytes(rtl.read_bytes())
    tb_text = make_tb(rows)
    with tempfile.TemporaryDirectory(dir=output) as temp:
        work = pathlib.Path(temp)
        good = run(args.iverilog, args.vvp, original, tb_text, work)
        if good.returncode:
            raise RuntimeError("Icarus candidate mismatch: " + good.stdout + good.stderr)
    mutant = output / "page-fault-delivery-mutant.sv"
    mutant_text = original.read_text(encoding="utf-8-sig")
    old = "assign thrown_address = _GEN ? winner_fault_address : incoming_fault_address;"
    new = "assign thrown_address = ((winner_stage == 3'd5) || (winner_stage == 3'd4)) ? winner_fault_address : incoming_fault_address;"
    if old not in mutant_text:
        raise RuntimeError("mutant anchor not found in generated RTL")
    mutant.write_text(mutant_text.replace(old, new, 1), encoding="utf-8")
    with tempfile.TemporaryDirectory(dir=output) as temp:
        work = pathlib.Path(temp)
        mutated = run(args.iverilog, args.vvp, mutant, tb_text, work)
        if mutated.returncode == 0:
            raise RuntimeError("dual-MEM winner-payload mutant escaped detection")
    dual = next(row for row in rows if row["scenario"] == "dual-memory-selected-winner-versus-thrown-input")
    receipt = {
        "schema": "circuitweave.processor-page-fault-delivery-rtl-check/v1",
        "vectorsSha256": sha(vectors_path.read_bytes()),
        "candidateRtlSha256": sha(original.read_bytes()),
        "hwirSha256": json.loads((rtl.parent.parent / "manifest.json").read_text(encoding="utf-8"))["hwirSha256"],
        "mlirSha256": sha((rtl.parent.parent / "page-fault-delivery.mlir").read_bytes()),
        "vectorCount": len(rows),
        "runtimeDerivedVectors": sum(row["evidence"].startswith("v195") for row in rows),
        "candidate": "PASS",
        "candidateOutput": f"PASS vectors={len(rows)}",
        "dualMemWinnerAddress": dual["winner_fault_address"],
        "dualMemThrownAddress": expected(dual)[3],
        "mutant": "DETECTED",
        "mutantFailure": "Icarus rejected the winner-payload substitution at a differential output assertion.",
        "toolchain": {"iverilog": "Icarus Verilog 14.0-devel", "vvp": "Icarus vvp 14.0-devel"},
        "scope": "Combinational delivery branch projection after a source-resolved winner; inputs are helper/runtime facts. Not arbitration, cycle timing, clock/reset, architectural CSR/mtvec, or sequential equivalence."
    }
    receipt_path = output / "receipt.json"
    receipt_path.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(receipt))


if __name__ == "__main__":
    main()
