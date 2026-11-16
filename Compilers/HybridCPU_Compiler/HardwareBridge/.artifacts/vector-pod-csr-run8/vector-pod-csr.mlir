// Candidate hierarchy; no RTL equivalence or source proof.
// canonical-hwir-sha256: 9a00779fd9ad0d37e74a9fdcfe6f81a4dc0bf87859205fcec496520a31403924
module {
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
