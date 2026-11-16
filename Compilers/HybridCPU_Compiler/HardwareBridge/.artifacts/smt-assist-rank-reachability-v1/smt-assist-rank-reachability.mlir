// Candidate hierarchy; no RTL equivalence or source proof.
// canonical-hwir-sha256: dc4cd9b4d8ead654f59b3122aa7caa6c18d0c0533c69fd8554266699cf80a864
module {
  hw.module @smt_assist_rank_reachability_projection(in %assist_enabled : i1, in %boundary_blocked : i1, in %owner_vt : i32, in %prior_rank0_committed : i1, in %prior_rank1_committed : i1, in %prior_rank2_committed : i1, in %rank3_vt : i32, in %slot_available : i1, out rank3_index_oob_exception_condition : i1, out rank3_iteration_reached : i1) {
    %entry_assist_slot = comb.and bin %assist_enabled, %slot_available : i1
    %one = hw.constant 1 : i1
    %not_boundary_blocked = comb.xor bin %boundary_blocked, %one : i1
    %entry_open = comb.and bin %entry_assist_slot, %not_boundary_blocked : i1
    %prior_commit01 = comb.or bin %prior_rank0_committed, %prior_rank1_committed : i1
    %prior_commit_any = comb.or bin %prior_commit01, %prior_rank2_committed : i1
    %no_prior_commit = comb.xor bin %prior_commit_any, %one : i1
    %rank3_reached = comb.and bin %entry_open, %no_prior_commit : i1
    %rank3_u64_fill = hw.constant 0 : i32
    %rank3_u64 = comb.concat %rank3_u64_fill, %rank3_vt : i32, i32
    %four32 = hw.constant 4 : i32
    %four64_u64_fill = hw.constant 0 : i32
    %four64_u64 = comb.concat %four64_u64_fill, %four32 : i32, i32
    %rank3_in_range = comb.icmp bin ult %rank3_u64, %four64_u64 : i64
    %not_rank3_in_range = comb.xor bin %rank3_in_range, %one : i1
    %rank3_is_owner = comb.icmp bin eq %rank3_vt, %owner_vt : i32
    %not_rank3_owner = comb.xor bin %rank3_is_owner, %one : i1
    %oob_without_owner_guard = comb.and bin %not_rank3_in_range, %not_rank3_owner : i1
    %oob_if_reached = comb.and bin %rank3_reached, %oob_without_owner_guard : i1
    hw.output %oob_if_reached, %rank3_reached : i1, i1
  }
}
