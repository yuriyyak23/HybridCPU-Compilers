// Partial 8080 implementation. Supported: INR/DCR r/M, MOV 40-7F except HLT, ALU 80-BF, MVI A, ADI, HLT.
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
    output logic [7:0]  accumulator,
    output logic [7:0]  b, c, d, e, h, l,
    output logic        sign, zero, parity, carry, aux_carry,
    output logic        halted,
    output logic        unsupported
);
    typedef enum logic [3:0] {FETCH, FETCH_WAIT, EXECUTE, IMM_FETCH, IMM_WAIT, MEM_OPERAND, MEM_WAIT, HALTED, MEM_WRITE} state_t;
    state_t state;
    logic [7:0] opcode;
    logic [7:0] operand;
    logic [7:0] source_value;
    logic [7:0] incdec_source;
    logic [7:0] incdec_result;
    logic is_inr;
    logic is_dcr;
    logic [8:0] alu_ext;
    logic [4:0] alu_nibble_ext;
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
        mem_req = 1'b0;
        mem_write = !reset && (state == MEM_WRITE);
        mem_addr = pc;
        mem_wdata = 8'h00;
        io_req = 1'b0;
        io_write = 1'b0;
        io_port = 8'h00;
        io_wdata = 8'h00;
        service_irq = ((state == FETCH) || (state == HALTED)) && irq && interrupts_enabled && !ei_pending;
        if (state == FETCH || state == FETCH_WAIT || state == IMM_FETCH || state == IMM_WAIT ||
            state == MEM_OPERAND || state == MEM_WAIT || state == MEM_WRITE) begin
            mem_req = !reset && !service_irq;
            mem_addr = (state == MEM_OPERAND || state == MEM_WAIT) ? {h, l} : pc;
            if (state == MEM_WRITE) begin
                mem_addr = {h, l};
                mem_wdata = (is_inr || is_dcr) ? incdec_result : source_value;
            end
        end
    end

    always_ff @(posedge clk) begin
        if (reset) begin
            state <= FETCH;
            pc <= 16'h0000;
            opcode <= 8'h00;
            operand <= 8'h00;
            accumulator <= 8'h00;
            b <= 8'h00; c <= 8'h00; d <= 8'h00; e <= 8'h00; h <= 8'h00; l <= 8'h00;
            sign <= 1'b0; zero <= 1'b0; parity <= 1'b0; carry <= 1'b0; aux_carry <= 1'b0;
            halted <= 1'b0; unsupported <= 1'b0; irq_ack <= 1'b0;
            interrupts_enabled <= 1'b0; ei_pending <= 1'b0;
            memory_operand_valid <= 1'b0;
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
                    if (is_inr || is_dcr) begin
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
                    end else if (opcode >= 8'h80 && opcode <= 8'hbf) begin
                        if (opcode[2:0] == 3'b110 && !memory_operand_valid) begin
                            // Register/memory ALU group. The (HL) operand uses the synchronous memory channel.
                            state <= MEM_OPERAND;
                        end else begin
                            if (opcode[5:3] != 3'b111) accumulator <= alu_result;
                            sign <= alu_result[7]; zero <= (alu_result == 8'h00);
                            parity <= ~^alu_result;
                            if (opcode[5:3] <= 3'b011 || opcode[5:3] == 3'b111) begin
                                carry <= alu_ext[8];
                                aux_carry <= alu_nibble_ext[4];
                            end else begin
                                carry <= 1'b0;
                                aux_carry <= 1'b0;
                            end
                            memory_operand_valid <= 1'b0;
                            state <= FETCH;
                        end
                    end else if (opcode == 8'h3e || opcode == 8'hc6) begin
                        state <= IMM_FETCH;
                    end else if (opcode == 8'h76) begin
                        pc <= pc - 16'd1; // Source Ops.HLT leaves PC on the HLT opcode.
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
                IMM_FETCH: begin
                    if (mem_ready) begin
                        operand <= mem_rdata;
                        pc <= pc + 16'd1;
                        if (opcode == 8'h3e) begin
                            accumulator <= mem_rdata;
                            state <= FETCH;
                        end else if (opcode == 8'hc6) begin
                            accumulator <= accumulator + mem_rdata;
                            sign <= (accumulator + mem_rdata) >> 7;
                            zero <= ((accumulator + mem_rdata) == 8'h00);
                            parity <= ~^(accumulator + mem_rdata);
                            carry <= (({1'b0, accumulator} + {1'b0, mem_rdata}) >> 8);
                            // Ops.ADI calls CalcSZPC but preserves auxiliary carry.
                            state <= FETCH;
                        end else begin state <= FETCH; end
                    end else state <= IMM_WAIT;
                end
                IMM_WAIT: if (mem_ready) begin
                    operand <= mem_rdata;
                    pc <= pc + 16'd1;
                    if (opcode == 8'h3e) begin accumulator <= mem_rdata; state <= FETCH; end
                    else if (opcode == 8'hc6) begin
                        accumulator <= accumulator + mem_rdata;
                        sign <= (accumulator + mem_rdata) >> 7;
                        zero <= ((accumulator + mem_rdata) == 8'h00);
                        parity <= ~^(accumulator + mem_rdata);
                        carry <= (({1'b0, accumulator} + {1'b0, mem_rdata}) >> 8);
                        state <= FETCH;
                    end else state <= FETCH;
                end
                MEM_OPERAND: if (mem_ready) begin
                    operand <= mem_rdata;
                    memory_operand_valid <= 1'b1;
                    state <= EXECUTE;
                end else state <= MEM_WAIT;
                MEM_WAIT: if (mem_ready) begin
                    operand <= mem_rdata;
                    memory_operand_valid <= 1'b1;
                    state <= EXECUTE;
                end
                MEM_WRITE: if (mem_ready) begin
                    if (is_inr || is_dcr) begin
                        sign <= incdec_result[7];
                        zero <= (incdec_result == 8'h00);
                        parity <= ~^incdec_result;
                        aux_carry <= is_inr ? (incdec_source[3:0] == 4'hf) : (incdec_source[3:0] == 4'h0);
                    end
                    memory_operand_valid <= 1'b0;
                    state <= FETCH;
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
