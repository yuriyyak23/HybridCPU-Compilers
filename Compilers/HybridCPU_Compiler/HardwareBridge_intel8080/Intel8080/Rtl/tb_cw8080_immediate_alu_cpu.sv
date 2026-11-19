`timescale 1ns/1ps
module tb_cw8080_immediate_alu_cpu;
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
      mem[0]=8'h3e; mem[1]=8'h0f;       // MVI A,0F
      mem[2]=8'hc6; mem[3]=8'h01;       // ADI 01 => 10
      mem[4]=8'hce; mem[5]=8'h00;       // ACI 00
      mem[6]=8'hd6; mem[7]=8'h01;       // SUI 01 => 0F
      mem[8]=8'hde; mem[9]=8'h00;       // SBI 00
      mem[10]=8'he6; mem[11]=8'hf0;     // ANI F0 => 00
      mem[12]=8'hee; mem[13]=8'h0f;     // XRI 0F
      mem[14]=8'hf6; mem[15]=8'h80;     // ORI 80 => 8F
      mem[16]=8'hfe; mem[17]=8'h90;     // CPI 90 => FF flags, A preserved
      mem[18]=8'h76;
      repeat(2) @(posedge clk); #1 reset=0;
      while(!(pc==16'h000c&&a==8'h00&&!carry&&aux_carry)&&cycles<150) begin @(posedge clk); #1; cycles++; end
      check(pc==16'h000c&&a==8'h00&&!carry&&aux_carry,
            "ANI sets 8080 AC from OR of input bit 3 when result is zero");
      while(!halted&&cycles<300) begin @(posedge clk); #1; cycles++; end
      check(halted&&pc==19,"all immediate ALU instructions consume one operand and HLT advances PC");
      check(a==8'h8f,"ADI/ACI/SUI/SBI/ANI/XRI/ORI update A; CPI preserves A");
      check(sign&&!zero&&parity&&carry&&aux_carry,"CPI computes sign/parity/borrow and auxiliary carry-out");
      check(!unsupported,"all eight immediate ALU opcodes decode");
      $display("PASS immediate ALU subset in %0d cycles",cycles); $finish;
    end
endmodule
