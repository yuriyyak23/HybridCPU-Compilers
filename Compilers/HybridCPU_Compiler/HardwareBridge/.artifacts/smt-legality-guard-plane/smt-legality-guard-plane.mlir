// Candidate hierarchy; no RTL equivalence or source proof.
// canonical-hwir-sha256: 11d8a0f1ef0299300a7de191cb75b045db525e1ba6490e519d73b659aaf2754f
module {
  hw.module @smt_legality_guard_plane_projection(in %boundary_blocked : i1, in %candidate_domain : i64, in %candidate_owner_context : i32, in %kernel_domain_isolation : i1, in %owner_context : i32, in %owner_context_known : i1, in %owner_domain_certificate : i64, in %phase_key_valid : i1, in %phase_template_matches : i1, in %selected_certificate_allowed : i1, out boundary_reject : i1, out certificate_checked : i1, out domain_reject : i1, out legality_allowed : i1, out owner_context_reject : i1, out replay_certificate_selected : i1, out structural_certificate_selected : i1) {
    %one = hw.constant 1 : i1
    %not_boundary = comb.xor bin %boundary_blocked, %one : i1
    %owner_context_equal = comb.icmp bin eq %owner_context, %candidate_owner_context : i32
    %owner_context_not_equal = comb.xor bin %owner_context_equal, %one : i1
    %owner_context_mismatch = comb.and bin %owner_context_known, %owner_context_not_equal : i1
    %owner_reject = comb.and bin %not_boundary, %owner_context_mismatch : i1
    %guard_reject_any = comb.or bin %boundary_blocked, %owner_reject : i1
    %owner_reject_after_boundary = comb.mux bin %not_boundary, %owner_context_mismatch, %one : i1
    %no_boundary_or_owner_reject = comb.xor bin %owner_reject_after_boundary, %one : i1
    %zero64 = hw.constant 0 : i64
    %domain_is_kernel = comb.icmp bin eq %candidate_domain, %zero64 : i64
    %pod_domain_is_user = comb.icmp bin eq %owner_domain_certificate, %zero64 : i64
    %pod_domain_nonzero = comb.xor bin %pod_domain_is_user, %one : i1
    %kernel_domain_rejected = comb.and bin %domain_is_kernel, %kernel_domain_isolation : i1
    %domain_rejected_for_kernel_candidate = comb.and bin %pod_domain_nonzero, %kernel_domain_rejected : i1
    %domain_intersection = comb.and bin %candidate_domain, %owner_domain_certificate : i64
    %domain_intersection_is_zero = comb.icmp bin eq %domain_intersection, %zero64 : i64
    %domain_rejected_for_tagged_candidate = comb.and bin %pod_domain_nonzero, %domain_intersection_is_zero : i1
    %domain_reject_value = comb.mux bin %domain_is_kernel, %domain_rejected_for_kernel_candidate, %domain_rejected_for_tagged_candidate : i1
    %domain_reject_after_prior_guards = comb.and bin %no_boundary_or_owner_reject, %domain_reject_value : i1
    %all_guard_rejects = comb.or bin %guard_reject_any, %domain_reject_after_prior_guards : i1
    %guard_clear = comb.xor bin %all_guard_rejects, %one : i1
    %certificate_checked_value = comb.and bin %guard_clear, %one : i1
    %certificate_accept = comb.and bin %certificate_checked_value, %selected_certificate_allowed : i1
    %replay_key_match = comb.and bin %phase_key_valid, %phase_template_matches : i1
    %replay_selected_value = comb.and bin %certificate_checked_value, %replay_key_match : i1
    %not_replay_key_match = comb.xor bin %replay_key_match, %one : i1
    %structural_selected_value = comb.and bin %certificate_checked_value, %not_replay_key_match : i1
    hw.output %boundary_blocked, %certificate_checked_value, %domain_reject_after_prior_guards, %certificate_accept, %owner_reject, %replay_selected_value, %structural_selected_value : i1, i1, i1, i1, i1, i1, i1
  }
}
