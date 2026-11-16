`timescale 1ns/1ps
module tb_cw8080_daa_cpu;
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
    task automatic run_case(input int which,input logic [7:0] expected_a,input bit expected_cy,input bit expected_ac,input bit expected_z,input bit expected_p);
      begin
        reset=1; irq=0; irq_opcode=0; cycles=0; for(int i=0;i<65536;i++) mem[i]=0;
        case(which)
          0: begin mem[0]=8'h3e; mem[1]=8'h9b; mem[2]=8'h27; mem[3]=8'h76; end
          1: begin mem[0]=8'h3e; mem[1]=8'h0f; mem[2]=8'hc6; mem[3]=8'h01; mem[4]=8'h27; mem[5]=8'h76; end
          2: begin mem[0]=8'h37; mem[1]=8'h3e; mem[2]=8'h00; mem[3]=8'h27; mem[4]=8'h76; end
          3: begin mem[0]=8'h3e; mem[1]=8'h9a; mem[2]=8'h27; mem[3]=8'h76; end
        endcase
        repeat(2) @(posedge clk); #1 reset=0;
        while(!halted&&cycles<100) begin @(posedge clk); #1; cycles++; end
        check(halted,"DAA vector reaches HLT"); check(a==expected_a,"DAA adjusted accumulator");
        check(carry==expected_cy&&aux_carry==expected_ac,"DAA carry outputs");
        check(zero==expected_z&&parity==expected_p,"DAA zero/parity flags");
        check(!unsupported,"DAA opcode is supported");
      end
    endtask
    initial begin
      reset=1; irq=0; irq_opcode=0;
      run_case(0,8'h01,1,1,0,0); // low and high correction, carry out
      run_case(1,8'h16,0,0,0,0); // incoming AC corrects low nibble, then clears
      run_case(2,8'h60,1,0,0,1); // incoming CY applies high correction and is retained
      run_case(3,8'h00,1,1,1,1); // both corrections produce zero with CY/AC
      $display("PASS DAA directed cases"); $finish;
    end
endmodule
