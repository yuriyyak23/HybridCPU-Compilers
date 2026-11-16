`timescale 1ns/1ps
module tb_cw8080_io_cpu;
    logic clk=0; always #5 clk=~clk;
    logic reset,mem_req,mem_write,mem_ready,io_req,io_write,io_ready,irq,irq_ack,ready_phase,halted,unsupported;
    logic [15:0] mem_addr,pc,sp;
    logic [7:0] mem_wdata,mem_rdata,io_port,io_wdata,io_rdata,irq_opcode,a,b,c,d,e,h,l;
    logic sign,zero,parity,carry,aux_carry,io_phase,prior_waiting,prior_write;
    logic [7:0] prior_port,prior_data,mem[0:65535];
    int cycles,outs,ins,stalls;
    cw8080_subset_cpu dut(.clk,.reset,.mem_req,.mem_write,.mem_addr,.mem_wdata,.mem_ready,.mem_rdata,
      .io_req,.io_write,.io_port,.io_wdata,.io_ready,.io_rdata,.irq,.irq_opcode,.irq_ack,
      .pc,.stack_pointer(sp),.accumulator(a),.b,.c,.d,.e,.h,.l,.sign,.zero,.parity,.carry,.aux_carry,.halted,.unsupported);
    always_comb begin
      mem_ready=mem_req&&ready_phase; mem_rdata=mem[mem_addr];
      io_ready=io_req&&io_phase; io_rdata=8'ha7;
    end
    always_ff @(posedge clk) if(reset) begin ready_phase<=0; io_phase<=0; end else begin ready_phase<=~ready_phase; io_phase<=~io_phase; end
    always @(posedge clk) if(!reset) begin
      if(io_req&&io_ready) begin if(io_write) outs++; else ins++; end
      if(prior_waiting&&(!io_req||io_write!==prior_write||io_port!==prior_port||io_wdata!==prior_data)) begin $error("I/O payload changed before ready"); $fatal(1); end
      prior_waiting<=io_req&&!io_ready;
      if(io_req&&!io_ready) begin prior_write<=io_write; prior_port<=io_port; prior_data<=io_wdata; stalls++; end
    end else prior_waiting<=0;
    task automatic check(input bit condition,input string label); if(!condition) begin $error("FAIL %s",label); $fatal(1); end endtask
    initial begin
      reset=1; irq=0; irq_opcode=0; cycles=0; outs=0; ins=0; stalls=0;
      for(int i=0;i<65536;i++) mem[i]=0;
      mem[0]=8'h3e; mem[1]=8'h56;          // MVI A,56
      mem[2]=8'hd3; mem[3]=8'h12;          // OUT 12
      mem[4]=8'hdb; mem[5]=8'h34;          // IN 34
      mem[6]=8'h76;                       // HLT
      repeat(2) @(posedge clk); #1 reset=0;
      while(!halted&&cycles<200) begin @(posedge clk); #1; cycles++; end
      check(halted&&pc==6,"IN/OUT immediate ports advance PC and reach HLT");
      check(outs==1&&ins==1,"one acknowledged output and input transaction");
      check(a==8'ha7,"IN samples the ready-cycle port data");
      check(stalls>0,"I/O request waits for ready");
      check(!unsupported,"IN and OUT are decoded");
      $display("PASS I/O CPU subset in %0d cycles with %0d stalls",cycles,stalls); $finish;
    end
    always @(posedge clk) if(!reset&&io_req&&io_ready&&io_write) begin
      if(io_port!==8'h12||io_wdata!==8'h56) begin $error("OUT payload mismatch"); $fatal(1); end
    end
    always @(posedge clk) if(!reset&&io_req&&io_ready&&!io_write) begin
      if(io_port!==8'h34) begin $error("IN port mismatch"); $fatal(1); end
    end
endmodule
