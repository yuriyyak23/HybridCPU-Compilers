import argparse
import hashlib
import json
import pathlib
import sys

MODEL_COMMIT = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f"
EXPECTED_SOURCES = {
    "8d265200a0faabc52f7c5b62eea7fa04a938f9fe1b8f61326aa3bed83bc39328",
    "9a46971ca2c41a52611a4c7fb65991df90c7645d1e25b21cbad63c38268b049c",
    "030a4262d0d57db5185b70e20ac3c61cce37bd471aa10116ce309b30971d665c",
}
CASE_COUNT = 131072


def sha(path):
    digest = hashlib.sha256()
    with pathlib.Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def load_oracle(path):
    with pathlib.Path(path).open(encoding="utf-8") as stream:
        try:
            header = json.loads(next(stream))
        except (StopIteration, json.JSONDecodeError) as exc:
            raise ValueError("Oracle JSONL is empty or has an invalid header") from exc
        if header.get("schema") != "circuitweave.issue-packet-materialization-oracle/v1":
            raise ValueError("Unexpected oracle JSONL schema")
        if header.get("modelCommit") != MODEL_COMMIT:
            raise ValueError("Oracle model commit differs from pinned model")
        if header.get("cases") != CASE_COUNT:
            raise ValueError("Oracle header case count differs from exhaustive domain")
        source_hashes = {item.get("sha256") for item in header.get("sources", [])}
        if source_hashes != EXPECTED_SOURCES:
            raise ValueError("Oracle source identities do not match pinned method/packet/lane partials")
        count = 0
        for line_number, line in enumerate(stream, 2):
            if not line.strip():
                continue
            try:
                item = json.loads(line)
            except json.JSONDecodeError as exc:
                raise ValueError(f"Invalid JSONL record at line {line_number}") from exc
            lane = item.get("laneIndex")
            occupied = item.get("occupied")
            mask = item.get("executableMask")
            result = item.get("materialize")
            if not isinstance(lane, int) or not 0 <= lane <= 255:
                raise ValueError(f"Invalid laneIndex at line {line_number}")
            if not isinstance(occupied, bool) or not isinstance(result, bool):
                raise ValueError(f"Invalid boolean field at line {line_number}")
            if not isinstance(mask, int) or not 0 <= mask <= 255:
                raise ValueError(f"Invalid executableMask at line {line_number}")
            if result != (occupied if lane < 4 else bool(mask & (1 << (lane & 31)))):
                raise ValueError(f"Oracle row violates exact source rule at line {line_number}")
            yield lane, occupied, mask, result
            count += 1
        if count != CASE_COUNT:
            raise ValueError(f"Expected {CASE_COUNT} oracle rows, found {count}")


def prepare(args):
    vectors_path = pathlib.Path(args.vectors)
    tb_path = pathlib.Path(args.testbench)
    if vectors_path.exists() or tb_path.exists():
        raise FileExistsError("Refusing to overwrite prepared comparison inputs")
    vectors_path.parent.mkdir(parents=True, exist_ok=True)
    for parent in (tb_path.parent,):
        parent.mkdir(parents=True, exist_ok=True)
    with vectors_path.open("x", encoding="ascii", newline="\n") as vector_file:
        for lane, occupied, mask, result in load_oracle(args.oracle):
            packed = (lane << 10) | (int(occupied) << 9) | (mask << 1) | int(result)
            vector_file.write(f"{packed:05x}\n")
    tb = """module tb;
  localparam integer CASES = 131072;
  reg [17:0] vectors [0:CASES-1];
  reg [7:0] lane_index;
  reg occupied;
  reg [7:0] executable_non_scalar_mask;
  reg expected;
  wire materialize;
  integer i;

  issue_packet_materialization_eligibility dut(
    .lane_index(lane_index),
    .occupied(occupied),
    .executable_non_scalar_mask(executable_non_scalar_mask),
    .materialize(materialize)
  );

  initial begin
    $readmemh("vectors.mem", vectors);
    for (i = 0; i < CASES; i = i + 1) begin
      {lane_index, occupied, executable_non_scalar_mask, expected} = vectors[i];
      #1;
      if (materialize !== expected) begin
        $display("FAIL index=%0d lane=%0d occupied=%0d mask=%0d got=%0d expected=%0d",
          i, lane_index, occupied, executable_non_scalar_mask, materialize, expected);
        $fatal(1, "materialization eligibility mismatch");
      end
    end
    $display("PASS vectors=%0d mismatches=0", CASES);
    $finish;
  end
endmodule
"""
    with tb_path.open("x", encoding="ascii", newline="\n") as stream:
        stream.write(tb)
    print(f"Prepared {CASE_COUNT} exhaustive oracle vectors")


def verify(args):
    output = pathlib.Path(args.receipt)
    if output.exists():
        raise FileExistsError(f"Refusing to overwrite immutable receipt: {output}")
    log = pathlib.Path(args.simulation_log).read_text(encoding="utf-8", errors="replace")
    if f"PASS vectors={CASE_COUNT} mismatches=0" not in log or "FAIL" in log:
        raise ValueError("Icarus simulation log does not contain the exhaustive PASS sentinel")
    oracle_count = sum(1 for _ in load_oracle(args.oracle))
    receipt = {
        "schema": "circuitweave.issue-packet-materialization-rtl-comparison/v1",
        "status": "PASS_EXHAUSTIVE_EXACT_SOURCE_ORACLE_VS_CIRCT_RTL",
        "modelCommit": MODEL_COMMIT,
        "cases": oracle_count,
        "mismatches": 0,
        "oracleSources": sorted(EXPECTED_SOURCES),
        "toolchain": {
            "circtOpt": {"path": str(pathlib.Path(args.circt).resolve()), "sha256": sha(args.circt)},
            "iverilog": {"path": str(pathlib.Path(args.iverilog).resolve()), "sha256": sha(args.iverilog)},
            "vvp": {"path": str(pathlib.Path(args.vvp).resolve()), "sha256": sha(args.vvp)},
        },
        "inputs": {
            "oracleJsonlSha256": sha(args.oracle),
            "bridgeHwirSha256": sha(args.hwir),
            "mlirSha256": sha(args.mlir),
            "bridgeProjectionSourceSha256": sha(args.bridge_source),
            "bridgeProgramSha256": sha(args.bridge_program),
            "bridgeProjectSha256": sha(args.bridge_project),
            "comparePythonSha256": sha(__file__),
            "runnerSha256": sha(args.runner),
        },
        "outputs": {
            "systemVerilogSha256": sha(args.rtl),
            "testbenchSha256": sha(args.testbench),
            "packedVectorsSha256": sha(args.vectors),
            "simulationLogSha256": sha(args.simulation_log),
        },
        "scope": "Exact-source ShouldMaterializeIssuePacketLane method versus CIRCT-emitted combinational eligibility logic; exhaustive byte laneIndex, occupied, and executable mask domain. No downstream source mapping, issue grant, stateful behavior, or CPU-level equivalence.",
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(receipt, stream, indent=2)
        stream.write("\n")
    print(f"Receipt: {output}")


parser = argparse.ArgumentParser()
sub = parser.add_subparsers(dest="command", required=True)
prep = sub.add_parser("prepare")
prep.add_argument("--oracle", required=True)
prep.add_argument("--testbench", required=True)
prep.add_argument("--vectors", required=True)
prep.set_defaults(run=prepare)
check = sub.add_parser("verify")
for name in ("oracle", "rtl", "testbench", "vectors", "simulation-log", "hwir", "mlir", "bridge-source", "bridge-program", "bridge-project", "circt", "iverilog", "vvp", "runner", "receipt"):
    check.add_argument("--" + name, required=True)
check.set_defaults(run=verify)
parsed = parser.parse_args()
try:
    parsed.run(parsed)
except Exception as exc:
    print(f"ERROR: {exc}", file=sys.stderr)
    raise SystemExit(1)
