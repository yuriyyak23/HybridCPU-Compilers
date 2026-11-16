// Candidate hierarchy; no RTL equivalence or source proof.
// canonical-hwir-sha256: d7cec726a9ff746e52e75fdd414112c7a53cbf11fa4389be9c398b9e48d1c299
module {
  hw.module @vector_pod_csr_projection(in %csr_addr : i12, in %state_mask : i8, in %state_priority : i16, in %write_enable : i1, in %write_value : i64, out next_mask : i8, out next_priority : i16, out read_data : i64, out read_valid : i1) {
    %k0 = hw.constant 2304 : i12
    %n1 = comb.icmp bin eq %csr_addr, %k0 : i12
    %n5 = comb.and bin %write_enable, %n1 : i1
    %n7 = comb.extract %write_value from 0 : (i64) -> i8
    %k8 = hw.constant 31 : i8
    %n9 = comb.and bin %n7, %k8 : i8
    %n10 = comb.mux bin %n5, %n9, %state_mask : i8
    %k2 = hw.constant 2305 : i12
    %n3 = comb.icmp bin eq %csr_addr, %k2 : i12
    %n6 = comb.and bin %write_enable, %n3 : i1
    %k14 = hw.constant 32768 : i16
    %n15 = comb.and bin %state_priority, %k14 : i16
    %k11 = hw.constant 32767 : i64
    %n12 = comb.and bin %write_value, %k11 : i64
    %n13 = comb.extract %n12 from 0 : (i64) -> i16
    %n16 = comb.or bin %n15, %n13 : i16
    %n17 = comb.mux bin %n6, %n16, %state_priority : i16
    %n4 = comb.or bin %n1, %n3 : i1
    %n18_fill = hw.constant 0 : i56
    %n18 = comb.concat %n18_fill, %state_mask : i56, i8
    %k19 = hw.constant 32767 : i16
    %n20 = comb.and bin %state_priority, %k19 : i16
    %n21_fill = hw.constant 0 : i48
    %n21 = comb.concat %n21_fill, %n20 : i48, i16
    %n22 = comb.mux bin %n1, %n18, %n21 : i64
    %k23 = hw.constant 0 : i64
    %n24 = comb.mux bin %n4, %n22, %k23 : i64
    hw.output %n10, %n17, %n24, %n4 : i8, i16, i64, i1
  }
}
