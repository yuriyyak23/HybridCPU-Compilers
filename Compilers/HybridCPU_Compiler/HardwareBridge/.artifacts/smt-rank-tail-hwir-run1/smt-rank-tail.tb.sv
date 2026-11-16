module smt_rank_tail_tb;
  reg [31:0] credit0,credit1,credit2,credit3,owner_vt,unwritten_tail;
  reg [63:0] cycle;
  reg fairness_enabled;
  reg vt0_present,vt0_ready,vt1_present,vt1_ready,vt2_present,vt2_ready,vt3_present,vt3_ready;
  wire assist_rank3_index_oob_if_reached,main_duplicate_candidate_attempt,rank3_in_range;
  wire [2:0] collected_count;
  wire valid;
  integer owner,tailIndex,packedCredits,mask,i,j,tmp,ranked[0:2];
  integer cases, expectedDup, expectedOob, c0,c1,c2,c3;
  reg [31:0] tail;
  smt_ranked_collected_admission_projection dut(
    .credit0(credit0),.credit1(credit1),.credit2(credit2),.credit3(credit3),.cycle(cycle),
    .fairness_enabled(fairness_enabled),.owner_vt(owner_vt),.unwritten_tail(unwritten_tail),
    .vt0_present(vt0_present),.vt0_ready(vt0_ready),.vt1_present(vt1_present),.vt1_ready(vt1_ready),
    .vt2_present(vt2_present),.vt2_ready(vt2_ready),.vt3_present(vt3_present),.vt3_ready(vt3_ready),
    .assist_rank3_index_oob_if_reached(assist_rank3_index_oob_if_reached),
    .main_duplicate_candidate_attempt(main_duplicate_candidate_attempt),.rank3_in_range(rank3_in_range),
    .collected_count(collected_count),.valid(valid));
  initial begin
    cases=0; cycle=1; fairness_enabled=1;
    for(owner=0;owner<4;owner=owner+1)
    for(tailIndex=0;tailIndex<8;tailIndex=tailIndex+1)
    for(packedCredits=0;packedCredits<256;packedCredits=packedCredits+1)
    for(mask=0;mask<16;mask=mask+1) begin
      tail = tailIndex<5 ? tailIndex : (tailIndex==5 ? 32'hffffffff : (tailIndex==6 ? 32'h80000000 : 32'hfffffffe));
      owner_vt=owner; unwritten_tail=tail;
      credit0=(packedCredits>>0)&3; credit1=(packedCredits>>2)&3; credit2=(packedCredits>>4)&3; credit3=(packedCredits>>6)&3;
      vt0_ready=mask[0];vt1_ready=mask[1];vt2_ready=mask[2];vt3_ready=mask[3];
      vt0_present=mask[0];vt1_present=mask[1];vt2_present=mask[2];vt3_present=mask[3];
      #1;
      j=0; for(i=0;i<4;i=i+1) if(i!=owner) begin ranked[j]=i;j=j+1;end
      for(i=0;i<2;i=i+1) for(j=i+1;j<3;j=j+1) begin
        if(((packedCredits>>(ranked[j]*2))&3)>((packedCredits>>(ranked[i]*2))&3) ||
           ((((packedCredits>>(ranked[j]*2))&3)==((packedCredits>>(ranked[i]*2))&3)) && ranked[j]<ranked[i])) begin tmp=ranked[i];ranked[i]=ranked[j];ranked[j]=tmp;end
      end
      expectedDup=0; expectedOob=0;
      if(tail<4 && tail!=owner) begin
        for(i=0;i<3;i=i+1) if(tail==ranked[i]) expectedDup=1;
        expectedDup=expectedDup & mask[tail];
      end
      if(tail>=4 && tail!=owner) expectedOob=1;
      if(main_duplicate_candidate_attempt!==expectedDup[0] || assist_rank3_index_oob_if_reached!==expectedOob[0] || rank3_in_range!==(tail<4)) begin
        $display("FAIL owner=%0d tail=%08x packedCredits=%0d mask=%x got dup/oob/range=%b%b%b expected=%0d%0d%0d",owner,tail,packedCredits,mask,main_duplicate_candidate_attempt,assist_rank3_index_oob_if_reached,rank3_in_range,expectedDup,expectedOob,(tail<4));$fatal(1);
      end
      cases=cases+1;
    end
    $display("PASS: %0d SMT rank-tail RTL vectors",cases);$finish;
  end
endmodule
