`timescale 1ns/1ps
module tb_cw8080_pair_cpu;
    logic clk = 0;
    always #5 clk = ~clk;
    logic reset;
    logic mem_req, mem_write, mem_ready;
    logic [15:0] mem_addr, pc, stack_pointer;
    logic [7:0] mem_wdata, mem_rdata;
    logic io_req, io_write, io_ready, irq, irq_ack;
    logic [7:0] io_port, io_wdata, io_rdata, irq_opcode;
    logic [7:0] a,b,c,d,e,h,l;
    logic sign,zero,parity,carry,aux_carry,halted,unsupported;
    logic ready_phase;
    logic [7:0] mem [0:65535];
    int cycles, stalls;

    cw8080_subset_cpu dut (
        .clk, .reset, .mem_req, .mem_write, .mem_addr, .mem_wdata, .mem_ready, .mem_rdata,
        .io_req, .io_write, .io_port, .io_wdata, .io_ready, .io_rdata,
        .irq, .irq_opcode, .irq_ack, .pc, .stack_pointer, .accumulator(a), .b, .c, .d, .e, .h, .l,
        .sign, .zero, .parity, .carry, .aux_carry, .halted, .unsupported
    );
    always_comb begin
        mem_ready = mem_req && ready_phase;
        mem_rdata = mem[mem_addr];
        io_ready = 1'b0;
        io_rdata = 8'h00;
    end
    always_ff @(posedge clk) begin
        if (reset) ready_phase <= 1'b0;
        else ready_phase <= ~ready_phase;
    end
    always @(posedge clk) if (!reset && mem_req && !mem_ready) stalls++;
    task automatic check(input bit condition, input string label);
        if (!condition) begin $error("FAIL %s",label); $fatal(1); end
    endtask
    initial begin
        reset=1; irq=0; irq_opcode=0; cycles=0; stalls=0;
        for (int i=0;i<65536;i++) mem[i]=8'h00;
        mem[16'h0000]=8'h01; mem[16'h0001]=8'hff; mem[16'h0002]=8'hff; // LXI B,FFFF
        mem[16'h0003]=8'h03; // INX B -> 0000, flags unchanged
        mem[16'h0004]=8'h0b; // DCX B -> FFFF, flags unchanged
        mem[16'h0005]=8'h11; mem[16'h0006]=8'h01; mem[16'h0007]=8'h00; // LXI D,0001
        mem[16'h0008]=8'h19; // DAD D -> HL=0001, CY=0
        mem[16'h0009]=8'h21; mem[16'h000a]=8'h02; mem[16'h000b]=8'h00; // LXI H,0002
        mem[16'h000c]=8'h09; // DAD B -> HL=0001, CY=1
        mem[16'h000d]=8'h31; mem[16'h000e]=8'h34; mem[16'h000f]=8'h12; // LXI SP,1234
        mem[16'h0010]=8'h33; // manual INX SP -> 1235
        mem[16'h0011]=8'h3b; // manual DCX SP -> 1234
        mem[16'h0012]=8'h76; // HLT
        repeat(2) @(posedge clk);
        #1 reset=0;
        while(!halted && cycles<300) begin @(posedge clk); #1; cycles++; end
        check(halted && pc==16'h0013,"pair program reaches HLT with PC advanced");
        check({b,c}==16'hffff,"INX/DCX wrap BC and preserve both bytes");
        check({d,e}==16'h0001,"LXI D captures low then high byte");
        check({h,l}==16'h0001 && carry,"DAD updates HL and carry from 17-bit sum");
        check(stack_pointer==16'h1234,"LXI/INX/DCX operate on SP");
        check(!sign && !zero && !parity && !aux_carry,"LXI/INX/DCX/DAD preserve non-carry flags");
        check(!unsupported && stalls>0,"all pair opcodes execute with memory stalls");
        $display("PASS pair CPU subset in %0d cycles with %0d fetch stalls",cycles,stalls);
        $finish;
    end
endmodule
