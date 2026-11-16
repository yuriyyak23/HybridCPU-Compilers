import argparse
import hashlib
import json
import pathlib
import subprocess
import sys
import os
import re


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hwir", required=True, type=pathlib.Path)
    ap.add_argument("--inventory", required=True, type=pathlib.Path)
    ap.add_argument("--mlir", required=True, type=pathlib.Path)
    ap.add_argument("--circt-bin", required=True, type=pathlib.Path)
    ap.add_argument("--oss-bin", required=True, type=pathlib.Path)
    ap.add_argument("--output", required=True, type=pathlib.Path)
    ap.add_argument("--active-runtime", type=pathlib.Path)
    ap.add_argument("--golden-runtime", type=pathlib.Path)
    ap.add_argument("--runtime-provenance", type=pathlib.Path)
    args = ap.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    doc = json.loads(args.hwir.read_text(encoding="utf-8"))
    modules = {m["id"]: m for m in doc["modules"]}
    top = modules[doc["topModule"]]
    inputs = [p for p in top["ports"] if p["direction"] == "Input"]
    outputs = [p for p in top["ports"] if p["direction"] == "Output"]
    inventory = json.loads(args.inventory.read_text(encoding="utf-8"))
    by_addr = {int(r["Address"]): r for r in inventory}
    addresses = sorted(set(by_addr) | {0x0FFF, 0xFFFF})
    privileges = [0, 1, 2, 3, 4, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF]
    policy_codes = {r["Policy"]: int(r["PolicyValue"]) for r in inventory}
    vector_rows = []
    input_words = []
    expected_words = []
    chain_flags = []
    runtime_expected = []
    for address in addresses:
        row = by_addr.get(address)
        for privilege in privileges:
            signed_privilege = privilege if privilege < 0x80000000 else privilege - 0x100000000
            for operation in range(5):
                for variant in range(2):
                    current = {}
                    for r in inventory:
                        member = r["AddressMember"]
                        initial = int(r["InitialValue"])
                        current[member] = initial if variant == 0 else initial ^ (int(r["Address"]) << 32) ^ 0xA55A5AA50123FEDC
                    privilege_fault = False
                    if row and operation == 0 and privilege != 3 and row["Policy"] == "MachineReadWrite":
                        privilege_fault = signed_privilege < 3
                    if row and operation == 1:
                        pol = row["Policy"]
                        privilege_fault = ((pol == "MachineReadWrite" and signed_privilege < 3) or
                                           (pol in ("ReadOnly", "HardwareUpdated")) or
                                           (pol == "SupervisorReadWrite" and signed_privilege < 1))
                    unknown_fault = operation in (0, 1, 2) and row is None
                    fault = privilege_fault or unknown_fault
                    values = {"address": address, "event_valid": 1, "operation": operation,
                              "privilege": privilege, "write_data": 0xD3A59C71E4B2860F}
                    values.update({"current_" + member: value for member, value in current.items()})
                    expected = {
                        "actual_privilege": privilege if privilege_fault else 0,
                        "fault_address": address if fault else 0,
                        "is_registered": int(operation == 4 and row is not None),
                        "is_write_fault": int(privilege_fault and operation == 1),
                        "operation_invalid": 0, "operation_valid": 1,
                        "privilege_fault": int(privilege_fault),
                        "read_data": current[row["AddressMember"]] if not fault and operation in (0, 3) and row else 0,
                        "required_privilege": (1 if operation == 1 and row and row["Policy"] == "SupervisorReadWrite" else 3) if privilege_fault else 0,
                        "reservation_kind": 1 if operation in (0, 3) else 2 if operation in (1, 2) else 0,
                        "selected_policy": int(row["PolicyValue"]) if row else 0,
                        "unknown_address_fault": int(unknown_fault),
                        "write_enable": int(not fault and operation in (1, 2)),
                    }
                    for r in inventory:
                        member = r["AddressMember"]
                        expected["next_" + member] = (values["write_data"] if expected["write_enable"] and address == int(r["Address"]) else current[member])
                    input_words.append(values)
                    expected_words.append(expected)
                    chain_flags.append(False)

    # Feed each RTL next-value set back as the next serialized software event's
    # current state. This is an explicit event recurrence in the testbench, not a
    # physical clocked CSR register implementation.
    chain_state = {r["AddressMember"]: int(r["InitialValue"]) for r in inventory}
    chain_events = []
    for r in inventory:
        for privilege in (3, 1, 0, 0x80000000):
            for operation in (1, 2, 0, 3, 4):
                chain_events.append((int(r["Address"]), privilege, operation))
    for address in (0x0FFF, 0xFFFF):
        for operation in range(5):
            chain_events.append((address, 3, operation))
    chain_events.extend(((0x300, 3, 7), (0xC00, 0, 7)))
    for index, (address, privilege, operation) in enumerate(chain_events):
        row = by_addr.get(address)
        signed_privilege = privilege if privilege < 0x80000000 else privilege - 0x100000000
        event_valid = 0 if index % 19 == 7 else 1
        write_data = (0x9E3779B97F4A7C15 * (index + 1)) & 0xFFFFFFFFFFFFFFFF
        operation_valid = operation in range(5)
        active = bool(event_valid and operation_valid)
        privilege_fault = False
        if active and row and operation == 0 and privilege != 3 and row["Policy"] == "MachineReadWrite":
            privilege_fault = signed_privilege < 3
        if active and row and operation == 1:
            pol = row["Policy"]
            privilege_fault = ((pol == "MachineReadWrite" and signed_privilege < 3) or
                               (pol in ("ReadOnly", "HardwareUpdated")) or
                               (pol == "SupervisorReadWrite" and signed_privilege < 1))
        unknown_fault = active and operation in (0, 1, 2) and row is None
        fault = privilege_fault or unknown_fault
        values = {"address": address, "event_valid": event_valid, "operation": operation,
                  "privilege": privilege, "write_data": write_data}
        values.update({"current_" + member: value for member, value in chain_state.items()})
        write_enable = active and not fault and operation in (1, 2)
        expected = {
            "actual_privilege": privilege if privilege_fault else 0,
            "fault_address": address if fault else 0,
            "is_registered": int(active and operation == 4 and row is not None),
            "is_write_fault": int(privilege_fault and operation == 1),
            "operation_invalid": int(event_valid and not operation_valid), "operation_valid": int(operation_valid),
            "privilege_fault": int(privilege_fault),
            "read_data": chain_state[row["AddressMember"]] if active and not fault and operation in (0, 3) and row else 0,
            "required_privilege": (1 if operation == 1 and row and row["Policy"] == "SupervisorReadWrite" else 3) if privilege_fault else 0,
            "reservation_kind": 1 if operation in (0, 3) else 2 if operation in (1, 2) else 0,
            "selected_policy": int(row["PolicyValue"]) if row else 0,
            "unknown_address_fault": int(unknown_fault),
            "write_enable": int(write_enable),
        }
        for r in inventory:
            member = r["AddressMember"]
            expected["next_" + member] = write_data if write_enable and address == int(r["Address"]) else chain_state[member]
        input_words.append(values)
        expected_words.append(expected)
        chain_flags.append(True)
        runtime_fault = None
        if privilege_fault:
            runtime_fault = {"kind": "Privilege", "address": address,
                             "required": 1 if operation == 1 and row and row["Policy"] == "SupervisorReadWrite" else 3,
                             "actual": privilege, "isWrite": operation == 1}
        elif unknown_fault:
            runtime_fault = {"kind": "UnknownAddress", "address": address}
        runtime_value = None
        if runtime_fault is None and active and operation in (0, 3):
            runtime_value = chain_state[row["AddressMember"]] if row else 0
        elif runtime_fault is None and active and operation == 4:
            runtime_value = row is not None
        call_reservation = active and operation in (0, 1, 2, 3)
        reservation_kind = "Read" if operation in (0, 3) else "Write"
        runtime_expected.append({
            "index": index, "valid": event_valid, "address": address, "privilege": privilege,
            "operation": operation, "writeData": write_data, "skipped": not active,
            "value": runtime_value, "fault": runtime_fault,
            "reservationCallbacks": (["acquire:" + reservation_kind, "dispose:" + reservation_kind] if call_reservation else []),
            "state": {r["AddressMember"]: expected["next_" + r["AddressMember"]] for r in inventory}
        })
        chain_state = {r["AddressMember"]: expected["next_" + r["AddressMember"]] for r in inventory}

    runtime_comparison = {"status": "NOT_RUN"}
    if args.active_runtime or args.golden_runtime:
        if not args.active_runtime or not args.golden_runtime or not args.runtime_provenance:
            raise RuntimeError("--active-runtime, --golden-runtime and --runtime-provenance are required together")
        runtime_reports = {}
        for label, path in (("ACTIVE", args.active_runtime), ("GOLDEN", args.golden_runtime)):
            runtime_doc = json.loads(path.read_text(encoding="utf-8"))
            events = runtime_doc.get("events", [])
            if runtime_doc.get("eventCount") != len(runtime_expected) or len(events) != len(runtime_expected):
                raise RuntimeError(f"{label} runtime event count mismatch")
            mismatches = []
            for expected_event, actual_event in zip(runtime_expected, events):
                for key in ("index", "valid", "address", "privilege", "operation", "writeData", "skipped", "value", "fault", "state"):
                    if actual_event.get(key) != expected_event.get(key):
                        mismatches.append({"index": expected_event["index"], "field": key, "expected": expected_event.get(key), "actual": actual_event.get(key)})
                        break
                if label == "ACTIVE" and actual_event.get("reservationCallbacks") != expected_event["reservationCallbacks"]:
                    mismatches.append({"index": expected_event["index"], "field": "reservationCallbacks", "expected": expected_event["reservationCallbacks"], "actual": actual_event.get("reservationCallbacks")})
                if len(mismatches) >= 64:
                    break
            runtime_reports[label] = {"events": len(events), "mismatches": len(mismatches), "firstMismatches": mismatches[:16], "traceSha256": sha(path)}
        runtime_comparison = {"status": "PASS" if all(report["mismatches"] == 0 for report in runtime_reports.values()) else "DIFFERENCES_RECORDED",
                              "models": runtime_reports,
                              "sourceProvenanceSha256": sha(args.runtime_provenance) if args.runtime_provenance else None,
                              "activeCallbackContract": "Read/DirectRead acquire then dispose Read; Write/HardwareWrite acquire then dispose Write; IsRegistered has no reservation callback"}

    def width(port): return int(port["type"]["width"])
    in_width = sum(width(p) for p in inputs)
    out_width = sum(width(p) for p in outputs)
    vec_path = args.output / "csr_storage_vectors.mem"
    with vec_path.open("w", encoding="ascii", newline="\n") as f:
        for iv, ov, chain_flag in zip(input_words, expected_words, chain_flags):
            ib = "".join(format(int(iv[p["name"]]), f"0{width(p)}b") for p in inputs)
            ob = "".join(format(int(ov[p["name"]]), f"0{width(p)}b") for p in outputs)
            f.write(format(int(str(int(chain_flag)) + ib + ob, 2), f"0{(in_width+out_width+4)//4}x") + "\n")
    sv = ["module tb;", f"localparam N={len(input_words)};", f"reg [{in_width-1}:0] in_vec;", f"reg [{out_width-1}:0] expected;", f"reg [{in_width+out_width}:0] vectors [0:N-1];", "reg chain_flag; reg previous_chain;", "integer i;", f"wire [{out_width-1}:0] out_vec;"]
    for p in inputs: sv.append(f"reg [{width(p)-1}:0] {p['name']};")
    for p in outputs: sv.append(f"wire [{width(p)-1}:0] {p['name']};")
    offset = in_width
    for p in inputs:
        w = width(p); offset -= w
    sv.append(f"{doc['topModule']} dut (" + ",".join(f".{p['name']}({p['name']})" for p in top["ports"]) + ");")
    sv.append("assign out_vec = {" + ",".join(p["name"] for p in outputs) + "};")
    mem_literal = vec_path.resolve().as_posix().replace('"', '\\"')
    sv += ["initial begin", f"$readmemh(\"{mem_literal}\", vectors);", "previous_chain = 0;", "for (i=0;i<N;i=i+1) begin", f"in_vec = vectors[i][{in_width+out_width-1}:{out_width}];", f"expected = vectors[i][{out_width-1}:0];", f"chain_flag = vectors[i][{in_width+out_width}];"]
    offset = in_width
    for p in inputs:
        w = width(p); offset -= w
        if p["name"].startswith("current_"):
            sv.append(f"if (!chain_flag || !previous_chain) {p['name']} = in_vec[{offset} +: {w}];")
        else:
            sv.append(f"{p['name']} = in_vec[{offset} +: {w}];")
    sv += ["#1;", "if (out_vec !== expected) begin $display(\"FAIL vector=%0d chain=%0d expected=%h actual=%h\", i, chain_flag, expected, out_vec); $fatal(1); end", "if (chain_flag) begin"]
    for p in outputs:
        if p["name"].startswith("next_"):
            sv.append(f"current_{p['name'][5:]} = {p['name']};")
    sv += ["end", "previous_chain = chain_flag;", "end", f"$display(\"PASS CSR storage event RTL vectors={len(input_words)} chained={len(chain_events)}\");", "$finish;", "end", "endmodule"]
    tb = args.output / "csr_storage_event_tb.sv"
    tb.write_text("\n".join(sv) + "\n", encoding="utf-8")
    rtl = args.output / "csr_storage_event.sv"
    circt_opt = args.circt_bin / "circt-opt.exe"
    with rtl.open("w", encoding="utf-8", newline="\n") as out:
        subprocess.run([str(circt_opt), str(args.mlir), "--lower-comb", "--export-verilog", "-o", "NUL"], check=True, text=True, stdout=out)
    image = args.output / "csr_storage_event.vvp"
    env = os.environ.copy()
    env["PATH"] = str(args.oss_bin) + ";" + str(args.oss_bin.parent / "lib") + ";" + env.get("PATH", "")
    compile_run = subprocess.run([str(args.oss_bin / "iverilog.exe"), "-g2012", "-s", "tb", "-o", str(image), str(rtl), str(tb)], cwd=args.output, env=env, text=True, capture_output=True)
    (args.output / "csr_storage_event_compile.log").write_text(compile_run.stdout + compile_run.stderr, encoding="utf-8")
    if compile_run.returncode:
        raise RuntimeError("Icarus compile failed: " + compile_run.stdout + compile_run.stderr)
    sim = subprocess.run([str(args.oss_bin / "vvp.exe"), str(image)], cwd=args.output, env=env, check=True, text=True, capture_output=True)
    (args.output / "csr_storage_event_simulation.log").write_text(sim.stdout + sim.stderr, encoding="utf-8")
    if f"PASS CSR storage event RTL vectors={len(input_words)} chained={len(chain_events)}" not in sim.stdout:
        raise RuntimeError("Icarus did not report the full vector pass")
    mutant_text, replacements = re.subn(r"assign\s+write_enable\s*=\s*[^;]+;", "assign write_enable = 1'b0;", rtl.read_text(encoding="utf-8"), count=1)
    if replacements != 1:
        raise RuntimeError("Could not construct the targeted write_enable mutant")
    mutant = args.output / "csr_storage_event_zero_write_enable_mutant.sv"
    mutant.write_text(mutant_text, encoding="utf-8")
    mutant_image = args.output / "csr_storage_event_mutant.vvp"
    mutant_compile = subprocess.run([str(args.oss_bin / "iverilog.exe"), "-g2012", "-s", "tb", "-o", str(mutant_image), str(mutant), str(tb)], cwd=args.output, env=env, text=True, capture_output=True)
    (args.output / "csr_storage_event_mutant_compile.log").write_text(mutant_compile.stdout + mutant_compile.stderr, encoding="utf-8")
    if mutant_compile.returncode:
        raise RuntimeError("Icarus mutant compile failed: " + mutant_compile.stdout + mutant_compile.stderr)
    mutant_run = subprocess.run([str(args.oss_bin / "vvp.exe"), str(mutant_image)], cwd=args.output, env=env, text=True, capture_output=True)
    mutant_log = mutant_run.stdout + mutant_run.stderr
    (args.output / "csr_storage_event_mutant_simulation.log").write_text(mutant_log, encoding="utf-8")
    if mutant_run.returncode == 0 or "FAIL vector=" not in mutant_log:
        raise RuntimeError("Independent vectors failed to detect zero-write-enable mutant")
    receipt = {
        "schema": "circuitweave.csr-storage-event-rtl-crosscheck/v1",
        "status": "PASS_SERIALIZED_SOFTWARE_EVENT_RTL_ONLY",
        "vectors": len(input_words),
        "singleEventVectors": len(input_words) - len(chain_events),
        "chainedEventVectors": len(chain_events),
        "oracle": "independently coded Python policy/storage transition equations derived from published 40-row source inventory; not Golden equivalence",
        "scope": "combinational serialized software CsrFile events; the testbench feeds each next-value output into the next event sample for chained vectors; this external sample recurrence does not implement a physical clock/reset/arbitration/RAM latency/CPU retire timing",
        "inputs": {p.name: sha(p) for p in (args.hwir, args.inventory, args.mlir)},
        "rtlSha256": sha(rtl), "testbenchSha256": sha(tb), "vectorsSha256": sha(vec_path),
        "mutant": {"mutation": "force write_enable low", "rtlSha256": sha(mutant), "detected": True, "firstMismatch": re.search(r"FAIL vector=\d+", mutant_log).group(0)},
        "simulationLogSha256": hashlib.sha256((sim.stdout + sim.stderr).encode()).hexdigest(),
        "simulation": sim.stdout.strip(),
        "runtimeComparison": runtime_comparison
    }
    receipt_path = args.output / "csr-storage-event-rtl-receipt.json"
    receipt_path.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(receipt, indent=2))


if __name__ == "__main__":
    main()
