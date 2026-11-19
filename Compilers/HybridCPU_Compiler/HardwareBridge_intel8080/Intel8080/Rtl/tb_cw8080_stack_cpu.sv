`timescale 1ns/1ps
module tb_cw8080_stack_cpu;
    logic clk=0; always #5 clk=~clk;
    logic reset,mem_req,mem_write,mem_ready,io_req,io_write,io_ready,irq,irq_ack;
    logic [15:0] mem_addr,pc,stack_pointer,prior_addr;
    logic [7:0] mem_wdata,mem_rdata,io_port,io_wdata,io_rdata,irq_opcode,prior_wdata;
    logic [7:0] a,b,c,d,e,h,l;
    logic sign,zero,parity,carry,aux_carry,halted,unsupported,ready_phase,prior_waiting,prior_write;
    logic [7:0] mem[0:65535];
    int cycles,stalls,writes,reads;
    cw8080_subset_cpu dut(
        .clk,.reset,.mem_req,.mem_write,.mem_addr,.mem_wdata,.mem_ready,.mem_rdata,
        .io_req,.io_write,.io_port,.io_wdata,.io_ready,.io_rdata,.irq,.irq_opcode,.irq_ack,
        .pc,.stack_pointer,.accumulator(a),.b,.c,.d,.e,.h,.l,.sign,.zero,.parity,.carry,.aux_carry,.halted,.unsupported
    );
    always_comb begin mem_ready=mem_req&&ready_phase; mem_rdata=mem[mem_addr]; io_ready=0; io_rdata=0; end
    always_ff @(posedge clk) if(reset) ready_phase<=0; else ready_phase<=~ready_phase;
    always @(posedge clk) begin
        if(!reset&&mem_req&&mem_ready) begin
            if(mem_write) begin mem[mem_addr]<=mem_wdata; writes<=writes+1; end
            else if(dut.state==4'd13||dut.state==4'd14) reads<=reads+1;
        end
        if(reset) prior_waiting<=0;
        else begin
            if(prior_waiting&&(!mem_req||mem_addr!==prior_addr||mem_write!==prior_write||mem_wdata!==prior_wdata)) begin $error("stack bus changed before ready"); $fatal(1); end
            prior_waiting<=mem_req&&!mem_ready;
            if(mem_req&&!mem_ready) begin prior_addr<=mem_addr; prior_write<=mem_write; prior_wdata<=mem_wdata; end
            if(mem_req&&!mem_ready) stalls++;
        end
    end
    task automatic check(input bit condition,input string label); if(!condition) begin $error("FAIL %s",label); $fatal(1); end endtask
    initial begin
        reset=1; irq=0; irq_opcode=0; cycles=0; stalls=0; writes=0; reads=0;
        for(int i=0;i<65536;i++) mem[i]=0;
        mem[16'h0000]=8'h31; mem[16'h0001]=8'h00; mem[16'h0002]=8'h10; // LXI SP,1000
        mem[16'h0003]=8'hcd; mem[16'h0004]=8'h10; mem[16'h0005]=8'h00; // CALL 0010
        mem[16'h0006]=8'hcc; mem[16'h0007]=8'h30; mem[16'h0008]=8'h00; // CZ 0030, not taken
        mem[16'h0009]=8'hc3; mem[16'h000a]=8'h20; mem[16'h000b]=8'h00; // JMP 0020
        mem[16'h0010]=8'hcd; mem[16'h0011]=8'h18; mem[16'h0012]=8'h00; // nested CALL 0018
        mem[16'h0013]=8'hc0; // RNZ, taken because reset Z=0
        mem[16'h0018]=8'h0e; mem[16'h0019]=8'h33; // MVI C,33
        mem[16'h001a]=8'hc9; // RET to 0013
        mem[16'h0020]=8'hef; // RST 5 -> 0028
        mem[16'h0021]=8'h76; // HLT after RST returns
        mem[16'h0028]=8'hc9; // RET from restart vector
        mem[16'h0030]=8'h76; // must be skipped by untaken CZ
        repeat(2) @(posedge clk); #1 reset=0;
        while(!halted&&cycles<500) begin @(posedge clk); #1; cycles++; end
        check(halted&&pc==16'h0022,"nested CALL/RET, conditional CALL skip, JMP and RST return reach HLT with PC advanced");
        check(stack_pointer==16'h1000,"all taken calls, returns and RST balance SP");
        check(c==8'h33,"nested callee effects survive both returns");
        check(writes==6&&reads==6,"three pushes and three pops use two acknowledged bytes each");
        check(mem[16'h0ffc]==8'h13&&mem[16'h0ffd]==8'h00,"nested call stores little-endian return address");
        check(mem[16'h0ffe]==8'h21&&mem[16'h0fff]==8'h00,"RST pushes the post-opcode return address");
        check(!zero&&!carry&&!sign&&!parity&&!aux_carry,"control-flow instructions preserve flags");
        check(!unsupported&&stalls>0,"stack bus payload remains stable through memory stalls");
        $display("PASS stack CPU subset in %0d cycles with %0d stalls",cycles,stalls); $finish;
    end
endmodule
