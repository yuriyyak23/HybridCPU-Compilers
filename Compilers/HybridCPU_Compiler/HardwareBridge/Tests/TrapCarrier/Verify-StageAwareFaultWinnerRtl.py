import argparse
import copy
import hashlib
import json
import os
import pathlib
import random
import subprocess
import tempfile

TRANSLATION_FAULT_FIELDS = {
    "reason": 8, "scope_domain_id": 64, "scope_context_id": 32, "scope_virtual_thread_id": 32,
    "access_kind": 8, "virtual_address": 64, "access_size": 16,
    "operation_attempt_id": 64, "operation_event_id": 64, "memory_operation_id": 64,
    "owner_epoch": 64, "qualification": 64, "auxiliary": 64, "fault_stage": 8,
    "has_guest_physical_address": 1, "guest_physical_address": 64,
    "memory_domain_owner_epoch": 64, "address_space_generation": 64,
    "address_space_identity": 64, "failure_offset": 16, "translation_generation": 64,
    "has_request_start_guest_physical_address": 1, "request_start_guest_physical_address": 64,
}


def translation_fault_state(seed=0):
    return {name: ((seed + 1) * 0x1020304050607081 + index * 0x0101010101010101) & ((1 << width) - 1)
            for index, (name, width) in enumerate(TRANSLATION_FAULT_FIELDS.items())}


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def stage_state(explicit=False):
    return {"uses_explicit_packet_lanes": int(explicit), "lanes": [
        {"occupied": 0, "has_fault": 0, "slot_index": 0,
         "fault_address": 0, "fault_is_write": 0, "owner_thread_id": 0,
         "virtual_thread_id": 0, "pc": 0, "has_cpu_translation_fault": 0,
         "translation_fault": translation_fault_state(i)} for i in range(8)]}


def put(row, stage, lane, *, occupied=1, fault=1, slot=0, address=0, is_write=0):
    row[stage]["lanes"][lane] = {"occupied": occupied, "has_fault": fault,
                                  "slot_index": slot,
                                  "fault_address": address, "fault_is_write": is_write,
                                  "owner_thread_id": lane - 3, "virtual_thread_id": lane % 4,
                                  "pc": 0x1000 + lane * 4 + {"wb": 0x3000, "mem": 0x2000, "ex": 0x1000}[stage],
                                  "has_cpu_translation_fault": int((lane + {"wb": 1, "mem": 2, "ex": 0}[stage]) % 3 == 0),
                                  "translation_fault": translation_fault_state(100 + lane + {"wb": 0, "mem": 16, "ex": 32}[stage])}


def runtime_vectors():
    # Arbitration/fault facts come from the source-instrumented v195 scenarios;
    # added identity/PC/optional-fault facts are deterministic synthetic inputs.
    vectors = []
    wb = {"scenario": "runtime-writeback-over-memory", "evidence": "v195-active-golden-runtime",
          "incoming_fault_valid": 1, "incoming_fault_address": 0xB200, "incoming_fault_is_write": 0,
          "incoming_active_mem_lane": 5, "wb": stage_state(True), "mem": stage_state(True), "ex": stage_state(True)}
    put(wb, "wb", 7, slot=0, address=0xB100, is_write=1); put(wb, "mem", 5, slot=5, address=0xB200); put(wb, "ex", 1, slot=0, address=0xA200)
    vectors.append(wb)
    mem = {"scenario": "runtime-memory-over-execute", "evidence": "v195-active-golden-runtime",
           "incoming_fault_valid": 1, "incoming_fault_address": 0xA100, "incoming_fault_is_write": 0,
           "incoming_active_mem_lane": 5, "wb": stage_state(True), "mem": stage_state(True), "ex": stage_state(True)}
    put(mem, "mem", 5, slot=5, address=0xA100); put(mem, "ex", 1, slot=0, address=0xA200)
    vectors.append(mem)
    dual = {"scenario": "runtime-dual-memory-slot-order", "evidence": "v195-active-golden-runtime",
            "incoming_fault_valid": 1, "incoming_fault_address": 0xC100, "incoming_fault_is_write": 0,
            "incoming_active_mem_lane": 5, "wb": stage_state(True), "mem": stage_state(True), "ex": stage_state(True)}
    put(dual, "mem", 5, slot=5, address=0xC100); put(dual, "mem", 2, slot=0, address=0xC050); put(dual, "ex", 1, slot=0, address=0xC200)
    vectors.append(dual)
    return vectors


def production_translation_fault_vector(active_path: str, golden_path: str, comparison_path: str) -> dict:
    active = json.loads(pathlib.Path(active_path).read_text(encoding="utf-8"))
    golden = json.loads(pathlib.Path(golden_path).read_text(encoding="utf-8"))
    comparison = json.loads(pathlib.Path(comparison_path).read_text(encoding="utf-8"))
    if comparison.get("activeGoldenCommonFieldsEqual") is not True:
        raise RuntimeError("production translation-fault active/Golden common fields differ")
    af, gf = active["fault"], golden["fault"]
    def common_payload(record: dict) -> dict:
        return {key: value for key, value in record.items() if key != "optionalActiveFields"}
    if common_payload(af) != common_payload(gf):
        raise RuntimeError("production translation-fault nested records differ between active and Golden")
    row = {"scenario": "runtime-" + active["scenario"], "evidence": "active-golden-ExecutePipelineCycle",
           "incoming_fault_valid": 0, "incoming_fault_address": 0, "incoming_fault_is_write": 0,
           "incoming_active_mem_lane": 0, "wb": stage_state(), "mem": stage_state(), "ex": stage_state()}
    put(row, "wb", 0)
    lane = row["wb"]["lanes"][0]
    lane["has_cpu_translation_fault"] = 1
    request = af["request"]
    fields = {"reason": af["reason"], "scope_domain_id": request["scope"]["domainId"],
              "scope_context_id": request["scope"]["contextId"], "scope_virtual_thread_id": request["scope"]["virtualThreadId"],
              "access_kind": request["accessKind"], "virtual_address": request["virtualAddress"], "access_size": request["accessSize"],
              "operation_attempt_id": request["operationIdentity"]["attemptId"], "operation_event_id": request["operationIdentity"]["eventId"],
              "memory_operation_id": request["operationIdentity"]["memoryOperationId"], "owner_epoch": af["ownerEpoch"],
              "qualification": af["qualification"], "auxiliary": af["auxiliary"], "fault_stage": af["faultStage"],
              "has_guest_physical_address": int(af["hasGuestPhysicalAddress"]), "guest_physical_address": af["guestPhysicalAddress"],
              "memory_domain_owner_epoch": af["memoryDomainOwnerEpoch"], "address_space_generation": af["addressSpaceGeneration"],
              "address_space_identity": af["addressSpaceIdentity"], "failure_offset": af["optionalActiveFields"]["failureOffset"],
              "translation_generation": af["optionalActiveFields"]["translationGeneration"],
              "has_request_start_guest_physical_address": int(af["optionalActiveFields"]["hasRequestStartGuestPhysicalAddress"]),
              "request_start_guest_physical_address": af["optionalActiveFields"]["requestStartGuestPhysicalAddress"] or 0}
    if any(value is None for value in fields.values()):
        raise RuntimeError("runtime record lacks an active RTL payload field")
    lane["translation_fault"] = fields
    return row


def production_scalar_memory_translation_fault_vectors(active_path: str, golden_path: str) -> list[dict]:
    active_rows = json.loads(pathlib.Path(active_path).read_text(encoding="utf-8"))
    golden_rows = json.loads(pathlib.Path(golden_path).read_text(encoding="utf-8"))
    if len(active_rows) != 2 or len(golden_rows) != 2:
        raise RuntimeError("expected exactly two active/Golden production scalar memory fault records")
    vectors = []
    expected = {
        "production-scalar-load-unmapped": (1, 2, 0x4000, 1, False, 0),
        "production-scalar-store-access-denied": (2, 3, 0x2080, 1, False, 0),
        "production-second-stage-scalar-load-violation": (5, 2, 0x1000, 2, True, 0x3000),
        "production-second-stage-scalar-store-violation": (5, 3, 0x1000, 2, True, 0x3000),
    }
    for active, golden in zip(active_rows, golden_rows):
        if active.get("scenario") != golden.get("scenario") or active.get("isValid") != golden.get("isValid"):
            raise RuntimeError("active/Golden scalar memory fault scenario or validity differs")
        af, gf = active["fault"], golden["fault"]
        common_active = {key: value for key, value in af.items() if key != "optionalActiveFields"}
        common_golden = {key: value for key, value in gf.items() if key != "optionalActiveFields"}
        if common_active != common_golden:
            raise RuntimeError(f"active/Golden common scalar memory fault fields differ: {active['scenario']}")
        request = af["request"]
        payload = {"reason": af["reason"], "scope_domain_id": request["scope"]["domainId"],
                   "scope_context_id": request["scope"]["contextId"], "scope_virtual_thread_id": request["scope"]["virtualThreadId"],
                   "access_kind": request["accessKind"], "virtual_address": request["virtualAddress"], "access_size": request["accessSize"],
                   "operation_attempt_id": request["operationIdentity"]["attemptId"], "operation_event_id": request["operationIdentity"]["eventId"],
                   "memory_operation_id": request["operationIdentity"]["memoryOperationId"], "owner_epoch": af["ownerEpoch"],
                   "qualification": af["qualification"], "auxiliary": af["auxiliary"], "fault_stage": af["faultStage"],
                   "has_guest_physical_address": int(af["hasGuestPhysicalAddress"]), "guest_physical_address": af["guestPhysicalAddress"],
                   "memory_domain_owner_epoch": af["memoryDomainOwnerEpoch"], "address_space_generation": af["addressSpaceGeneration"],
                   "address_space_identity": af["addressSpaceIdentity"],
                   "failure_offset": af["optionalActiveFields"]["failureOffset"],
                   "translation_generation": af["optionalActiveFields"]["translationGeneration"],
                   "has_request_start_guest_physical_address": int(af["optionalActiveFields"]["hasRequestStartGuestPhysicalAddress"]),
                   "request_start_guest_physical_address": af["optionalActiveFields"]["requestStartGuestPhysicalAddress"] or 0}
        if any(value is None for value in payload.values()):
            raise RuntimeError(f"active scalar memory record lacks RTL payload facts: {active['scenario']}")
        contract = expected.get(active["scenario"])
        if contract is None or (payload["reason"], payload["access_kind"], payload["virtual_address"],
                                payload["fault_stage"], bool(payload["has_guest_physical_address"]),
                                payload["guest_physical_address"]) != contract:
            raise RuntimeError(f"scalar memory runtime facts do not match the expected source contract: {active['scenario']}")
        row = {"scenario": "runtime-" + active["scenario"], "evidence": "active-golden-production-scalar-memory-caller",
               "incoming_fault_valid": 0, "incoming_fault_address": 0, "incoming_fault_is_write": 0,
               "incoming_active_mem_lane": 0, "wb": stage_state(), "mem": stage_state(), "ex": stage_state()}
        put(row, "wb", 0)
        row["wb"]["lanes"][0]["has_cpu_translation_fault"] = 1
        row["wb"]["lanes"][0]["translation_fault"] = payload
        vectors.append(row)
    return vectors


def directed_vectors():
    vectors = []
    def fresh(name, **explicit):
        return {"scenario": name, "evidence": "directed-source-contract-vector", "incoming_fault_valid": 1,
                "incoming_fault_address": 0xDD00, "incoming_fault_is_write": 0, "incoming_active_mem_lane": 5,
                **{s: stage_state(explicit.get(s, False)) for s in ("wb", "mem", "ex")}}
    vectors.append(fresh("no-fault-none-sentinel"))
    row = fresh("wb-priority-lane-seven", wb=True, mem=True, ex=True)
    put(row, "wb", 7); put(row, "mem", 0); put(row, "ex", 0); vectors.append(row)
    row = fresh("mem-explicit-lower-slot-wins", mem=True)
    put(row, "mem", 2, slot=6); put(row, "mem", 5, slot=1); vectors.append(row)
    row = fresh("equal-slot-falls-back-to-low-physical-lane", mem=True)
    put(row, "mem", 2, slot=3); put(row, "mem", 5, slot=3); vectors.append(row)
    row = fresh("slot-eight-falls-back-to-physical-lane", mem=True)
    put(row, "mem", 2, slot=7); put(row, "mem", 5, slot=8); vectors.append(row)
    row = fresh("negative-signed-slot-participates-and-orders", mem=True)
    put(row, "mem", 2, slot=0); put(row, "mem", 5, slot=-1); vectors.append(row)
    row = fresh("legacy-physical-lane-order", mem=False)
    put(row, "mem", 2); put(row, "mem", 5); vectors.append(row)
    row = fresh("execute-only-lane-six", ex=True)
    put(row, "ex", 6); vectors.append(row)
    row = fresh("wb-dominates-mem-and-ex", wb=True, mem=True, ex=True)
    put(row, "wb", 0); put(row, "mem", 7); put(row, "ex", 7); vectors.append(row)
    # Mixed usable/unusable key order is deliberately non-transitive under the source pairwise fallback rule.
    row = fresh("mixed-key-fallback-scans-in-lane-order", mem=True)
    put(row, "mem", 0, slot=7); put(row, "mem", 1, slot=8); put(row, "mem", 2, slot=0); vectors.append(row)
    base = fresh("translation-fault-valid-baseline", wb=True)
    put(base, "wb", 3)
    lane = base["wb"]["lanes"][3]
    lane["has_cpu_translation_fault"] = 1
    lane["translation_fault"] = {name: 0 for name in TRANSLATION_FAULT_FIELDS}
    lane["translation_fault"].update({"reason": 1, "scope_domain_id": 1, "scope_context_id": 1,
        "scope_virtual_thread_id": 0, "access_kind": 1, "virtual_address": (1 << 64) - 2,
        "access_size": 4, "operation_attempt_id": 1, "operation_event_id": 1,
        "memory_operation_id": 1, "owner_epoch": 1, "fault_stage": 1, "failure_offset": 3})
    mutations = [
        ("translation-fault-boundaries-valid-vtid3-overflow", {}),
        ("translation-fault-invalid-no-payload", {"has_cpu_translation_fault": 0}),
        ("translation-fault-invalid-reason-none", {"reason": 0}),
        ("translation-fault-invalid-domain-zero", {"scope_domain_id": 0}),
        ("translation-fault-invalid-context-zero", {"scope_context_id": 0}),
        ("translation-fault-invalid-context-negative", {"scope_context_id": 0xffffffff}),
        ("translation-fault-invalid-vtid-negative", {"scope_virtual_thread_id": 0xffffffff}),
        ("translation-fault-boundary-vtid-zero", {"scope_virtual_thread_id": 0}),
        ("translation-fault-boundary-vtid-three", {"scope_virtual_thread_id": 3}),
        ("translation-fault-invalid-vtid-four", {"scope_virtual_thread_id": 4}),
        ("translation-fault-invalid-access-kind-none", {"access_kind": 0}),
        ("translation-fault-unknown-access-kind-accepted", {"access_kind": 255}),
        ("translation-fault-invalid-size-zero", {"access_size": 0}),
        ("translation-fault-invalid-attempt-zero", {"operation_attempt_id": 0}),
        ("translation-fault-invalid-event-zero", {"operation_event_id": 0}),
        ("translation-fault-invalid-memory-operation-zero", {"memory_operation_id": 0}),
        ("translation-fault-invalid-owner-epoch-zero", {"owner_epoch": 0}),
        ("translation-fault-invalid-offset-equals-size", {"failure_offset": 4}),
        ("translation-fault-unknown-stage-treated-as-stage-one", {"fault_stage": 255}),
        ("translation-fault-invalid-second-stage-missing-gpa", {"fault_stage": 2}),
        ("translation-fault-valid-second-stage-complete", {"fault_stage": 2, "has_guest_physical_address": 1,
         "memory_domain_owner_epoch": 2, "address_space_generation": 3, "address_space_identity": 4}),
        ("translation-fault-invalid-second-stage-missing-owner", {"fault_stage": 2, "has_guest_physical_address": 1,
         "address_space_generation": 3, "address_space_identity": 4}),
        ("translation-fault-no-overflow", {"virtual_address": 0x1000, "failure_offset": 3}),
    ]
    for name, updates in mutations:
        row = copy.deepcopy(base); row["scenario"] = name
        if "has_cpu_translation_fault" in updates:
            row["wb"]["lanes"][3]["has_cpu_translation_fault"] = updates["has_cpu_translation_fault"]
        for field, value in updates.items():
            if field != "has_cpu_translation_fault":
                row["wb"]["lanes"][3]["translation_fault"][field] = value
        vectors.append(row)
    return vectors


def random_vectors(count=256):
    rng = random.Random(0x6946CCAB)
    result = []
    for n in range(count):
        row = {"scenario": f"seeded-random-{n:04d}", "evidence": "deterministic-synthetic",
               "incoming_fault_valid": rng.randrange(2), "incoming_fault_address": rng.getrandbits(64),
               "incoming_fault_is_write": rng.randrange(2), "incoming_active_mem_lane": rng.randrange(8),
               **{s: stage_state(bool(rng.getrandbits(1))) for s in ("wb", "mem", "ex")}}
        for stage in ("wb", "mem", "ex"):
            for lane in range(8):
                occupied = int(rng.random() < 0.58)
                fault = int(rng.random() < 0.34)
                slot = rng.randrange(-8, 12)
                row[stage]["lanes"][lane] = {"occupied": occupied, "has_fault": fault,
                                               "slot_index": slot,
                                               "fault_address": rng.getrandbits(64), "fault_is_write": rng.randrange(2),
                                               "owner_thread_id": rng.randrange(-4, 9), "virtual_thread_id": rng.randrange(-1, 5),
                                               "pc": rng.getrandbits(64), "has_cpu_translation_fault": rng.randrange(2),
                                               "translation_fault": translation_fault_state(1000 + n * 24 + lane + {"wb": 0, "mem": 8, "ex": 16}[stage])}
        result.append(row)
    return result


def resolve_stage(stage: dict) -> tuple[bool, int]:
    found = False
    current_lane = 255
    current_key = 0
    current_key_valid = False
    explicit = bool(stage["uses_explicit_packet_lanes"])
    for lane, state in enumerate(stage["lanes"]):
        eligible = bool(state["occupied"] and state["has_fault"])
        if not eligible:
            continue
        key_valid = bool(explicit and state["occupied"] and state["slot_index"] < 8)
        replace = not found or (key_valid and current_key_valid and state["slot_index"] < current_key)
        if replace:
            found, current_lane = True, lane
            current_key, current_key_valid = state["slot_index"], key_valid
    return found, current_lane


def expected(row: dict) -> tuple[int, int, int]:
    for stage, value in (("wb", 5), ("mem", 4), ("ex", 3)):
        found, lane = resolve_stage(row[stage])
        if found:
            return 1, value, lane
    return 0, 0, 255


def attach_runtime_metadata(rows: list[dict], active_path: str, golden_path: str) -> int:
    def read(path: str) -> dict[str, dict]:
        found = {}
        for line in pathlib.Path(path).read_text(encoding="utf-8").splitlines():
            if line.strip():
                row = json.loads(line)
                if row.get("scenario") in {"writeback-over-memory", "memory-over-execute", "dual-memory-winner"}:
                    found[row["scenario"]] = row
        return found
    active, golden = read(active_path), read(golden_path)
    mapping = {"runtime-writeback-over-memory": "writeback-over-memory",
               "runtime-memory-over-execute": "memory-over-execute",
               "runtime-dual-memory-slot-order": "dual-memory-winner"}
    compared = 0
    scalar_keys = ("winnerOwnerThreadId", "winnerVirtualThreadId", "winnerPc", "winnerCpuTranslationFaultHasValue")
    for row in rows:
        if row["scenario"] not in mapping:
            continue
        name = mapping[row["scenario"]]
        if name not in active or name not in golden:
            raise RuntimeError(f"missing active/Golden runtime metadata observation: {name}")
        a, g = active[name], golden[name]
        keys = ("winnerStage", "winnerLane", "winnerAddress", "winnerIsWrite", *scalar_keys)
        if any(a.get(key) != g.get(key) for key in keys):
            raise RuntimeError(f"active/Golden runtime winner tuple differs: {name}")
        stage = {"WriteBack": "wb", "Memory": "mem", "Execute": "ex"}.get(a["winnerStage"])
        if stage is None:
            raise RuntimeError(f"unexpected runtime winner stage {a['winnerStage']}")
        valid, code, lane = expected(row)
        if not valid or {5: "WriteBack", 4: "Memory", 3: "Execute"}[code] != a["winnerStage"] or lane != a["winnerLane"]:
            raise RuntimeError(f"runtime winner does not match RTL source-fact scenario: {name}")
        facts = row[stage]["lanes"][lane]
        facts["fault_address"] = a["winnerAddress"]
        facts["fault_is_write"] = int(a["winnerIsWrite"])
        facts["owner_thread_id"] = a["winnerOwnerThreadId"]
        facts["virtual_thread_id"] = a["winnerVirtualThreadId"]
        facts["pc"] = a["winnerPc"]
        facts["has_cpu_translation_fault"] = int(a["winnerCpuTranslationFaultHasValue"])
        compared += 1
    if compared != 3:
        raise RuntimeError(f"expected three runtime metadata observations, found {compared}")
    return compared


def make_tb(rows: list[dict]) -> str:
    lines = ["module tb;"]
    for stage in ("wb", "mem", "ex"):
        lines.append(f"reg {stage}_uses_explicit_packet_lanes;")
        for lane in range(8):
            lines.extend([f"reg {stage}_lane{lane}_occupied;", f"reg {stage}_lane{lane}_has_fault;",
                          f"reg [31:0] {stage}_lane{lane}_slot_index;"])
    lines.extend(["wire winner_valid;", "wire [2:0] winner_stage;", "wire [7:0] winner_lane;",
                  "stage_aware_fault_winner dut(.*);", "initial begin"])
    for index, row in enumerate(rows):
        for stage in ("wb", "mem", "ex"):
            lines.append(f"  {stage}_uses_explicit_packet_lanes=1'b{row[stage]['uses_explicit_packet_lanes']};")
            for lane, state in enumerate(row[stage]["lanes"]):
                lines.append(f"  {stage}_lane{lane}_occupied=1'b{state['occupied']}; {stage}_lane{lane}_has_fault=1'b{state['has_fault']}; ")
                lines.append(f"  {stage}_lane{lane}_slot_index=32'h{state['slot_index'] & 0xffffffff:08x};")
        valid, stage, lane = expected(row)
        lines.append(f"  #1; if ({{winner_valid,winner_stage,winner_lane}} !== 12'h{(valid << 11) | (stage << 8) | lane:03x}) $fatal(1, \"{index}:{row['scenario']}\");")
    lines.extend([f"  $display(\"PASS vectors={len(rows)}\"); $finish;", "end", "endmodule", ""])
    return "\n".join(lines)


def expected_composed(row: dict) -> tuple[int, int]:
    winner_valid, winner_stage, winner_lane = expected(row)
    address, is_write = 0, 0
    if winner_valid:
        stage_name = {5: "wb", 4: "mem", 3: "ex"}[winner_stage]
        lane = row[stage_name]["lanes"][winner_lane]
        address, is_write = lane["fault_address"], lane["fault_is_write"]
    incoming = bool(row["incoming_fault_valid"])
    delivery_valid = incoming and bool(winner_valid) and winner_stage in (4, 5)
    delivery_rejected = incoming and (not winner_valid or winner_stage not in (4, 5))
    selected_lane = winner_lane if delivery_valid and winner_stage == 4 else row["incoming_active_mem_lane"]
    thrown_address = address if winner_stage == 5 else row["incoming_fault_address"]
    thrown_write = is_write if winner_stage == 5 else row["incoming_fault_is_write"]
    packed = ((address << 73) | (is_write << 72) | (int(delivery_rejected) << 71) | (int(delivery_valid) << 70) |
              (selected_lane << 67) | (thrown_address << 3) | (thrown_write << 2) | (int(delivery_valid) << 1) | int(delivery_valid))
    owner, virtual_thread, pc, has_translation_fault = 0, 0, 0, 0
    if winner_valid:
        winner = row[{5: "wb", 4: "mem", 3: "ex"}[winner_stage]]["lanes"][winner_lane]
        owner, virtual_thread, pc = winner["owner_thread_id"], winner["virtual_thread_id"], winner["pc"]
        has_translation_fault = winner["has_cpu_translation_fault"]
    metadata = (((owner & 0xffffffff) << 97) | ((virtual_thread & 0xffffffff) << 65) |
                ((pc & 0xffffffffffffffff) << 1) | has_translation_fault)
    return (metadata << 137) | packed, 266


def expected_translation_fault(row: dict) -> dict[str, int]:
    winner_valid, winner_stage, winner_lane = expected(row)
    if not winner_valid:
        return {name: 0 for name in TRANSLATION_FAULT_FIELDS}
    stage_name = {5: "wb", 4: "mem", 3: "ex"}[winner_stage]
    return row[stage_name]["lanes"][winner_lane]["translation_fault"]


def expected_translation_fault_semantics(row: dict) -> tuple[int, int, int]:
    payload = expected_translation_fault(row)
    winner_valid, _, _ = expected(row)
    if not winner_valid or not row[{5: "wb", 4: "mem", 3: "ex"}[expected(row)[1]]]["lanes"][expected(row)[2]]["has_cpu_translation_fault"]:
        return 0, 0, 0
    signed = lambda x: x - (1 << 32) if x & (1 << 31) else x
    scope_valid = (payload["scope_domain_id"] != 0 and signed(payload["scope_context_id"]) > 0 and
                   0 <= signed(payload["scope_virtual_thread_id"]) < 4)
    request_valid = (scope_valid and payload["access_kind"] != 0 and payload["access_size"] != 0 and
                     payload["operation_attempt_id"] != 0 and payload["operation_event_id"] != 0 and
                     payload["memory_operation_id"] != 0)
    second_stage_valid = (payload["has_guest_physical_address"] != 0 and
                          payload["memory_domain_owner_epoch"] != 0 and
                          payload["address_space_generation"] != 0 and payload["address_space_identity"] != 0)
    is_valid = (payload["reason"] != 0 and request_valid and
                payload["failure_offset"] < payload["access_size"] and payload["owner_epoch"] != 0 and
                (payload["fault_stage"] != 2 or second_stage_valid))
    va = payload["virtual_address"]
    total = va + payload["failure_offset"]
    return int(is_valid), (total & ((1 << 64) - 1)), int(total >= (1 << 64))


def make_composed_tb(rows: list[dict]) -> str:
    lines = ["module tb;"]
    for stage in ("wb", "mem", "ex"):
        lines.append(f"reg {stage}_uses_explicit_packet_lanes;")
        for lane in range(8):
            lines.extend([f"reg {stage}_lane{lane}_occupied;", f"reg {stage}_lane{lane}_has_fault;",
                          f"reg [31:0] {stage}_lane{lane}_slot_index;",
                          f"reg [63:0] {stage}_lane{lane}_fault_address;", f"reg {stage}_lane{lane}_fault_is_write;",
                          f"reg signed [31:0] {stage}_lane{lane}_owner_thread_id;",
                          f"reg signed [31:0] {stage}_lane{lane}_virtual_thread_id;",
                          f"reg [63:0] {stage}_lane{lane}_pc;", f"reg {stage}_lane{lane}_has_cpu_translation_fault;"])
            for field, width in TRANSLATION_FAULT_FIELDS.items():
                declaration = f"reg {stage}_lane{lane}_cpu_translation_fault_{field};" if width == 1 else f"reg [{width - 1}:0] {stage}_lane{lane}_cpu_translation_fault_{field};"
                lines.append(declaration)
    lines.extend(["reg incoming_fault_valid, incoming_fault_is_write;", "reg [63:0] incoming_fault_address;", "reg [2:0] incoming_active_mem_lane;",
                  "wire [63:0] winner_fault_address;", "wire winner_fault_is_write;", "wire delivery_rejected, delivery_valid, trap_flush;",
                  "wire [2:0] selected_active_mem_lane;", "wire thrown_valid, thrown_is_write;", "wire [63:0] thrown_address;",
                  "wire signed [31:0] winner_owner_thread_id, winner_virtual_thread_id;", "wire [63:0] winner_pc;", "wire winner_has_cpu_translation_fault;",
                  "wire winner_cpu_translation_fault_is_valid;", "wire [63:0] winner_cpu_translation_fault_failing_virtual_address_wrapped_sum;",
                  "wire winner_cpu_translation_fault_failing_virtual_address_overflow;",
                  "processor_pagefault_delivery_top dut(.*);", "initial begin"])
    for field, width in TRANSLATION_FAULT_FIELDS.items():
        declaration = f"wire winner_cpu_translation_fault_{field};" if width == 1 else f"wire [{width - 1}:0] winner_cpu_translation_fault_{field};"
        lines.insert(-2, declaration)
    for index, row in enumerate(rows):
        for stage in ("wb", "mem", "ex"):
            lines.append(f"  {stage}_uses_explicit_packet_lanes=1'b{row[stage]['uses_explicit_packet_lanes']};")
            for lane, state in enumerate(row[stage]["lanes"]):
                lines.append(f"  {stage}_lane{lane}_occupied=1'b{state['occupied']}; {stage}_lane{lane}_has_fault=1'b{state['has_fault']};")
                lines.append(f"  {stage}_lane{lane}_slot_index=32'h{state['slot_index'] & 0xffffffff:08x};")
                lines.append(f"  {stage}_lane{lane}_fault_address=64'd{state['fault_address']}; {stage}_lane{lane}_fault_is_write=1'b{state['fault_is_write']};")
                lines.append(f"  {stage}_lane{lane}_owner_thread_id=32'h{state['owner_thread_id'] & 0xffffffff:08x}; {stage}_lane{lane}_virtual_thread_id=32'h{state['virtual_thread_id'] & 0xffffffff:08x};")
                lines.append(f"  {stage}_lane{lane}_pc=64'h{state['pc'] & 0xffffffffffffffff:016x}; {stage}_lane{lane}_has_cpu_translation_fault=1'b{state['has_cpu_translation_fault']};")
                for field, width in TRANSLATION_FAULT_FIELDS.items():
                    value = state["translation_fault"][field] & ((1 << width) - 1)
                    literal = f"1'b{value}" if width == 1 else f"{width}'h{value:x}"
                    lines.append(f"  {stage}_lane{lane}_cpu_translation_fault_{field}={literal};")
        lines.append(f"  incoming_fault_valid=1'b{row['incoming_fault_valid']}; incoming_fault_address=64'd{row['incoming_fault_address']}; incoming_fault_is_write=1'b{row['incoming_fault_is_write']}; incoming_active_mem_lane=3'd{row['incoming_active_mem_lane']};")
        packed, width = expected_composed(row)
        lines.append(f"  #1; if ({{winner_owner_thread_id,winner_virtual_thread_id,winner_pc,winner_has_cpu_translation_fault,winner_fault_address,winner_fault_is_write,delivery_rejected,delivery_valid,selected_active_mem_lane,thrown_address,thrown_is_write,thrown_valid,trap_flush}} !== {width}'h{packed:067x}) $fatal(1, \"{index}:{row['scenario']}\");")
        payload = expected_translation_fault(row)
        payload_width = sum(TRANSLATION_FAULT_FIELDS.values())
        payload_value = 0
        for field, field_width in TRANSLATION_FAULT_FIELDS.items():
            payload_value = (payload_value << field_width) | (payload[field] & ((1 << field_width) - 1))
        concatenation = ",".join("winner_cpu_translation_fault_" + field for field in TRANSLATION_FAULT_FIELDS)
        lines.append(f"  if ({{{concatenation}}} !== {payload_width}'h{payload_value:x}) $fatal(1, \"translation-payload-{index}:{row['scenario']}\");")
        valid, failing_address, overflow = expected_translation_fault_semantics(row)
        packed_derived = (valid << 65) | (failing_address << 1) | overflow
        lines.append(f"  if ({{winner_cpu_translation_fault_is_valid,winner_cpu_translation_fault_failing_virtual_address_wrapped_sum,winner_cpu_translation_fault_failing_virtual_address_overflow}} !== 66'h{packed_derived:017x}) $fatal(1, \"translation-derived-{index}:{row['scenario']} present=%b actual=%h expected=%h\", winner_has_cpu_translation_fault, {{winner_cpu_translation_fault_is_valid,winner_cpu_translation_fault_failing_virtual_address_wrapped_sum,winner_cpu_translation_fault_failing_virtual_address_overflow}}, 66'h{packed_derived:017x});")
    lines.extend([f"  $display(\"PASS vectors={len(rows)}\"); $finish;", "end", "endmodule", ""])
    return "\n".join(lines)


def run(iverilog: str, vvp: str, rtl: pathlib.Path, tb_text: str, work: pathlib.Path) -> subprocess.CompletedProcess:
    tb = work / "tb.sv"; exe = work / "tb.vvp"; tb.write_text(tb_text, encoding="utf-8")
    env = os.environ.copy(); suite = pathlib.Path(iverilog).resolve().parent.parent
    env["PATH"] = os.pathsep.join([str(pathlib.Path(iverilog).resolve().parent), str(pathlib.Path(vvp).resolve().parent), str(suite / "lib")]) + os.pathsep + env.get("PATH", "")
    env["YOSYSHQ_ROOT"] = str(suite)
    built = subprocess.run([iverilog, "-g2012", "-s", "tb", "-o", str(exe), str(rtl), str(tb)], capture_output=True, text=True, env=env)
    if built.returncode: raise RuntimeError("iverilog compile failed: " + built.stdout + built.stderr)
    return subprocess.run([vvp, str(exe)], capture_output=True, text=True, env=env)


def main() -> None:
    parser = argparse.ArgumentParser(); parser.add_argument("--rtl", required=True); parser.add_argument("--composed-rtl"); parser.add_argument("--hwir-run", required=True)
    parser.add_argument("--active-runtime-trace"); parser.add_argument("--golden-runtime-trace")
    parser.add_argument("--active-translation-fault-trace"); parser.add_argument("--golden-translation-fault-trace"); parser.add_argument("--translation-fault-runtime-comparison")
    parser.add_argument("--active-scalar-memory-translation-fault-trace"); parser.add_argument("--golden-scalar-memory-translation-fault-trace")
    parser.add_argument("--active-second-stage-translation-fault-trace"); parser.add_argument("--golden-second-stage-translation-fault-trace"); parser.add_argument("--second-stage-translation-fault-runtime-comparison")
    parser.add_argument("--active-second-stage-scalar-memory-translation-fault-trace"); parser.add_argument("--golden-second-stage-scalar-memory-translation-fault-trace"); parser.add_argument("--second-stage-scalar-memory-translation-fault-runtime-comparison")
    parser.add_argument("--out", required=True); parser.add_argument("--iverilog", required=True); parser.add_argument("--vvp", required=True)
    args = parser.parse_args(); rtl = pathlib.Path(args.rtl).resolve(); hwir_run = pathlib.Path(args.hwir_run).resolve(); out = pathlib.Path(args.out).resolve()
    if out.exists(): raise SystemExit("output directory must be new")
    manifest = json.loads((hwir_run / "manifest.json").read_text(encoding="utf-8"))
    rows = runtime_vectors() + directed_vectors() + random_vectors()
    translation_fault_runtime_vectors = 0
    translation_fault_runtime_provenance = None
    tf_args = (args.active_translation_fault_trace, args.golden_translation_fault_trace, args.translation_fault_runtime_comparison)
    if any(tf_args):
        if not all(tf_args): raise SystemExit("all three translation-fault runtime trace/comparison paths are required")
        rows.append(production_translation_fault_vector(*tf_args)); translation_fault_runtime_vectors = 1
        active_fault_bytes = pathlib.Path(args.active_translation_fault_trace).read_bytes()
        golden_fault_bytes = pathlib.Path(args.golden_translation_fault_trace).read_bytes()
        fault_comparison_bytes = pathlib.Path(args.translation_fault_runtime_comparison).read_bytes()
        fault_comparison = json.loads(fault_comparison_bytes)
        translation_fault_runtime_provenance = {
            "scenario": fault_comparison.get("scenario"),
            "activeTraceSha256": sha(active_fault_bytes),
            "goldenTraceSha256": sha(golden_fault_bytes),
            "comparisonSha256": sha(fault_comparison_bytes),
            "activeGoldenCommonFieldsEqual": fault_comparison.get("activeGoldenCommonFieldsEqual"),
            "runtimeFrom": fault_comparison.get("runtimeFrom"),
            "trust": fault_comparison.get("trust"),
            "activeOnlyFields": fault_comparison.get("activeOnlyFields"),
            "derivedSurfaceDifference": fault_comparison.get("derivedSurfaceDifference")}
    scalar_memory_args = (args.active_scalar_memory_translation_fault_trace, args.golden_scalar_memory_translation_fault_trace)
    scalar_memory_runtime_count = 0
    scalar_memory_runtime_provenance = None
    if any(scalar_memory_args):
        if not all(scalar_memory_args): raise SystemExit("both active and Golden scalar-memory translation-fault traces are required")
        scalar_memory_rows = production_scalar_memory_translation_fault_vectors(*scalar_memory_args)
        rows.extend(scalar_memory_rows); scalar_memory_runtime_count = len(scalar_memory_rows)
        scalar_memory_runtime_provenance = {"activeTraceSha256": sha(pathlib.Path(scalar_memory_args[0]).read_bytes()),
                                            "goldenTraceSha256": sha(pathlib.Path(scalar_memory_args[1]).read_bytes()),
                                            "scenarioCount": scalar_memory_runtime_count,
                                            "commonFieldsComparedInVerifier": True,
                                            "trust": "production LoadMicroOp/StoreMicroOp caller runtime; not pipeline-cycle timing"}
    second_stage_args = (args.active_second_stage_translation_fault_trace, args.golden_second_stage_translation_fault_trace,
                         args.second_stage_translation_fault_runtime_comparison)
    second_stage_runtime_count = 0
    second_stage_runtime_provenance = None
    if any(second_stage_args):
        if not all(second_stage_args): raise SystemExit("all three second-stage active/Golden trace and differential paths are required")
        second_stage_vector = production_translation_fault_vector(*second_stage_args)
        second_stage_payload = second_stage_vector["wb"]["lanes"][0]["translation_fault"]
        if second_stage_payload["fault_stage"] != 2 or not second_stage_payload["has_guest_physical_address"]:
            raise RuntimeError("second-stage runtime vector lacks SecondStage code/GPA presence")
        rows.append(second_stage_vector); second_stage_runtime_count = 1
        second_stage_runtime_provenance = {"activeTraceSha256": sha(pathlib.Path(second_stage_args[0]).read_bytes()),
                                           "goldenTraceSha256": sha(pathlib.Path(second_stage_args[1]).read_bytes()),
                                           "comparisonSha256": sha(pathlib.Path(second_stage_args[2]).read_bytes()),
                                           "commonFieldsEqual": True,
                                           "trust": "production ExecutePipelineCycle second-stage fetch fault; not RTL equivalence"}
    second_stage_scalar_args = (args.active_second_stage_scalar_memory_translation_fault_trace,
                                args.golden_second_stage_scalar_memory_translation_fault_trace,
                                args.second_stage_scalar_memory_translation_fault_runtime_comparison)
    second_stage_scalar_runtime_count = 0
    second_stage_scalar_runtime_provenance = None
    if any(second_stage_scalar_args):
        if not all(second_stage_scalar_args): raise SystemExit("all second-stage scalar-memory active/Golden trace and differential paths are required")
        scalar_comparison = json.loads(pathlib.Path(second_stage_scalar_args[2]).read_text(encoding="utf-8"))
        if scalar_comparison.get("allCommonFieldsEqual") is not True or scalar_comparison.get("scenarioCount") != 2:
            raise RuntimeError("second-stage scalar-memory comparison does not confirm two common-field matches")
        rows.extend(production_scalar_memory_translation_fault_vectors(*second_stage_scalar_args[:2]))
        second_stage_scalar_runtime_count = 2
        second_stage_scalar_runtime_provenance = {"activeTraceSha256": sha(pathlib.Path(second_stage_scalar_args[0]).read_bytes()),
                                                  "goldenTraceSha256": sha(pathlib.Path(second_stage_scalar_args[1]).read_bytes()),
                                                  "comparisonSha256": sha(pathlib.Path(second_stage_scalar_args[2]).read_bytes()),
                                                  "commonFieldsComparedInVerifier": True,
                                                  "trust": "production second-stage LoadMicroOp/StoreMicroOp callers; not cycle timing"}
    runtime_metadata_matches = 0
    if args.active_runtime_trace or args.golden_runtime_trace:
        if not args.active_runtime_trace or not args.golden_runtime_trace:
            raise SystemExit("both --active-runtime-trace and --golden-runtime-trace are required")
        runtime_metadata_matches = attach_runtime_metadata(rows, args.active_runtime_trace, args.golden_runtime_trace)
    out.mkdir(parents=True); (out / "stage-aware-fault-winner.sv").write_bytes(rtl.read_bytes())
    vector_bytes = ("\n".join(json.dumps(row, separators=(",", ":")) for row in rows) + "\n").encode()
    (out / "vectors.jsonl").write_bytes(vector_bytes)
    with tempfile.TemporaryDirectory(dir=out) as temp:
        good = run(args.iverilog, args.vvp, rtl, make_tb(rows), pathlib.Path(temp))
        if good.returncode: raise RuntimeError("candidate mismatch: " + good.stdout + good.stderr)
    composed_receipt = None
    if args.composed_rtl:
        composed_rtl = pathlib.Path(args.composed_rtl).resolve()
        composed_text = composed_rtl.read_text(encoding="utf-8-sig")
        (out / "processor-pagefault-delivery.sv").write_text(composed_text, encoding="utf-8")
        with tempfile.TemporaryDirectory(dir=out) as temp:
            result = run(args.iverilog, args.vvp, composed_rtl, make_composed_tb(rows), pathlib.Path(temp))
            if result.returncode: raise RuntimeError("composed page-fault top mismatch: " + result.stdout + result.stderr)
        connections = list(__import__("re").finditer(r"(\.winner_fault_address\s+)\(_select_winner_payload_winner_fault_address\)", composed_text))
        if len(connections) < 2: raise RuntimeError("delivery-instance winner payload mutation anchor missing")
        connection = connections[-1]
        mutant_text = composed_text[:connection.start()] + connection.group(1) + "(incoming_fault_address)" + composed_text[connection.end():]
        composed_mutant = out / "processor-pagefault-delivery-mutant.sv"
        composed_mutant.write_text(mutant_text, encoding="utf-8")
        with tempfile.TemporaryDirectory(dir=out) as temp:
            changed = run(args.iverilog, args.vvp, composed_mutant, make_composed_tb(rows), pathlib.Path(temp))
            if changed.returncode == 0: raise RuntimeError("composed WB winner payload wiring mutant escaped detection")
        metadata_pattern = r"(\.winner_pc\s*\()\s*winner_pc(\s*\))"
        metadata_mutant_text, metadata_mutations = __import__("re").subn(metadata_pattern, r"\1wb_lane0_pc\2", composed_text, count=1)
        if metadata_mutations != 1: raise RuntimeError("winner PC metadata mutation anchor missing")
        metadata_mutant = out / "processor-pagefault-delivery-metadata-mutant.sv"
        metadata_mutant.write_text(metadata_mutant_text, encoding="utf-8")
        with tempfile.TemporaryDirectory(dir=out) as temp:
            changed_metadata = run(args.iverilog, args.vvp, metadata_mutant, make_composed_tb(rows), pathlib.Path(temp))
            if changed_metadata.returncode == 0: raise RuntimeError("winner PC payload mutation escaped detection")
        translation_mutant_text, translation_mutations = __import__("re").subn(
            r"(\?\s+)wb_lane0_cpu_translation_fault_reason\b",
            r"\1wb_lane1_cpu_translation_fault_reason", composed_text, count=1)
        if translation_mutations != 1: raise RuntimeError("translation-fault reason payload mutation anchor missing")
        translation_mutant = out / "processor-pagefault-delivery-translation-fault-mutant.sv"
        translation_mutant.write_text(translation_mutant_text, encoding="utf-8")
        with tempfile.TemporaryDirectory(dir=out) as temp:
            changed_translation = run(args.iverilog, args.vvp, translation_mutant, make_composed_tb(rows), pathlib.Path(temp))
            if changed_translation.returncode == 0: raise RuntimeError("translation-fault reason payload mutation escaped detection")
        validity_pattern = r"(assign\s+winner_cpu_translation_fault_is_valid\s*=)[\s\S]*?;"
        validity_mutant_text, validity_mutations = __import__("re").subn(validity_pattern,
            r"\1 1'b0;", composed_text, count=1)
        if validity_mutations != 1: raise RuntimeError("translation-fault IsValid mutation anchor missing")
        validity_mutant = out / "processor-pagefault-delivery-validity-mutant.sv"
        validity_mutant.write_text(validity_mutant_text, encoding="utf-8")
        with tempfile.TemporaryDirectory(dir=out) as temp:
            changed_validity = run(args.iverilog, args.vvp, validity_mutant, make_composed_tb(rows), pathlib.Path(temp))
            if changed_validity.returncode == 0: raise RuntimeError("translation-fault IsValid mutation escaped detection")
        failing_address_pattern = r"(assign\s+winner_cpu_translation_fault_failing_virtual_address_wrapped_sum\s*=)[\s\S]*?;"
        failing_address_mutant_text, failing_address_mutations = __import__("re").subn(
            failing_address_pattern, r"\1 64'h0;", composed_text, count=1)
        if failing_address_mutations != 1: raise RuntimeError("failing-virtual-address mutation anchor missing")
        failing_address_mutant = out / "processor-pagefault-delivery-failing-address-mutant.sv"
        failing_address_mutant.write_text(failing_address_mutant_text, encoding="utf-8")
        with tempfile.TemporaryDirectory(dir=out) as temp:
            changed_address = run(args.iverilog, args.vvp, failing_address_mutant, make_composed_tb(rows), pathlib.Path(temp))
            if changed_address.returncode == 0: raise RuntimeError("failing-virtual-address mutation escaped detection")
        composed_receipt = {"candidate": "PASS", "candidateRtlSha256": sha(composed_rtl.read_bytes()),
                            "composedHwirSha256": manifest["composedHwirSha256"],
                            "composedMlirSha256": manifest["composedMlirSha256"],
                            "vectorCount": len(rows), "runtimeDerivedVectors": 3 + translation_fault_runtime_vectors + scalar_memory_runtime_count + second_stage_runtime_count + second_stage_scalar_runtime_count,
                            "activeGoldenRuntimeMetadataMatches": runtime_metadata_matches,
                            "selectedScalarFieldsChecked": ["FaultAddress", "FaultIsWrite", "OwnerThreadId", "VirtualThreadId", "PC", "CpuTranslationFault.HasValue"],
                            "translationFaultFieldsChecked": list(TRANSLATION_FAULT_FIELDS),
                            "translationFaultPayloadFieldFacts": "Every flattened nullable record scalar is checked at winner output for all vectors; runtime-derived records include first-stage fetch, first-stage scalar callers, second-stage fetch, and second-stage scalar callers; other nested payload values are synthetic.",
                            "payloadFacts": "Three winner tuples use active/Golden source-instrumented helper observations; all four classes of translation-fault payload are hydrated from active/Golden runtime records.",
                            "candidateOutput": f"PASS vectors={len(rows)}", "mutant": "DETECTED",
                            "mutantFailure": "MEM delivery was wired to the incoming payload instead of the selected WriteBack winner; runtime WB vector rejected the mutant.",
                            "metadataMutant": "DETECTED", "metadataMutantFailure": "winner PC trace output was wired to WB lane 0 rather than the resolved winner; selected-PC oracle rejected the mutant.",
                            "translationFaultMutant": "DETECTED", "translationFaultMutantFailure": "winner translation-fault Reason was wired from WB lane 1 instead of lane 0; full payload oracle rejected the mutant.",
                            "derivedSemanticOutputsChecked": ["IsValid", "FailingVirtualAddress modulo-2^64", "checked overflow flag"],
                            "derivedSemanticMutants": {"validity": "DETECTED", "failingVirtualAddress": "DETECTED"}}
        (out / "composed-receipt.json").write_text(json.dumps(composed_receipt, indent=2) + "\n", encoding="utf-8")
    mutant = out / "stage-aware-fault-winner-mutant.sv"
    rtl_text = rtl.read_text(encoding="utf-8-sig")
    # Equality must preserve the earlier physical lane. Older generated RTL
    # encoded the signed comparison through a 4-bit subtract result; current
    # RTL uses a direct signed less-than. Mutate one winner-key comparison to
    # <= so the equal-slot directed vector proves the tie rule is enforced.
    anchor = "4'h9"
    if anchor in rtl_text:
        mutant_text = rtl_text.replace(anchor, "4'h0")
    else:
        pattern = r"\$signed\((_GEN_\d+)\) < \$signed\((_GEN_\d+)\)"
        mutant_text, replacements = __import__("re").subn(pattern, lambda m: m.group(0).replace(" < ", " <= "), rtl_text)
        if replacements == 0: raise RuntimeError("signed slot-order comparator mutation anchor not found")
    mutant.write_text(mutant_text, encoding="utf-8")
    with tempfile.TemporaryDirectory(dir=out) as temp:
        changed = run(args.iverilog, args.vvp, mutant, make_tb(rows), pathlib.Path(temp))
        if changed.returncode == 0: raise RuntimeError("equal-slot physical-lane tie mutant escaped detection")
    receipt = {"schema": "circuitweave.processor-stage-aware-fault-winner-rtl-check/v1",
               "hwirSha256": manifest["hwirSha256"], "mlirSha256": manifest["mlirSha256"],
               "candidateRtlSha256": sha((out / "stage-aware-fault-winner.sv").read_bytes()),
               "vectorsSha256": sha(vector_bytes), "vectorCount": len(rows), "runtimeDerivedVectors": 3 + translation_fault_runtime_vectors + scalar_memory_runtime_count + second_stage_runtime_count + second_stage_scalar_runtime_count,
               "productionTranslationFaultRuntimeVectors": translation_fault_runtime_vectors,
               "productionTranslationFaultRuntime": translation_fault_runtime_provenance,
               "productionScalarMemoryTranslationFaultRuntimeVectors": scalar_memory_runtime_count,
               "productionScalarMemoryTranslationFaultRuntime": scalar_memory_runtime_provenance,
               "productionSecondStageTranslationFaultRuntimeVectors": second_stage_runtime_count,
               "productionSecondStageTranslationFaultRuntime": second_stage_runtime_provenance,
               "productionSecondStageScalarMemoryTranslationFaultRuntimeVectors": second_stage_scalar_runtime_count,
               "productionSecondStageScalarMemoryTranslationFaultRuntime": second_stage_scalar_runtime_provenance,
               "candidate": "PASS", "candidateOutput": f"PASS vectors={len(rows)}", "mutant": "DETECTED",
               "mutantFailure": "Equal slot keys incorrectly replaced the earlier physical lane; directed tie vector rejected mutant.",
               "toolchain": {
                   "iverilog": (lambda p: (p.stdout + p.stderr).splitlines()[0])(subprocess.run([args.iverilog, "-V"], capture_output=True, text=True)),
                   "vvp": ((lambda p: (p.stdout + p.stderr).splitlines()[0] if (p.stdout + p.stderr).splitlines() else "Icarus vvp executable; paired suite binary")
                           (subprocess.run([args.vvp, "-V"], capture_output=True, text=True))),
                   "iverilogSha256": sha(pathlib.Path(args.iverilog).read_bytes()),
                   "vvpSha256": sha(pathlib.Path(args.vvp).read_bytes()),
                   "iverilogPath": str(pathlib.Path(args.iverilog).resolve()),
                   "vvpPath": str(pathlib.Path(args.vvp).resolve())},
               "scope": "Combinational stage winner arbitration from materialized lane facts. No clock/reset/stall/replay or downstream trap equivalence."}
    if composed_receipt is not None:
        receipt["composedTop"] = composed_receipt
    (out / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(receipt))


if __name__ == "__main__": main()
