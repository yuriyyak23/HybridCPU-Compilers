`timescale 1ns/1ps
module tb_cw8080_memory_address_cpu;
    logic clk=0; always #5 clk=~clk;
    logic reset,mem_req,mem_write,mem_ready,io_req,io_write,io_ready,irq,irq_ack,ready_phase,halted,unsupported;
    logic [15:0] mem_addr,pc,sp,prior_addr;
    logic [7:0] mem_wdata,mem_rdata,io_port,io_wdata,io_rdata,irq_opcode,a,b,c,d,e,h,l,prior_data;
    logic sign,zero,parity,carry,aux_carry,prior_waiting,prior_write;
    logic [7:0] mem[0:65535]; int cycles,writes,stalls;
    cw8080_subset_cpu dut(.clk,.reset,.mem_req,.mem_write,.mem_addr,.mem_wdata,.mem_ready,.mem_rdata,
      .io_req,.io_write,.io_port,.io_wdata,.io_ready,.io_rdata,.irq,.irq_opcode,.irq_ack,
      .pc,.stack_pointer(sp),.accumulator(a),.b,.c,.d,.e,.h,.l,.sign,.zero,.parity,.carry,.aux_carry,.halted,.unsupported);
    always_comb begin mem_ready=mem_req&&ready_phase; mem_rdata=mem[mem_addr]; io_ready=0; io_rdata=0; end
    always_ff @(posedge clk) if(reset) ready_phase<=0; else ready_phase<=~ready_phase;
    always @(posedge clk) begin
      if(!reset&&mem_req&&mem_ready&&mem_write) begin mem[mem_addr]<=mem_wdata; writes++; end
      if(reset) prior_waiting<=0;
      else begin
        if(prior_waiting&&(!mem_req||mem_addr!==prior_addr||mem_write!==prior_write||mem_wdata!==prior_data)) begin $error("memory request changed while stalled"); $fatal(1); end
        prior_waiting<=mem_req&&!mem_ready;
        if(mem_req&&!mem_ready) begin prior_addr<=mem_addr; prior_write<=mem_write; prior_data<=mem_wdata; stalls++; end
      end
    end
    task automatic check(input bit condition,input string label); if(!condition) begin $error("FAIL %s",label); $fatal(1); end endtask
    initial begin
      reset=1; irq=0; irq_opcode=0; cycles=0; writes=0; stalls=0; for(int i=0;i<65536;i++) mem[i]=0;
      mem[16'h3000]=8'h78; mem[16'h3001]=8'h56;
      mem[0]=8'h01; mem[1]=8'h00; mem[2]=8'h20;       // LXI B,2000
      mem[3]=8'h11; mem[4]=8'h01; mem[5]=8'h20;       // LXI D,2001
      mem[6]=8'h3e; mem[7]=8'h55;                     // MVI A,55
      mem[8]=8'h02;                                   // STAX B
      mem[9]=8'h3e; mem[10]=8'haa;                    // MVI A,AA
      mem[11]=8'h0a;                                  // LDAX B =>55
      mem[12]=8'h12;                                  // STAX D
      mem[13]=8'h3e; mem[14]=8'h00;                   // MVI A,00
      mem[15]=8'h1a;                                  // LDAX D =>55
      mem[16]=8'h32; mem[17]=8'h02; mem[18]=8'h20;    // STA 2002
      mem[19]=8'h3e; mem[20]=8'h00;                   // MVI A,00
      mem[21]=8'h3a; mem[22]=8'h02; mem[23]=8'h20;    // LDA 2002 =>55
      mem[24]=8'h21; mem[25]=8'h34; mem[26]=8'h12;    // LXI H,1234
      mem[27]=8'h22; mem[28]=8'h03; mem[29]=8'h20;    // SHLD 2003
      mem[30]=8'h21; mem[31]=8'h00; mem[32]=8'h00;    // LXI H,0000
      mem[33]=8'h2a; mem[34]=8'h03; mem[35]=8'h20;    // LHLD 2003
      mem[36]=8'h31; mem[37]=8'hff; mem[38]=8'h2f;    // LXI SP,2FFF
      mem[39]=8'h21; mem[40]=8'h00; mem[41]=8'h30;    // LXI H,3000
      mem[42]=8'hf9;                                  // SPHL
      mem[43]=8'he3;                                  // XTHL
      mem[44]=8'heb;                                  // XCHG
      mem[45]=8'h21; mem[46]=8'h40; mem[47]=8'h00;    // LXI H,0040
      mem[48]=8'he9;                                  // PCHL
      mem[16'h0040]=8'h76;                            // HLT via PCHL
      repeat(2) @(posedge clk); #1 reset=0;
      while(!halted&&cycles<350) begin @(posedge clk); #1; cycles++; end
      check(halted&&pc==16'h0040,"direct/indirect transfers, XTHL, XCHG and PCHL reach HLT");
      check(a==8'h55,"LDAX/LDA load accumulator from selected addresses");
      check(mem[16'h2000]==8'h55&&mem[16'h2001]==8'h55&&mem[16'h2002]==8'h55,"STAX/STA store A at BC, DE and immediate address");
      check(mem[16'h2003]==8'h34&&mem[16'h2004]==8'h12,"SHLD stores HL to consecutive addresses");
      check({h,l}==16'h0040,"PCHL target is loaded into HL and used as the final PC");
      check(sp==16'h3000&&{d,e}==16'h5678,"XTHL exchanges stack words without changing SP and XCHG swaps DE/HL");
      check(mem[16'h3000]==8'h34&&mem[16'h3001]==8'h12,"XTHL writes prior HL to stack memory");
      check(writes==7&&stalls>0,"single and paired writes complete only on ready");
      check(!unsupported,"addressed memory, XTHL, XCHG, PCHL and SPHL decode");
      $display("PASS addressed memory subset in %0d cycles with %0d stalls",cycles,stalls); $finish;
    end
endmodule
