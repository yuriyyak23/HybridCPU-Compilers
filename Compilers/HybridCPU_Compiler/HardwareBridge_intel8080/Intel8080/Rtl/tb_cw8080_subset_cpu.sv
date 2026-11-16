`timescale 1ns/1ps
module tb_cw8080_subset_cpu;
    logic clk = 0;
    always #5 clk = ~clk;
    logic reset;
    logic mem_req, mem_write, mem_ready;
    logic [15:0] mem_addr;
    logic [7:0] mem_wdata, mem_rdata;
    logic io_req, io_write, io_ready, irq, irq_ack;
    logic [7:0] io_port, io_wdata, io_rdata, irq_opcode;
    logic [15:0] pc;
    logic [7:0] a, b, c, d, e, h, l;
    logic sign, zero, parity, carry, aux_carry, halted, unsupported;
    logic ready_phase;
    logic prior_waiting;
    logic prior_write;
    logic [15:0] prior_addr;
    logic [7:0] prior_wdata;
    logic [7:0] mem [0:65535];
    int cycles, stall_cycles, write_transfers;

    cw8080_subset_cpu dut (
        .clk, .reset, .mem_req, .mem_write, .mem_addr, .mem_wdata, .mem_ready, .mem_rdata,
        .io_req, .io_write, .io_port, .io_wdata, .io_ready, .io_rdata,
        .irq, .irq_opcode, .irq_ack, .pc, .accumulator(a), .b, .c, .d, .e, .h, .l,
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
    always @(posedge clk) begin
        if (!reset && mem_req && mem_ready && mem_write) begin
            mem[mem_addr] <= mem_wdata;
            write_transfers <= write_transfers + 1;
        end
    end
    always @(posedge clk) begin
        if (reset) prior_waiting <= 1'b0;
        else begin
            if (prior_waiting && (!mem_req || mem_addr !== prior_addr || mem_write !== prior_write || mem_wdata !== prior_wdata)) begin
                $error("FAIL memory request/payload changed before ready");
                $fatal(1);
            end
            if (mem_write && !mem_req) begin
                $error("FAIL write asserted without memory request");
                $fatal(1);
            end
            prior_waiting <= mem_req && !mem_ready;
            if (mem_req && !mem_ready) begin
                prior_addr <= mem_addr;
                prior_write <= mem_write;
                prior_wdata <= mem_wdata;
            end
        end
    end
    always @(posedge clk)
        if (!reset && mem_req && !mem_ready) stall_cycles = stall_cycles + 1;

    task automatic check(input bit condition, input string label);
        if (!condition) begin $error("FAIL %s", label); $fatal(1); end
    endtask

    initial begin
        reset = 1; irq = 0; irq_opcode = 0; cycles = 0; stall_cycles = 0;
        for (int i = 0; i < 65536; i++) mem[i] = 8'h00;
        // ADD M at HL=0000 (reads its own opcode); MVI A,FF; ADI 01;
        // ADC A consumes ADI's CY; ADD A ignores its CY result; mapped MOVs;
        // write A to (HL), DCR/INR/DCR M, then read back; finally exercise
        // INR/DCR wrap flags and carry preservation on B and C.
        mem[16'h0000]=8'h86;
        mem[16'h0001]=8'h3e; mem[16'h0002]=8'hff;
        mem[16'h0003]=8'hc6; mem[16'h0004]=8'h01;
        mem[16'h0005]=8'h8f; mem[16'h0006]=8'h87;
        mem[16'h0007]=8'h47; // MOV B,A
        mem[16'h0008]=8'h48; // MOV C,B
        mem[16'h0009]=8'h77; // MOV M,A
        mem[16'h000a]=8'h3e; mem[16'h000b]=8'hff;
        mem[16'h000c]=8'h35; // DCR M: 02 -> 01
        mem[16'h000d]=8'h34; // INR M: 01 -> 02 (not mapped in source _ops)
        mem[16'h000e]=8'h35; // DCR M: 02 -> 01
        mem[16'h000f]=8'h7e; // MOV A,M
        mem[16'h0010]=8'h3e; mem[16'h0011]=8'hff;
        mem[16'h0012]=8'h47; // MOV B,A
        mem[16'h0013]=8'h04; // INR B: FF -> 00
        mem[16'h0014]=8'h05; // DCR B: 00 -> FF
        mem[16'h0015]=8'h4f; // MOV C,A (FF)
        mem[16'h0016]=8'h0c; // INR C: FF -> 00 (not mapped in source _ops)
        mem[16'h0017]=8'h0d; // DCR C: 00 -> FF; hardware must preserve CY
        mem[16'h0018]=8'h16; mem[16'h0019]=8'h01; // MVI D,01 (manual RTL-only opcode)
        mem[16'h001a]=8'h90; // SUB B: 01 - FF = 02, borrow
        mem[16'h001b]=8'h99; // SBB C with borrow in: 02 - FF - 1 = 02
        mem[16'h001c]=8'ha7; // ANA A: clears CY and AC per Intel manual
        mem[16'h001d]=8'haa; // XRA D (D=01), result 03
        mem[16'h001e]=8'hb0; // ORA B (B=FF)
        mem[16'h001f]=8'hbe; // CMP M (M=01), leaves A unchanged
        mem[16'h0020]=8'h36; mem[16'h0021]=8'ha5; // MVI M,A5 through acknowledged write
        mem[16'h0022]=8'hfb; // EI
        mem[16'h0023]=8'h76; // HLT: EI delay must mature despite halt
        repeat (2) @(posedge clk);
        write_transfers = 0;
        #1 reset = 0;

        while (!(a == 8'h86 && pc == 16'h0001) && cycles < 100) begin
            @(posedge clk); #1; cycles++;
        end
        check(a == 8'h86 && pc == 16'h0001, "ADD M reads the HL address, not the next PC byte");

        while (!(pc == 16'h0005 && a == 8'h00 && carry && zero && parity) && cycles < 150) begin
            @(posedge clk); #1; cycles++;
        end
        check(pc == 16'h0005 && a == 8'h00 && carry && zero && parity,
              "ADI writes carry, zero, and even parity while preserving AC");
        while (!(pc == 16'h0006 && a == 8'h01 && !carry) && cycles < 175) begin
            @(posedge clk); #1; cycles++;
        end
        check(pc == 16'h0006 && a == 8'h01 && !carry,
              "ADC A consumes carry produced by ADI");

        while (!(pc == 16'h000f && mem[16'h0000] == 8'h01 && write_transfers == 4) && cycles < 220) begin @(posedge clk); #1; cycles++; end
        check(pc == 16'h000f && mem[16'h0000] == 8'h01 && write_transfers == 4 && !carry && !aux_carry,
              "DCR/INR/DCR M complete acknowledged read-modify-write cycles and preserve CY");
        while (!(pc == 16'h0010 && a == 8'h01) && cycles < 240) begin @(posedge clk); #1; cycles++; end
        check(pc == 16'h0010 && a == 8'h01,
              "MOV A,M reads the final acknowledged memory update");
        check(b == 8'h02 && c == 8'h02 && mem[16'h0000] == 8'h01,
              "mapped register MOVs and MOV M,A update expected destinations");
        check(write_transfers == 4, "one MOV and three (HL) read-modify-write instructions acknowledge writes");

        while (!(pc == 16'h0014 && b == 8'h00 && zero && !sign && parity && aux_carry && !carry) && cycles < 270) begin @(posedge clk); #1; cycles++; end
        check(pc == 16'h0014 && b == 8'h00 && zero && !sign && parity && aux_carry && !carry,
              "INR B wraps FF to 00, sets Z/P/AC, and preserves CY");
        while (!(pc == 16'h0015 && b == 8'hff && !zero && sign && parity && aux_carry && !carry) && cycles < 290) begin @(posedge clk); #1; cycles++; end
        check(pc == 16'h0015 && b == 8'hff && !zero && sign && parity && aux_carry && !carry,
              "DCR B wraps 00 to FF, sets S/P/AC, and preserves CY");
        while (!(pc == 16'h0017 && c == 8'h00 && zero && !sign && parity && aux_carry && !carry) && cycles < 310) begin @(posedge clk); #1; cycles++; end
        check(pc == 16'h0017 && c == 8'h00 && zero && !sign && parity && aux_carry && !carry,
              "manual INR C extension wraps FF to 00 with architectural flags");
        while (!(pc == 16'h0018 && c == 8'hff && !zero && sign && parity && aux_carry && !carry) && cycles < 330) begin @(posedge clk); #1; cycles++; end
        check(pc == 16'h0018 && c == 8'hff && !zero && sign && parity && aux_carry && !carry,
              "DCR C preserves CY despite the emulator's source helper side effect");

        while (!(pc == 16'h001b && d == 8'h01 && a == 8'h02 && carry && aux_carry && !sign && !zero && !parity) && cycles < 350) begin @(posedge clk); #1; cycles++; end
        check(pc == 16'h001b && d == 8'h01 && a == 8'h02 && carry && aux_carry && !sign && !zero && !parity, "MVI D writes its immediate, then SUB B sets borrow and auxiliary borrow");
        while (!(pc == 16'h001c && dut.opcode == 8'h99 && dut.state == 4'd0 && a == 8'h02 && carry && aux_carry) && cycles < 360) begin @(posedge clk); #1; cycles++; end
        check(pc == 16'h001c && dut.opcode == 8'h99 && dut.state == 4'd0 && a == 8'h02 && carry && aux_carry, "SBB C consumes CY as borrow");
        while (!(pc == 16'h001d && a == 8'h02 && !carry && !aux_carry) && cycles < 370) begin @(posedge clk); #1; cycles++; end
        check(pc == 16'h001d && a == 8'h02 && !carry && !aux_carry, "ANA A clears CY and AC");
        while (!(pc == 16'h001e && a == 8'h03) && cycles < 380) begin @(posedge clk); #1; cycles++; end
        check(pc == 16'h001e && a == 8'h03 && !carry && !aux_carry, "XRA D computes XOR and logical flags");
        while (!(pc == 16'h001f && a == 8'hff && sign && !zero && parity && !carry && !aux_carry) && cycles < 390) begin @(posedge clk); #1; cycles++; end
        check(pc == 16'h001f && a == 8'hff && sign && !zero && parity && !carry && !aux_carry, "ORA B computes OR and logical flags");
        while (!(pc == 16'h0020 && a == 8'hff && sign && !zero && !parity && !carry && !aux_carry) && cycles < 400) begin @(posedge clk); #1; cycles++; end
        check(pc == 16'h0020 && a == 8'hff && sign && !zero && !parity && !carry && !aux_carry, "CMP M sets subtraction flags without changing A");
        while (!(pc == 16'h0022 && mem[16'h0000] == 8'ha5 && write_transfers == 5) && cycles < 420) begin @(posedge clk); #1; cycles++; end
        check(pc == 16'h0022 && mem[16'h0000] == 8'ha5 && write_transfers == 5, "MVI M holds its immediate through an acknowledged memory write");
        while (!halted && cycles < 440) begin @(posedge clk); #1; cycles++; end
        check(halted, "program reaches HLT under alternating ready stalls");
        check(pc == 16'h0023, "HLT retains its opcode address");
        check(!unsupported, "all executed opcodes are in the supported subset");
        check(!io_req, "subset performs no I/O transfers");
        check(stall_cycles > 0, "memory request/ready stalls were exercised");
        check(dut.interrupts_enabled, "EI takes effect after the following HLT instruction is fetched");
        check(!mem_req, "HALT state stops issuing memory requests");
        repeat (3) begin
            @(posedge clk); #1;
            check(halted && !mem_req && pc == 16'h0023 && a == 8'hff,
                  "HALT holds architectural state and bus idle without an interrupt");
        end

        irq = 1'b1;
        irq_opcode = 8'hf3; // Inject DI; it wakes the halted CPU and disables further IRQs.
        while (!irq_ack && cycles < 250) begin @(posedge clk); #1; cycles++; end
        check(irq_ack && !halted && pc == 16'h0023,
              "enabled IRQ wakes HALT, acknowledges once, and does not consume a memory byte");
        irq = 1'b0;
        @(posedge clk); #1;
        check(!irq_ack && !dut.interrupts_enabled,
              "injected DI executes and IRQ acknowledge is one cycle");
        while (!halted && cycles < 500) begin @(posedge clk); #1; cycles++; end
        check(halted && pc == 16'h0023 && !mem_req && !unsupported,
              "CPU returns to bus-idle HALT after executing the injected opcode");
        $display("PASS integrated partial CPU program in %0d cycles with %0d memory stalls", cycles, stall_cycles);
        $finish;
    end
endmodule
