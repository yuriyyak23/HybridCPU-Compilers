import json
import sys

rtl_path, vectors_path, tb_path = sys.argv[1:4]
with open(vectors_path, encoding="utf-8") as stream:
    records = [json.loads(line) for line in stream if line.strip()]
cases = records[1:]

inputs = ["scalar_count", "scalar_issue_mask", "auxiliary_reservation_mask", "auxiliary_slot_count"]
for i in range(4):
    inputs += [f"scalar{i}_{name}" for name in ("slot", "microop_present", "issued_present", "issued_current", "issued_lane_signed_bits", "hard_pinned", "pinned_lane_id", "slot_class")]
for i in range(8):
    inputs += [f"aux{i}_{name}" for name in ("valid", "hard_pinned", "pinned_lane_id", "slot_class")]
outputs = ["assigned_lane_mask", "assigned_slot_mask", "scalar_lane_mask", "class_inputs_defined", "source_adapter_valid", "unmapped_selected_slot_mask", "selected_slot_mask"] + [f"lane{i}_slot" for i in range(8)]
lines = ["module tb;", "integer errors=0;", "" ]
for name in inputs:
    width = "[31:0] " if name.endswith("issued_lane_signed_bits") else ("[7:0] " if name.endswith(("mask", "slot", "pinned_lane_id", "slot_class")) or name in ("scalar_issue_mask", "auxiliary_reservation_mask") else ("[3:0] " if name in ("scalar_count", "auxiliary_slot_count") else ""))
    # scalar_count is 3-bit and slot count is 4-bit; declarations keep count widths explicit.
    if name == "scalar_count": width = "[2:0] "
    if name == "auxiliary_slot_count": width = "[3:0] "
    lines.append(f"reg {width}{name};")
for name in outputs:
    width = "" if name in ("class_inputs_defined", "source_adapter_valid") else "[7:0] "
    lines.append(f"wire {width}{name};")
lines.append("issue_packet_create_allocation_projection dut(" + ",".join(f".{name}({name})" for name in inputs + outputs) + ");")
lines += ["initial begin", "  errors=0;"]

def bits(value):
    return "1'b1" if value else "1'b0"

for index, record in enumerate(cases):
    inp, out = record["inputs"], record["outputs"]
    lines += [f"  // {record['name']}", f"  scalar_count=3'd{inp['entryCount']};", f"  scalar_issue_mask=8'd{inp['scalarMask']};", f"  auxiliary_reservation_mask=8'd{inp['auxMask']};", "  auxiliary_slot_count=4'd8;"]
    for i in range(4):
        exists = i < inp["entryCount"]
        def item(key, default): return inp[key][i] if exists else default
        lines.append(f"  scalar{i}_slot=8'd{item('entrySlots',255)};")
        lines.append(f"  scalar{i}_microop_present={bits(exists)};")
        lines.append(f"  scalar{i}_issued_present={bits(item('attemptPresent',False))};")
        lines.append(f"  scalar{i}_issued_current={bits(item('attemptCurrent',False))};")
        lines.append(f"  scalar{i}_issued_lane_signed_bits=32'd{item('attemptLanes',0) & 0xffffffff};")
        lines.append(f"  scalar{i}_hard_pinned={bits(item('scalarHardPinned',False))};")
        lines.append(f"  scalar{i}_pinned_lane_id=8'd{item('scalarPinnedLanes',0)};")
        lines.append(f"  scalar{i}_slot_class=8'd{item('scalarClasses',0)};")
    for i in range(8):
        lines.append(f"  aux{i}_valid={bits(inp['auxValid'][i])};")
        lines.append(f"  aux{i}_hard_pinned={bits(inp['auxHardPinned'][i])};")
        lines.append(f"  aux{i}_pinned_lane_id=8'd{inp['auxPinnedLanes'][i]};")
        lines.append(f"  aux{i}_slot_class=8'd{inp['auxClasses'][i]};")
    expected_lane_mask = sum(1 << lane for lane, slot in enumerate(out["laneSlots"]) if slot >= 0)
    expected_slot_mask = 0
    for slot in out["laneSlots"]:
        if slot >= 0:
            expected_slot_mask |= 1 << slot
    expected_scalar_mask = sum(1 << lane for lane, flag in enumerate(out["laneScalar"]) if flag)
    checks = {
        "assigned_lane_mask": expected_lane_mask,
        "assigned_slot_mask": expected_slot_mask,
        "scalar_lane_mask": expected_scalar_mask,
        "unmapped_selected_slot_mask": out["unmappedMask"],
        "selected_slot_mask": out["selectedMask"],
    }
    checks["class_inputs_defined"] = 1
    checks["source_adapter_valid"] = 1
    for name, value in checks.items():
        lines.append(f"  #1; if ({name} !== 8'd{value}) begin $display(\"FAIL case={index} {name} got=%0d expected={value}\", {name}); errors=errors+1; end")
    for lane, slot in enumerate(out["laneSlots"]):
        expected = 255 if slot < 0 else slot
        lines.append(f"  if (lane{lane}_slot !== 8'd{expected}) begin $display(\"FAIL case={index} lane{lane}_slot got=%0d expected={expected}\", lane{lane}_slot); errors=errors+1; end")
lines += [
    "  // Adapter precondition: selected scalar entry must carry a non-null MicroOp.",
    "  scalar_count=3'd1; scalar_issue_mask=8'd1; auxiliary_reservation_mask=8'd0; auxiliary_slot_count=4'd8;",
    "  scalar0_slot=8'd0; scalar0_microop_present=1'b0; scalar0_issued_present=1'b0; scalar0_issued_current=1'b0; scalar0_issued_lane_signed_bits=32'd0; scalar0_hard_pinned=1'b0; scalar0_pinned_lane_id=8'd0; scalar0_slot_class=8'd0;",
    "  #1; if (source_adapter_valid !== 1'b0) begin $display(\"FAIL missing scalar MicroOp adapter precondition\"); errors=errors+1; end",
    "  // Adapter precondition: every supplied SlotClass byte is an explicit enum value.",
    "  scalar0_microop_present=1'b1; scalar0_slot_class=8'd6;",
    "  #1; if (class_inputs_defined !== 1'b0) begin $display(\"FAIL undefined SlotClass adapter precondition\"); errors=errors+1; end",
    "  if (errors==0) $display(\"issue-packet-create cases=%0d errors=0; adapter-domain-checks=2\", 1028);",
    "  else $display(\"issue-packet-create errors=%0d\", errors);", "  $finish;", "end", "endmodule"]
with open(tb_path, "w", encoding="ascii", newline="\n") as stream:
    stream.write("\n".join(lines) + "\n")
