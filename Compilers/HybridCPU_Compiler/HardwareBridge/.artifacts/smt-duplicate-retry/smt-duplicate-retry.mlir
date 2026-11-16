// Candidate hierarchy; no RTL equivalence or source proof.
// canonical-hwir-sha256: e91cd1eadb138d6b974c33b15f76d21954a57d81e10e32a04ec208bf3169ca68
module {
  hw.module @smt_duplicate_retry_projection(in %attempt0_class_admitted : i1, in %attempt0_lane_materialized : i1, in %attempt0_template_budget_consumed : i1, in %attempt1_class_admitted : i1, in %attempt1_lane_materialized : i1, in %attempt1_template_budget_consumed : i1, in %duplicate_entry_present : i1, in %rank3_candidate_present : i1, out attempt0_rejected : i1, out attempt1_rejected : i1, out duplicate_retry_reached : i1, out first_attempt_committed : i1, out first_attempt_reached : i1, out nomination_cleared_after_loop : i1, out port_cleared_after_loop : i1, out rejection_events : i2, out retry_attempt_committed : i1, out template_budget_consumptions : i2) {
    %attempt0_reached = comb.and bin %duplicate_entry_present, %rank3_candidate_present : i1
    %attempt0_success = comb.and bin %attempt0_class_admitted, %attempt0_lane_materialized : i1
    %one = hw.constant 1 : i1
    %attempt0_failed_raw = comb.xor bin %attempt0_success, %one : i1
    %attempt0_failed = comb.and bin %attempt0_reached, %attempt0_failed_raw : i1
    %retry_reached = comb.and bin %attempt0_reached, %duplicate_entry_present : i1
    %retry_candidate = comb.xor bin %attempt0_success, %one : i1
    %retry_reached_final = comb.and bin %retry_reached, %retry_candidate : i1
    %attempt1_success_raw = comb.and bin %attempt1_class_admitted, %attempt1_lane_materialized : i1
    %attempt1_failed_raw = comb.xor bin %attempt1_success_raw, %one : i1
    %attempt1_failed = comb.and bin %retry_reached_final, %attempt1_failed_raw : i1
    %attempt1_success = comb.and bin %retry_reached_final, %attempt1_success_raw : i1
    %any_success = comb.or bin %attempt0_success, %attempt1_success : i1
    %reject0_u2_fill = hw.constant 0 : i1
    %reject0_u2 = comb.concat %reject0_u2_fill, %attempt0_failed : i1, i1
    %reject1_u2_fill = hw.constant 0 : i1
    %reject1_u2 = comb.concat %reject1_u2_fill, %attempt1_failed : i1, i1
    %reject_total = comb.add bin %reject0_u2, %reject1_u2 : i2
    %budget0_reached = comb.and bin %attempt0_reached, %attempt0_template_budget_consumed : i1
    %budget0_u2_fill = hw.constant 0 : i1
    %budget0_u2 = comb.concat %budget0_u2_fill, %budget0_reached : i1, i1
    %budget1_reached = comb.and bin %retry_reached_final, %attempt1_template_budget_consumed : i1
    %budget1_u2_fill = hw.constant 0 : i1
    %budget1_u2 = comb.concat %budget1_u2_fill, %budget1_reached : i1, i1
    %budget_total = comb.add bin %budget0_u2, %budget1_u2 : i2
    hw.output %attempt0_failed, %attempt1_failed, %retry_reached_final, %attempt0_success, %attempt0_reached, %any_success, %any_success, %reject_total, %attempt1_success, %budget_total : i1, i1, i1, i1, i1, i1, i1, i2, i1, i2
  }
}
