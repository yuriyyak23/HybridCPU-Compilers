`timescale 1ns/1ps
module tb_cw8080_rotate_cpu;
    logic clk=0; always #5 clk=~clk;
    logic reset,mem_req,mem_write,mem_ready,io_req,io_write,io_ready,irq,irq_ack,ready_phase,halted,unsupported;
    logic [15:0] mem_addr,pc,sp;
    logic [7:0] mem_wdata,mem_rdata,io_port,io_wdata,io_rdata,irq_opcode,a,b,c,d,e,h,l;
    logic sign,zero,parity,carry,aux_carry;
    logic [7:0] mem[0:65535]; int cycles;
    cw8080_subset_cpu dut(.clk,.reset,.mem_req,.mem_write,.mem_addr,.mem_wdata,.mem_ready,.mem_rdata,
      .io_req,.io_write,.io_port,.io_wdata,.io_ready,.io_rdata,.irq,.irq_opcode,.irq_ack,
      .pc,.stack_pointer(sp),.accumulator(a),.b,.c,.d,.e,.h,.l,.sign,.zero,.parity,.carry,.aux_carry,.halted,.unsupported);
    always_comb begin mem_ready=mem_req&&ready_phase; mem_rdata=mem[mem_addr]; io_ready=0; io_rdata=0; end
    always_ff @(posedge clk) if(reset) ready_phase<=0; else ready_phase<=~ready_phase;
    task automatic check(input bit condition,input string label); if(!condition) begin $error("FAIL %s",label); $fatal(1); end endtask
    initial begin
      reset=1; irq=0; irq_opcode=0; cycles=0; for(int i=0;i<65536;i++) mem[i]=0;
      mem[0]=8'h3e; mem[1]=8'h81; mem[2]=8'h07; mem[3]=8'h1f; mem[4]=8'h2f;
      mem[5]=8'h3f; mem[6]=8'h17; mem[7]=8'h0f; mem[8]=8'h37; mem[9]=8'h3f; mem[10]=8'h76;
      repeat(2) @(posedge clk); #1 reset=0;
      while(!halted&&cycles<160) begin @(posedge clk); #1; cycles++; end
      check(halted&&pc==10,"rotate and carry-control program reaches HLT");
      check(a==8'h7e&&carry==0,"RLC/RAR/CMA/CMC/RAL/RRC/STC/CMC results");
      check(!sign&&!zero&&!parity&&!aux_carry,"rotate and accumulator control instructions preserve SZPA flags");
      check(!unsupported,"all tested accumulator operations decode");
      $display("PASS rotate/control CPU subset in %0d cycles",cycles); $finish;
    end
endmodule
