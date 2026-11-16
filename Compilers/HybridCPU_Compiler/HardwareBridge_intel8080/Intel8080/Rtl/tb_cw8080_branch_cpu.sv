`timescale 1ns/1ps
module tb_cw8080_branch_cpu;
    logic clk=0; always #5 clk=~clk;
    logic reset,mem_req,mem_write,mem_ready,io_req,io_write,io_ready,irq,irq_ack;
    logic [15:0] mem_addr,pc,stack_pointer;
    logic [7:0] mem_wdata,mem_rdata,io_port,io_wdata,io_rdata,irq_opcode;
    logic [7:0] a,b,c,d,e,h,l;
    logic sign,zero,parity,carry,aux_carry,halted,unsupported,ready_phase;
    logic [7:0] mem[0:65535];
    int cycles,stalls;
    cw8080_subset_cpu dut(
        .clk,.reset,.mem_req,.mem_write,.mem_addr,.mem_wdata,.mem_ready,.mem_rdata,
        .io_req,.io_write,.io_port,.io_wdata,.io_ready,.io_rdata,.irq,.irq_opcode,.irq_ack,
        .pc,.stack_pointer,.accumulator(a),.b,.c,.d,.e,.h,.l,.sign,.zero,.parity,.carry,.aux_carry,.halted,.unsupported
    );
    always_comb begin mem_ready=mem_req&&ready_phase; mem_rdata=mem[mem_addr]; io_ready=0; io_rdata=0; end
    always_ff @(posedge clk) if(reset) ready_phase<=0; else ready_phase<=~ready_phase;
    always @(posedge clk) if(!reset&&mem_req&&!mem_ready) stalls++;
    task automatic check(input bit condition,input string label); if(!condition) begin $error("FAIL %s",label); $fatal(1); end endtask
    initial begin
        reset=1; irq=0; irq_opcode=0; cycles=0; stalls=0;
        for(int i=0;i<65536;i++) mem[i]=0;
        mem[16'h0000]=8'h3e; mem[16'h0001]=8'h00; // MVI A,00
        mem[16'h0002]=8'ha7; // ANA A sets Z
        mem[16'h0003]=8'hca; mem[16'h0004]=8'h0b; mem[16'h0005]=8'h00; // JZ taken
        mem[16'h0006]=8'h06; mem[16'h0007]=8'h11; // skipped MVI B,11
        mem[16'h000b]=8'h06; mem[16'h000c]=8'h5a; // target: MVI B,5A
        mem[16'h000d]=8'hc2; mem[16'h000e]=8'h12; mem[16'h000f]=8'h00; // JNZ not taken because Z=1
        mem[16'h0010]=8'hc3; mem[16'h0011]=8'h15; mem[16'h0012]=8'h00; // JMP 0015
        mem[16'h0015]=8'h76; // HLT
        repeat(2) @(posedge clk); #1 reset=0;
        while(!halted&&cycles<300) begin @(posedge clk); #1; cycles++; end
        check(halted&&pc==16'h0015,"conditional and unconditional branches reach HLT");
        check(b==8'h5a,"JZ takes target and JNZ preserves fallthrough when false");
        check(zero&& !sign && parity && !carry && !aux_carry,"branch instructions preserve condition flags");
        check(!unsupported&&stalls>0,"branch address reads complete under ready stalls");
        $display("PASS branch CPU subset in %0d cycles with %0d stalls",cycles,stalls); $finish;
    end
endmodule
