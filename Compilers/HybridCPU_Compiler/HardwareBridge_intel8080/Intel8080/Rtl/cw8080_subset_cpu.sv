// Partial 8080 implementation. Supported: NOP aliases, INR/DCR r/M, MOV 40-7F except HLT, ALU 80-BF and immediate ALU, MVI r/M, direct and BC/DE indirect loads/stores, XTHL/XCHG/PCHL, LXI, INX/DCX, DAD, JMP/Jcc, CALL/CC/RET/RC, PUSH/POP, RST, IN/OUT, rotates, CMA/DAA/STC/CMC, HLT.
// The full dispatch table is intentionally not implied by this subset.
module cw8080_subset_cpu (
    input  logic        clk,
    input  logic        reset,
    output logic        mem_req,
    output logic        mem_write,
    output logic [15:0] mem_addr,
    output logic [7:0]  mem_wdata,
    input  logic        mem_ready,
    input  logic [7:0]  mem_rdata,
    output logic        io_req,
    output logic        io_write,
    output logic [7:0]  io_port,
    output logic [7:0]  io_wdata,
    input  logic        io_ready,
    input  logic [7:0]  io_rdata,
    input  logic        irq,
    input  logic [7:0]  irq_opcode,
    output logic        irq_ack,
    output logic [15:0] pc,
    output logic [15:0] stack_pointer,
    output logic [7:0]  accumulator,
    output logic [7:0]  b, c, d, e, h, l,
    output logic        sign, zero, parity, carry, aux_carry,
    output logic        halted,
    output logic        unsupported
);
    typedef enum logic [3:0] {FETCH, FETCH_WAIT, EXECUTE, IMM_FETCH, IMM_WAIT, IMM_HIGH_FETCH, IMM_HIGH_WAIT, MEM_OPERAND, MEM_WAIT, HALTED, MEM_WRITE, STACK_WRITE_HIGH, STACK_WRITE_LOW, STACK_READ_LOW, STACK_READ_HIGH, IO_WAIT} state_t;
    state_t state;
    logic [7:0] opcode;
    logic [7:0] operand;
    logic [7:0] source_value;
    logic [7:0] incdec_source;
    logic [7:0] incdec_result;
    logic is_inr;
    logic is_dcr;
    logic is_mvi;
    logic is_lxi;
    logic is_inx;
    logic is_dcx;
    logic is_dad;
    logic is_jmp;
    logic is_jcc;
    logic is_call;
    logic is_cond_call;
    logic is_ret;
    logic is_cond_ret;
    logic is_rst;
    logic is_push;
    logic is_pop;
    logic is_rotate;
    logic is_imm_alu;
    logic is_nop;
    logic is_direct_mem;
    logic is_direct_word_mem;
    logic is_indirect_mem;
    logic memory_transfer_phase;
    logic immediate_operand_valid;
    logic [15:0] stack_target_pc;
    logic [15:0] stack_return_pc;
    logic [7:0] stack_low_byte;
    logic [7:0] stack_high_byte;
    logic branch_taken;
    logic [15:0] pair_value;
    logic [15:0] pair_result;
    logic [16:0] pair_sum;
    logic [8:0] alu_ext;
    logic [4:0] alu_nibble_ext;
    logic [4:0] daa_low_sum;
    logic [4:0] daa_high_sum;
    logic [7:0] daa_stage1;
    logic daa_low_adjust;
    logic daa_high_adjust;
    logic [7:0] alu_result;
    logic interrupts_enabled;
    logic ei_pending;
    logic service_irq;
    logic memory_operand_valid;

    always_comb begin
        source_value = 8'h00;
        unique case (opcode[2:0])
            3'b000: source_value = b;
            3'b001: source_value = c;
            3'b010: source_value = d;
            3'b011: source_value = e;
            3'b100: source_value = h;
            3'b101: source_value = l;
            3'b110: source_value = operand; // M operand captured by the memory handshake state.
            3'b111: source_value = accumulator;
        endcase
        is_inr = ((opcode & 8'hc7) == 8'h04);
        is_dcr = ((opcode & 8'hc7) == 8'h05);
        is_mvi = ((opcode & 8'hc7) == 8'h06);
        is_lxi = ((opcode & 8'hcf) == 8'h01);
        is_inx = ((opcode & 8'hcf) == 8'h03);
        is_dcx = ((opcode & 8'hcf) == 8'h0b);
        is_dad = ((opcode & 8'hcf) == 8'h09);
        is_jmp = (opcode == 8'hc3 || opcode == 8'hcb);
        is_jcc = ((opcode & 8'hc7) == 8'hc2);
        is_cond_call = ((opcode & 8'hc7) == 8'hc4);
        is_call = (opcode == 8'hcd) || is_cond_call;
        is_cond_ret = ((opcode & 8'hc7) == 8'hc0);
        is_ret = (opcode == 8'hc9) || is_cond_ret;
        is_rst = ((opcode & 8'hc7) == 8'hc7);
        is_push = ((opcode & 8'hcf) == 8'hc5);
        is_pop = ((opcode & 8'hcf) == 8'hc1);
        is_rotate = (opcode == 8'h07 || opcode == 8'h0f || opcode == 8'h17 || opcode == 8'h1f);
        is_imm_alu = (opcode == 8'hc6 || opcode == 8'hce || opcode == 8'hd6 || opcode == 8'hde || opcode == 8'he6 || opcode == 8'hee || opcode == 8'hf6 || opcode == 8'hfe);
        is_nop = (opcode[7:6] == 2'b00 && opcode[2:0] == 3'b000);
        is_direct_mem = (opcode == 8'h32 || opcode == 8'h3a || opcode == 8'h22 || opcode == 8'h2a);
        is_direct_word_mem = (opcode == 8'h22 || opcode == 8'h2a);
        is_indirect_mem = (opcode == 8'h02 || opcode == 8'h0a || opcode == 8'h12 || opcode == 8'h1a);
        branch_taken = 1'b0;
        unique case (opcode[5:3])
            3'b000: branch_taken = !zero;
            3'b001: branch_taken = zero;
            3'b010: branch_taken = !carry;
            3'b011: branch_taken = carry;
            3'b100: branch_taken = !parity;
            3'b101: branch_taken = parity;
            3'b110: branch_taken = !sign;
            3'b111: branch_taken = sign;
        endcase
        pair_value = 16'h0000;
        unique case (opcode[5:4])
            2'b00: pair_value = {b,c};
            2'b01: pair_value = {d,e};
            2'b10: pair_value = {h,l};
            2'b11: pair_value = stack_pointer;
        endcase
        pair_result = is_inx ? pair_value + 16'd1 : pair_value - 16'd1;
        pair_sum = {1'b0,h,l} + {1'b0,pair_value};
        incdec_source = 8'h00;
        unique case (opcode[5:3])
            3'b000: incdec_source = b;
            3'b001: incdec_source = c;
            3'b010: incdec_source = d;
            3'b011: incdec_source = e;
            3'b100: incdec_source = h;
            3'b101: incdec_source = l;
            3'b110: incdec_source = operand;
            3'b111: incdec_source = accumulator;
        endcase
        incdec_result = is_inr ? incdec_source + 8'd1 : incdec_source - 8'd1;
        alu_ext = 9'h000;
        alu_nibble_ext = 5'h00;
        alu_result = 8'h00;
        unique case (opcode[5:3])
            3'b000: begin alu_ext = {1'b0, accumulator} + {1'b0, source_value}; alu_nibble_ext = {1'b0, accumulator[3:0]} + {1'b0, source_value[3:0]}; end
            3'b001: begin alu_ext = {1'b0, accumulator} + {1'b0, source_value} + carry; alu_nibble_ext = {1'b0, accumulator[3:0]} + {1'b0, source_value[3:0]} + carry; end
            3'b010: begin alu_ext = {1'b0, accumulator} - {1'b0, source_value}; alu_nibble_ext = {1'b0, accumulator[3:0]} - {1'b0, source_value[3:0]}; end
            3'b011: begin alu_ext = {1'b0, accumulator} - {1'b0, source_value} - carry; alu_nibble_ext = {1'b0, accumulator[3:0]} - {1'b0, source_value[3:0]} - carry; end
            3'b100: alu_ext = {1'b0, accumulator & source_value};
            3'b101: alu_ext = {1'b0, accumulator ^ source_value};
            3'b110: alu_ext = {1'b0, accumulator | source_value};
            3'b111: begin alu_ext = {1'b0, accumulator} - {1'b0, source_value}; alu_nibble_ext = {1'b0, accumulator[3:0]} - {1'b0, source_value[3:0]}; end
        endcase
        alu_result = alu_ext[7:0];
        daa_low_adjust = (accumulator[3:0] > 4'd9) || aux_carry;
        daa_low_sum = {1'b0,accumulator[3:0]} + (daa_low_adjust ? 5'd6 : 5'd0);
        daa_stage1 = accumulator + (daa_low_adjust ? 8'h06 : 8'h00);
        daa_high_adjust = (daa_stage1[7:4] > 4'd9) || carry;
        daa_high_sum = {1'b0,daa_stage1[7:4]} + (daa_high_adjust ? 5'd6 : 5'd0);
        mem_req = 1'b0;
        mem_write = !reset && (state == MEM_WRITE || state == STACK_WRITE_HIGH || state == STACK_WRITE_LOW);
        mem_addr = pc;
        mem_wdata = 8'h00;
        io_req = 1'b0;
        io_write = 1'b0;
        io_port = 8'h00;
        io_wdata = 8'h00;
        if (state == IO_WAIT && !reset) begin
            io_req = 1'b1;
            io_write = (opcode == 8'hd3);
            io_port = operand;
            io_wdata = accumulator;
        end
        service_irq = ((state == FETCH) || (state == HALTED)) && irq && interrupts_enabled && !ei_pending;
        if (state == FETCH || state == FETCH_WAIT || state == IMM_FETCH || state == IMM_WAIT || state == IMM_HIGH_FETCH || state == IMM_HIGH_WAIT ||
            state == MEM_OPERAND || state == MEM_WAIT || state == MEM_WRITE || state == STACK_WRITE_HIGH || state == STACK_WRITE_LOW || state == STACK_READ_LOW || state == STACK_READ_HIGH) begin
            mem_req = !reset && !service_irq;
            if (state == MEM_OPERAND || state == MEM_WAIT || state == MEM_WRITE) begin
                if (is_direct_mem) mem_addr = stack_target_pc + ((is_direct_word_mem && memory_transfer_phase) ? 16'd1 : 16'd0);
                else if (is_indirect_mem) mem_addr = opcode[4] ? {d,e} : {b,c};
                else mem_addr = {h,l};
            end
            if (state == MEM_WRITE) begin
                if (opcode == 8'h22) mem_wdata = memory_transfer_phase ? h : l;
                else mem_wdata = (is_direct_mem || is_indirect_mem) ? accumulator : (is_inr || is_dcr) ? incdec_result : source_value;
            end else if (state == STACK_WRITE_HIGH) begin
                if (opcode == 8'he3) begin mem_addr = stack_pointer + 16'd1; mem_wdata = h; end
                else begin mem_addr = stack_pointer - 16'd1; mem_wdata = stack_return_pc[15:8]; end
            end else if (state == STACK_WRITE_LOW) begin
                if (opcode == 8'he3) begin mem_addr = stack_pointer; mem_wdata = l; end
                else begin mem_addr = stack_pointer - 16'd2; mem_wdata = stack_return_pc[7:0]; end
            end else if (state == STACK_READ_LOW) begin
                mem_addr = stack_pointer;
            end else if (state == STACK_READ_HIGH) begin
                mem_addr = stack_pointer + 16'd1;
            end
        end
    end

    always_ff @(posedge clk) begin
        if (reset) begin
            state <= FETCH;
            pc <= 16'h0000;
            stack_pointer <= 16'h0000;
            opcode <= 8'h00;
            operand <= 8'h00;
            accumulator <= 8'h00;
            b <= 8'h00; c <= 8'h00; d <= 8'h00; e <= 8'h00; h <= 8'h00; l <= 8'h00;
            sign <= 1'b0; zero <= 1'b0; parity <= 1'b0; carry <= 1'b0; aux_carry <= 1'b0;
            halted <= 1'b0; unsupported <= 1'b0; irq_ack <= 1'b0;
            interrupts_enabled <= 1'b0; ei_pending <= 1'b0;
            memory_operand_valid <= 1'b0;
            immediate_operand_valid <= 1'b0;
            memory_transfer_phase <= 1'b0;
            stack_target_pc <= 16'h0000; stack_return_pc <= 16'h0000; stack_low_byte <= 8'h00; stack_high_byte <= 8'h00;
        end else begin
            irq_ack <= 1'b0;
            case (state)
                FETCH: begin
                    if (service_irq) begin
                        opcode <= irq_opcode;
                        irq_ack <= 1'b1;
                        state <= EXECUTE;
                    end else if (mem_ready) begin
                        opcode <= mem_rdata;
                        pc <= pc + 16'd1;
                        // EI takes effect only after the following instruction has been fetched;
                        // IRQ acceptance is suppressed while ei_pending is set.
                        if (ei_pending) begin
                            interrupts_enabled <= 1'b1;
                            ei_pending <= 1'b0;
                        end
                        state <= EXECUTE;
                    end else state <= FETCH_WAIT;
                end
                FETCH_WAIT: if (mem_ready) begin
                    opcode <= mem_rdata;
                    pc <= pc + 16'd1;
                    if (ei_pending) begin
                        interrupts_enabled <= 1'b1;
                        ei_pending <= 1'b0;
                    end
                    state <= EXECUTE;
                end
                EXECUTE: begin
                    if (is_nop) begin
                        state <= FETCH;
                    end else if (is_inr || is_dcr) begin
                        if (opcode[5:3] == 3'b110 && !memory_operand_valid) begin
                            state <= MEM_OPERAND;
                        end else if (opcode[5:3] == 3'b110) begin
                            state <= MEM_WRITE;
                        end else begin
                            unique case (opcode[5:3])
                                3'b000: b <= incdec_result;
                                3'b001: c <= incdec_result;
                                3'b010: d <= incdec_result;
                                3'b011: e <= incdec_result;
                                3'b100: h <= incdec_result;
                                3'b101: l <= incdec_result;
                                3'b111: accumulator <= incdec_result;
                                default: begin end
                            endcase
                            sign <= incdec_result[7];
                            zero <= (incdec_result == 8'h00);
                            parity <= ~^incdec_result;
                            aux_carry <= is_inr ? (incdec_source[3:0] == 4'hf) : (incdec_source[3:0] == 4'h0);
                            memory_operand_valid <= 1'b0;
                            state <= FETCH;
                        end
                    end else if (opcode >= 8'h40 && opcode <= 8'h7f && opcode != 8'h76) begin
                        if (opcode[2:0] == 3'b110 && !memory_operand_valid) begin
                            state <= MEM_OPERAND;
                        end else if (opcode[5:3] == 3'b110) begin
                            state <= MEM_WRITE;
                        end else begin
                            unique case (opcode[5:3])
                                3'b000: b <= source_value;
                                3'b001: c <= source_value;
                                3'b010: d <= source_value;
                                3'b011: e <= source_value;
                                3'b100: h <= source_value;
                                3'b101: l <= source_value;
                                3'b111: accumulator <= source_value;
                                default: begin end
                            endcase
                            memory_operand_valid <= 1'b0;
                            state <= FETCH;
                        end
                    end else if (is_inx || is_dcx) begin
                        unique case (opcode[5:4])
                            2'b00: {b,c} <= pair_result;
                            2'b01: {d,e} <= pair_result;
                            2'b10: {h,l} <= pair_result;
                            2'b11: stack_pointer <= pair_result;
                        endcase
                        state <= FETCH;
                    end else if (is_dad) begin
                        {h,l} <= pair_sum[15:0];
                        carry <= pair_sum[16];
                        state <= FETCH;
                    end else if (is_lxi || is_jmp || is_jcc || is_call || is_direct_mem) begin
                        state <= IMM_FETCH;
                    end else if (is_indirect_mem) begin
                        if (opcode == 8'h02 || opcode == 8'h12) state <= MEM_WRITE;
                        else state <= MEM_OPERAND;
                    end else if (opcode == 8'he3) begin
                        state <= STACK_READ_LOW;
                    end else if (opcode == 8'he9) begin
                        pc <= {h,l}; state <= FETCH;
                    end else if (opcode == 8'heb) begin
                        {d,e} <= {h,l}; {h,l} <= {d,e}; state <= FETCH;
                    end else if (opcode == 8'hf9) begin
                        stack_pointer <= {h,l}; state <= FETCH;
                    end else if (is_push) begin
                        unique case (opcode[5:4])
                            2'b00: stack_return_pc <= {b,c};
                            2'b01: stack_return_pc <= {d,e};
                            2'b10: stack_return_pc <= {h,l};
                            2'b11: stack_return_pc <= {accumulator,sign,zero,1'b0,aux_carry,1'b0,parity,1'b1,carry};
                        endcase
                        stack_target_pc <= pc;
                        state <= STACK_WRITE_HIGH;
                    end else if (is_pop) begin
                        state <= STACK_READ_LOW;
                    end else if (is_ret) begin
                        if (!is_cond_ret || branch_taken) state <= STACK_READ_LOW;
                        else state <= FETCH;
                    end else if (is_rst) begin
                        stack_target_pc <= {13'h0000, opcode[5:3], 3'b000};
                        stack_return_pc <= pc;
                        state <= STACK_WRITE_HIGH;
                    end else if (is_imm_alu && !immediate_operand_valid) begin
                        state <= IMM_FETCH;
                    end else if ((opcode >= 8'h80 && opcode <= 8'hbf) || is_imm_alu) begin
                        if (opcode[2:0] == 3'b110 && !memory_operand_valid && !is_imm_alu) begin
                            // Register/memory ALU group. The (HL) operand uses the synchronous memory channel.
                            state <= MEM_OPERAND;
                        end else begin
                            if (opcode[5:3] != 3'b111) accumulator <= alu_result;
                            sign <= alu_result[7]; zero <= (alu_result == 8'h00);
                            parity <= ~^alu_result;
                            if (opcode[5:3] <= 3'b011 || opcode[5:3] == 3'b111) begin
                                carry <= alu_ext[8];
                                // SUB/SBB/CMP use the carry-out of the equivalent
                                // two's-complement nibble addition. The widened
                                // subtraction expression's bit 4 is borrow, so
                                // invert it for Intel 8080 auxiliary carry.
                                if (opcode[5:3] == 3'b010 || opcode[5:3] == 3'b011 || opcode[5:3] == 3'b111)
                                    aux_carry <= ~alu_nibble_ext[4];
                                else
                                    aux_carry <= alu_nibble_ext[4];
                            end else begin
                                carry <= 1'b0;
                                // Intel 8080 ANA/ANI set AC to the logical OR
                                // of bit 3 in the two inputs (8085 differs).
                                aux_carry <= (opcode[5:3] == 3'b100) ? (accumulator[3] | source_value[3]) : 1'b0;
                            end
                            memory_operand_valid <= 1'b0;
                            immediate_operand_valid <= 1'b0;
                            state <= FETCH;
                        end
                    end else if (is_rotate) begin
                        unique case (opcode)
                            8'h07: begin accumulator <= {accumulator[6:0],accumulator[7]}; carry <= accumulator[7]; end
                            8'h0f: begin accumulator <= {accumulator[0],accumulator[7:1]}; carry <= accumulator[0]; end
                            8'h17: begin accumulator <= {accumulator[6:0],carry}; carry <= accumulator[7]; end
                            8'h1f: begin accumulator <= {carry,accumulator[7:1]}; carry <= accumulator[0]; end
                            default: begin end
                        endcase
                        state <= FETCH;
                    end else if (opcode == 8'h2f) begin
                        accumulator <= ~accumulator; state <= FETCH;
                    end else if (opcode == 8'h37) begin
                        carry <= 1'b1; state <= FETCH;
                    end else if (opcode == 8'h3f) begin
                        carry <= ~carry; state <= FETCH;
                    end else if (opcode == 8'h27) begin
                        accumulator <= {daa_high_sum[3:0],daa_stage1[3:0]};
                        sign <= daa_high_sum[3];
                        zero <= ({daa_high_sum[3:0],daa_stage1[3:0]} == 8'h00);
                        parity <= ~^{daa_high_sum[3:0],daa_stage1[3:0]};
                        aux_carry <= daa_low_sum[4];
                        if (daa_high_sum[4]) carry <= 1'b1;
                        state <= FETCH;
                    end else if (is_mvi || opcode == 8'hd3 || opcode == 8'hdb) begin
                        state <= IMM_FETCH;
                    end else if (opcode == 8'h76) begin
                        // The opcode fetch already advanced PC to the next
                        // sequential byte, as specified for Intel 8080 HLT.
                        halted <= 1'b1;
                        state <= HALTED;
                    end else if (opcode == 8'hf3) begin
                        interrupts_enabled <= 1'b0; ei_pending <= 1'b0; state <= FETCH;
                    end else if (opcode == 8'hfb) begin
                        ei_pending <= 1'b1; state <= FETCH;
                    end else begin
                        unsupported <= 1'b1;
                        state <= FETCH;
                    end
                end
                IMM_FETCH, IMM_WAIT: begin
                    if (mem_ready) begin
                        operand <= mem_rdata;
                        pc <= pc + 16'd1;
                        if (is_lxi || is_jmp || is_jcc || is_call || is_direct_mem) begin
                            state <= IMM_HIGH_FETCH;
                        end else if (opcode == 8'hd3 || opcode == 8'hdb) begin
                            state <= IO_WAIT;
                        end else if (is_imm_alu) begin
                            operand <= mem_rdata;
                            immediate_operand_valid <= 1'b1;
                            pc <= pc + 16'd1;
                            state <= EXECUTE;
                        end else if (is_mvi) begin
                            unique case (opcode[5:3])
                                3'b000: b <= mem_rdata;
                                3'b001: c <= mem_rdata;
                                3'b010: d <= mem_rdata;
                                3'b011: e <= mem_rdata;
                                3'b100: h <= mem_rdata;
                                3'b101: l <= mem_rdata;
                                3'b111: accumulator <= mem_rdata;
                                default: begin end
                            endcase
                            if (opcode[5:3] == 3'b110) state <= MEM_WRITE;
                            else state <= FETCH;
                        end else state <= FETCH;
                    end else state <= IMM_WAIT;
                end
                IMM_HIGH_FETCH, IMM_HIGH_WAIT: begin
                    if (mem_ready) begin
                        if (is_lxi) begin
                            unique case (opcode[5:4])
                                2'b00: {b,c} <= {mem_rdata,operand};
                                2'b01: {d,e} <= {mem_rdata,operand};
                                2'b10: {h,l} <= {mem_rdata,operand};
                                2'b11: stack_pointer <= {mem_rdata,operand};
                            endcase
                            pc <= pc + 16'd1;
                            state <= FETCH;
                        end else if (is_call) begin
                            if (!is_cond_call || branch_taken) begin
                                stack_target_pc <= {mem_rdata,operand};
                                stack_return_pc <= pc + 16'd1;
                                state <= STACK_WRITE_HIGH;
                            end else begin
                                pc <= pc + 16'd1;
                                state <= FETCH;
                            end
                        end else if (is_direct_mem) begin
                            stack_target_pc <= {mem_rdata,operand};
                            pc <= pc + 16'd1;
                            memory_transfer_phase <= 1'b0;
                            if (opcode == 8'h32 || opcode == 8'h22) state <= MEM_WRITE;
                            else state <= MEM_OPERAND;
                        end else if (is_jmp || (is_jcc && branch_taken)) begin
                            pc <= {mem_rdata,operand};
                            state <= FETCH;
                        end else begin
                            pc <= pc + 16'd1;
                            state <= FETCH;
                        end
                    end else state <= IMM_HIGH_WAIT;
                end
                STACK_WRITE_HIGH: if (mem_ready) state <= STACK_WRITE_LOW;
                STACK_WRITE_LOW: if (mem_ready) begin
                    if (opcode == 8'he3) begin
                        {h,l} <= {stack_high_byte,stack_low_byte};
                        state <= FETCH;
                    end else begin
                        stack_pointer <= stack_pointer - 16'd2;
                        pc <= stack_target_pc;
                        state <= FETCH;
                    end
                end
                STACK_READ_LOW: if (mem_ready) begin
                    stack_low_byte <= mem_rdata;
                    state <= STACK_READ_HIGH;
                end
                STACK_READ_HIGH: if (mem_ready) begin
                    if (opcode == 8'he3) begin
                        stack_high_byte <= mem_rdata;
                        state <= STACK_WRITE_HIGH;
                    end else if (is_pop) begin
                        stack_pointer <= stack_pointer + 16'd2;
                        unique case (opcode[5:4])
                            2'b00: {b,c} <= {mem_rdata,stack_low_byte};
                            2'b01: {d,e} <= {mem_rdata,stack_low_byte};
                            2'b10: {h,l} <= {mem_rdata,stack_low_byte};
                            2'b11: begin
                                accumulator <= mem_rdata;
                                sign <= stack_low_byte[7]; zero <= stack_low_byte[6];
                                aux_carry <= stack_low_byte[4]; parity <= stack_low_byte[2];
                                carry <= stack_low_byte[0];
                            end
                        endcase
                        state <= FETCH;
                    end else begin
                        stack_pointer <= stack_pointer + 16'd2;
                        pc <= {mem_rdata,stack_low_byte};
                        state <= FETCH;
                    end
                end
                IO_WAIT: if (io_ready) begin
                    if (opcode == 8'hdb) accumulator <= io_rdata;
                    state <= FETCH;
                end
                MEM_OPERAND: if (mem_ready) begin
                    if (opcode == 8'h2a) begin
                        if (memory_transfer_phase) begin h <= mem_rdata; memory_transfer_phase <= 1'b0; state <= FETCH; end
                        else begin l <= mem_rdata; memory_transfer_phase <= 1'b1; state <= MEM_OPERAND; end
                    end else if (is_direct_mem || is_indirect_mem) begin
                        accumulator <= mem_rdata;
                        state <= FETCH;
                    end else begin
                        operand <= mem_rdata;
                        memory_operand_valid <= 1'b1;
                        state <= EXECUTE;
                    end
                end else state <= MEM_WAIT;
                MEM_WAIT: if (mem_ready) begin
                    if (opcode == 8'h2a) begin
                        if (memory_transfer_phase) begin h <= mem_rdata; memory_transfer_phase <= 1'b0; state <= FETCH; end
                        else begin l <= mem_rdata; memory_transfer_phase <= 1'b1; state <= MEM_OPERAND; end
                    end else if (is_direct_mem || is_indirect_mem) begin
                        accumulator <= mem_rdata;
                        state <= FETCH;
                    end else begin
                        operand <= mem_rdata;
                        memory_operand_valid <= 1'b1;
                        state <= EXECUTE;
                    end
                end
                MEM_WRITE: if (mem_ready) begin
                    if (opcode == 8'h22 && !memory_transfer_phase) begin
                        memory_transfer_phase <= 1'b1;
                        state <= MEM_WRITE;
                    end else begin
                        if (is_inr || is_dcr) begin
                            sign <= incdec_result[7];
                            zero <= (incdec_result == 8'h00);
                            parity <= ~^incdec_result;
                            aux_carry <= is_inr ? (incdec_source[3:0] == 4'hf) : (incdec_source[3:0] == 4'h0);
                        end
                        memory_operand_valid <= 1'b0;
                        if (opcode == 8'h22) memory_transfer_phase <= 1'b0;
                        state <= FETCH;
                    end
                end
                HALTED: if (service_irq) begin
                    opcode <= irq_opcode;
                    irq_ack <= 1'b1;
                    halted <= 1'b0;
                    state <= EXECUTE;
                end
                default: state <= FETCH;
            endcase
        end
    end
endmodule
