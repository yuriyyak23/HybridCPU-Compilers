hw.module @trap_event_projection(
  in %trap_microop_valid : i1,
  in %execute_attempt : i1,
  out event_valid : i1,
  out cause_code : i3,
  out fault_address : i64,
  out has_fault_address : i1
) {
  %valid = comb.and %trap_microop_valid, %execute_attempt : i1
  %cause_value = hw.constant 2 : i3
  %cause_zero = hw.constant 0 : i3
  %cause = comb.mux %valid, %cause_value, %cause_zero : i3
  %address = hw.constant 0 : i64
  %has_address = hw.constant 0 : i1
  hw.output %valid, %cause, %address, %has_address : i1, i3, i64, i1
}
