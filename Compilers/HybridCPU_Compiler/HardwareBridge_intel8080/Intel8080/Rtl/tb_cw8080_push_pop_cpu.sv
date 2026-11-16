`timescale 1ns/1ps
module tb_cw8080_push_pop_cpu;
    logic clk=0; always #5 clk=~clk;
    logic reset,mem_req,mem_write,mem_ready,io_req,io_write,io_ready,irq,irq_ack,ready_phase,halted,unsupported;
    logic [15:0] mem_addr,pc,sp;
    logic [7:0] mem_wdata,mem_rdata,io_port,io_wdata,io_rdata,irq_opcode,a,b,c,d,e,h,l;
    logic sign,zero,parity,carry,aux_carry;
    logic [7:0] mem[0:65535];
    int cycles,writes,reads;
    cw8080_subset_cpu dut(.clk,.reset,.mem_req,.mem_write,.mem_addr,.mem_wdata,.mem_ready,.mem_rdata,
      .io_req,.io_write,.io_port,.io_wdata,.io_ready,.io_rdata,.irq,.irq_opcode,.irq_ack,
      .pc,.stack_pointer(sp),.accumulator(a),.b,.c,.d,.e,.h,.l,.sign,.zero,.parity,.carry,.aux_carry,.halted,.unsupported);
    always_comb begin mem_ready=mem_req&&ready_phase; mem_rdata=mem[mem_addr]; io_ready=0; io_rdata=0; end
    always_ff @(posedge clk) if(reset) ready_phase<=0; else ready_phase<=~ready_phase;
    always @(posedge clk) if(!reset&&mem_req&&mem_ready) begin
      if(mem_write) begin mem[mem_addr]<=mem_wdata; writes<=writes+1; end
      else if(dut.state==4'd13||dut.state==4'd14) reads<=reads+1;
    end
    task automatic check(input bit condition,input string label); if(!condition) begin $error("FAIL %s",label); $fatal(1); end endtask
    initial begin
      reset=1; irq=0; irq_opcode=0; cycles=0; writes=0; reads=0;
      for(int i=0;i<65536;i++) mem[i]=0;
      mem[0]=8'h31; mem[1]=8'h00; mem[2]=8'h10;       // LXI SP,1000
      mem[3]=8'h01; mem[4]=8'h34; mem[5]=8'h12;       // LXI B,1234
      mem[6]=8'hc5;                                   // PUSH B
      mem[7]=8'h01; mem[8]=0; mem[9]=0;               // LXI B,0000
      mem[10]=8'hc1;                                  // POP B
      mem[11]=8'h3e; mem[12]=8'hf0;                   // MVI A,F0
      mem[13]=8'hc6; mem[14]=8'h20;                   // ADI 20 => A=10, CY=1
      mem[15]=8'hf5;                                  // PUSH PSW
      mem[16]=8'haf;                                  // XRA A => A=0, flags changed
      mem[17]=8'hf1;                                  // POP PSW
      mem[18]=8'h76;                                  // HLT
      repeat(2) @(posedge clk); #1 reset=0;
      while(!halted&&cycles<400) begin @(posedge clk); #1; cycles++; end
      check(halted&&pc==18,"PUSH/POP PSW and register pairs reach HLT");
      check(sp==16'h1000,"PUSH/POP restores SP");
      check({b,c}==16'h1234,"PUSH/POP BC preserves pair order");
      check(a==8'h5b,"POP PSW restores accumulator");
      check(!sign&&!zero&&!parity&&carry&&!aux_carry,"POP PSW restores 8080 flag bit layout from ADI result");
      check(writes==4&&reads==4,"BC and PSW stack transfers each use two acknowledged bytes");
      check(mem[16'h0ffe]==8'h03&&mem[16'h0fff]==8'h10,"PUSH PSW stores flags then accumulator in little-endian stack order");
      check(!unsupported,"all tested opcodes are supported");
      $display("PASS PUSH/POP CPU subset in %0d cycles",cycles); $finish;
    end
endmodule
