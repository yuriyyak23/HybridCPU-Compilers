// Candidate hierarchy; no RTL equivalence or source proof.
// canonical-hwir-sha256: 4c5a5e8dbd571225fd948ff4e8ca284d30aa9d3560ed067909f563e82d1834fd
module {
  hw.module @csr_clear_instruction_decode(in %canonical_opcode : i16, in %instruction_valid : i1, out csr_clear_decoded : i1) {
    %csr_clear_opcode_const = hw.constant 149 : i16
    %opcode_matches_csr_clear = comb.icmp bin eq %canonical_opcode, %csr_clear_opcode_const : i16
    %decoded_valid_csr_clear = comb.and bin %instruction_valid, %opcode_matches_csr_clear : i1
    hw.output %decoded_valid_csr_clear : i1
  }
  hw.module @csr_clear_microop_retire_effect(in %microop_present : i1, out clears_architectural_exception_state : i1, out effect_present : i1) {
    hw.output %microop_present, %microop_present : i1, i1
  }
  hw.module @processor_csr_clear_retire_event_projection(in %canonical_opcode : i16, in %current_div_by_zero_count : i32, in %current_inexact_count : i32, in %current_invalid_op_count : i32, in %current_overflow_count : i32, in %current_underflow_count : i32, in %instruction_valid : i1, in %retire_batch_apply_event : i1, out next_div_by_zero_count : i32, out next_inexact_count : i32, out next_invalid_op_count : i32, out next_overflow_count : i32, out next_underflow_count : i32) {
    %csr_clear_decode_result_csr_clear_decoded = hw.instance "csr_clear_decode" @csr_clear_instruction_decode(canonical_opcode: %canonical_opcode: i16, instruction_valid: %instruction_valid: i1) -> (csr_clear_decoded: i1)
    %csr_clear_effect_result_clears_architectural_exception_state, %csr_clear_effect_result_effect_present = hw.instance "csr_clear_effect" @csr_clear_microop_retire_effect(microop_present: %csr_clear_decode_result_csr_clear_decoded: i1) -> (clears_architectural_exception_state: i1, effect_present: i1)
    %effect_applied_in_batch = comb.and bin %csr_clear_effect_result_effect_present, %retire_batch_apply_event : i1
    %retired_counter_transition_result_next_div_by_zero_count, %retired_counter_transition_result_next_inexact_count, %retired_counter_transition_result_next_invalid_op_count, %retired_counter_transition_result_next_overflow_count, %retired_counter_transition_result_next_underflow_count = hw.instance "retired_counter_transition" @retired_csr_exception_counter_transition(clears_architectural_exception_state: %csr_clear_effect_result_clears_architectural_exception_state: i1, current_div_by_zero_count: %current_div_by_zero_count: i32, current_inexact_count: %current_inexact_count: i32, current_invalid_op_count: %current_invalid_op_count: i32, current_overflow_count: %current_overflow_count: i32, current_underflow_count: %current_underflow_count: i32, effect_present: %effect_applied_in_batch: i1) -> (next_div_by_zero_count: i32, next_inexact_count: i32, next_invalid_op_count: i32, next_overflow_count: i32, next_underflow_count: i32)
    hw.output %retired_counter_transition_result_next_div_by_zero_count, %retired_counter_transition_result_next_inexact_count, %retired_counter_transition_result_next_invalid_op_count, %retired_counter_transition_result_next_overflow_count, %retired_counter_transition_result_next_underflow_count : i32, i32, i32, i32, i32
  }
  hw.module @retired_csr_exception_counter_transition(in %clears_architectural_exception_state : i1, in %current_div_by_zero_count : i32, in %current_inexact_count : i32, in %current_invalid_op_count : i32, in %current_overflow_count : i32, in %current_underflow_count : i32, in %effect_present : i1, out next_div_by_zero_count : i32, out next_inexact_count : i32, out next_invalid_op_count : i32, out next_overflow_count : i32, out next_underflow_count : i32) {
    %effect_clear_applied = comb.and bin %effect_present, %clears_architectural_exception_state : i1
    %clear_action_result_zero_div_by_zero_count, %clear_action_result_zero_inexact_count, %clear_action_result_zero_invalid_op_count, %clear_action_result_zero_overflow_count, %clear_action_result_zero_underflow_count = hw.instance "clear_action" @vector_exception_counter_clear_action() -> (zero_div_by_zero_count: i32, zero_inexact_count: i32, zero_invalid_op_count: i32, zero_overflow_count: i32, zero_underflow_count: i32)
    %next_div_by_zero_count_mux = comb.mux bin %effect_clear_applied, %clear_action_result_zero_div_by_zero_count, %current_div_by_zero_count : i32
    %next_inexact_count_mux = comb.mux bin %effect_clear_applied, %clear_action_result_zero_inexact_count, %current_inexact_count : i32
    %next_invalid_op_count_mux = comb.mux bin %effect_clear_applied, %clear_action_result_zero_invalid_op_count, %current_invalid_op_count : i32
    %next_overflow_count_mux = comb.mux bin %effect_clear_applied, %clear_action_result_zero_overflow_count, %current_overflow_count : i32
    %next_underflow_count_mux = comb.mux bin %effect_clear_applied, %clear_action_result_zero_underflow_count, %current_underflow_count : i32
    hw.output %next_div_by_zero_count_mux, %next_inexact_count_mux, %next_invalid_op_count_mux, %next_overflow_count_mux, %next_underflow_count_mux : i32, i32, i32, i32, i32
  }
  hw.module @vector_exception_counter_clear_action(out zero_div_by_zero_count : i32, out zero_inexact_count : i32, out zero_invalid_op_count : i32, out zero_overflow_count : i32, out zero_underflow_count : i32) {
    %zero_div_by_zero_count_const = hw.constant 0 : i32
    %zero_inexact_count_const = hw.constant 0 : i32
    %zero_invalid_op_count_const = hw.constant 0 : i32
    %zero_overflow_count_const = hw.constant 0 : i32
    %zero_underflow_count_const = hw.constant 0 : i32
    hw.output %zero_div_by_zero_count_const, %zero_inexact_count_const, %zero_invalid_op_count_const, %zero_overflow_count_const, %zero_underflow_count_const : i32, i32, i32, i32, i32
  }
  hw.module @vector_pod_csr_read_projection(in %csr_addr : i12, in %state_mask : i8, in %state_priority : i16, out read_address_valid : i1, out read_data : i64) {
    %k0 = hw.constant 2304 : i12
    %n1 = comb.icmp bin eq %csr_addr, %k0 : i12
    %k2 = hw.constant 2305 : i12
    %n3 = comb.icmp bin eq %csr_addr, %k2 : i12
    %n4 = comb.or bin %n1, %n3 : i1
    %n5_fill = hw.constant 0 : i56
    %n5 = comb.concat %n5_fill, %state_mask : i56, i8
    %k6 = hw.constant 32767 : i16
    %n7 = comb.and bin %state_priority, %k6 : i16
    %n8_fill = hw.constant 0 : i48
    %n8 = comb.concat %n8_fill, %n7 : i48, i16
    %n9 = comb.mux bin %n1, %n5, %n8 : i64
    %k10 = hw.constant 0 : i64
    %n11 = comb.mux bin %n4, %n9, %k10 : i64
    hw.output %n4, %n11 : i1, i64
  }
  hw.module @vector_pod_csr_write_projection(in %csr_addr : i12, in %state_mask : i8, in %state_priority : i16, in %write_enable : i1, in %write_value : i64, out next_mask : i8, out next_priority : i16, out write_address_valid : i1) {
    %k0 = hw.constant 2304 : i12
    %n1 = comb.icmp bin eq %csr_addr, %k0 : i12
    %n4 = comb.and bin %write_enable, %n1 : i1
    %n7 = comb.extract %write_value from 0 : (i64) -> i8
    %k8 = hw.constant 31 : i8
    %n9 = comb.and bin %n7, %k8 : i8
    %n10 = comb.mux bin %n4, %n9, %state_mask : i8
    %k2 = hw.constant 2305 : i12
    %n3 = comb.icmp bin eq %csr_addr, %k2 : i12
    %n5 = comb.and bin %write_enable, %n3 : i1
    %k14 = hw.constant 32768 : i16
    %n15 = comb.and bin %state_priority, %k14 : i16
    %k11 = hw.constant 32767 : i64
    %n12 = comb.and bin %write_value, %k11 : i64
    %n13 = comb.extract %n12 from 0 : (i64) -> i16
    %n16 = comb.or bin %n15, %n13 : i16
    %n17 = comb.mux bin %n5, %n16, %state_priority : i16
    %n6 = comb.or bin %n1, %n3 : i1
    hw.output %n10, %n17, %n6 : i8, i16, i1
  }
}
