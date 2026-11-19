module cw8080_alu_add (
    input  logic [7:0] a,
    input  logic [7:0] b,
    input  logic       carry_in,
    input  logic       carry_enable,
    output logic [7:0] result,
    output logic       sign,
    output logic       zero,
    output logic       parity,
    output logic       carry,
    output logic       aux_carry
);
    logic [8:0] sum;
    always_comb begin
        sum = {1'b0, a} + {1'b0, b} + (carry_enable ? carry_in : 1'b0);
        result = sum[7:0];
        sign = result[7];
        zero = (result == 8'h00);
        parity = ~^result;
        carry = sum[8];
        aux_carry = ({1'b0, a[3:0]} + {1'b0, b[3:0]} +
                     (carry_enable ? carry_in : 1'b0)) > 5'h0f;
    end
endmodule

module cw8080_add_register_slice (
    input  logic       clk,
    input  logic       reset,
    input  logic       load,
    input  logic       add_immediate,
    input  logic       carry_enable,
    input  logic       carry_in,
    input  logic [7:0] operand,
    output logic [7:0] accumulator,
    output logic       sign,
    output logic       zero,
    output logic       parity,
    output logic       carry,
    output logic       aux_carry
);
    logic [7:0] next_accumulator;
    logic next_sign, next_zero, next_parity, next_carry, next_aux_carry;

    cw8080_alu_add alu (
        .a(accumulator), .b(operand), .carry_in(carry_in),
        .carry_enable(carry_enable), .result(next_accumulator),
        .sign(next_sign), .zero(next_zero), .parity(next_parity),
        .carry(next_carry), .aux_carry(next_aux_carry)
    );

    always_ff @(posedge clk) begin
        if (reset) begin
            accumulator <= 8'h00;
            sign <= 1'b0;
            zero <= 1'b0;
            parity <= 1'b0;
            carry <= 1'b0;
            aux_carry <= 1'b0;
        end else if (load) begin
            accumulator <= add_immediate ? next_accumulator : operand;
            if (add_immediate) begin
                sign <= next_sign;
                zero <= next_zero;
                parity <= next_parity;
                carry <= next_carry;
                aux_carry <= next_aux_carry;
            end
        end
    end
endmodule
module cw8080_add_adc_decode (
    input logic [7:0] opcode, a, b, c, d, e, h, l, memory_hl,
    input logic carry_in,
    output logic supported, writes_a,
    output logic [7:0] result,
    output logic sign, zero, parity, carry, aux_carry
);
    logic [7:0] source_value;
    logic use_carry;
    always_comb begin
        supported = (opcode >= 8'h80 && opcode <= 8'h8f);
        writes_a = supported;
        source_value = 8'h00;
        use_carry = (opcode >= 8'h88 && opcode <= 8'h8f);
        unique case (opcode[2:0])
            3'b000: source_value = b;
            3'b001: source_value = c;
            3'b010: source_value = d;
            3'b011: source_value = e;
            3'b100: source_value = h;
            3'b101: source_value = l;
            3'b110: source_value = memory_hl;
            3'b111: source_value = a;
        endcase
    end
    cw8080_alu_add alu (.a(a), .b(source_value), .carry_in(carry_in),
        .carry_enable(use_carry && supported), .result(result), .sign(sign),
        .zero(zero), .parity(parity), .carry(carry), .aux_carry(aux_carry));
endmodule
module cw8080_add_adc_exec (
    input logic clk, reset, execute,
    input logic [7:0] opcode,
    input logic [7:0] b, c, d, e, h, l, memory_hl,
    output logic [7:0] accumulator,
    output logic sign, zero, parity, carry, aux_carry,
    output logic supported
);
    logic [7:0] operand, alu_result;
    logic alu_sign, alu_zero, alu_parity, alu_carry, alu_aux_carry;
    logic is_adc;
    always_comb begin
        supported = (opcode >= 8'h80 && opcode <= 8'h8f);
        is_adc = (opcode >= 8'h88 && opcode <= 8'h8f);
        unique case (opcode[2:0])
            3'b000: operand=b; 3'b001: operand=c;
            3'b010: operand=d; 3'b011: operand=e;
            3'b100: operand=h; 3'b101: operand=l;
            3'b110: operand=memory_hl; 3'b111: operand=accumulator;
        endcase
    end
    cw8080_alu_add alu (.a(accumulator), .b(operand), .carry_in(carry),
        .carry_enable(is_adc && supported), .result(alu_result), .sign(alu_sign),
        .zero(alu_zero), .parity(alu_parity), .carry(alu_carry),
        .aux_carry(alu_aux_carry));
    always_ff @(posedge clk) begin
        if (reset) begin
            accumulator<=0; sign<=0; zero<=0; parity<=0; carry<=0; aux_carry<=0;
        end else if (execute && supported) begin
            accumulator<=alu_result; sign<=alu_sign; zero<=alu_zero;
            parity<=alu_parity; carry<=alu_carry; aux_carry<=alu_aux_carry;
        end
    end
endmodule
module cw8080_immediate_exec (
    input logic clk, reset, execute,
    input logic [7:0] opcode, immediate,
    output logic [15:0] pc,
    output logic [7:0] accumulator,
    output logic sign, zero, parity, carry, aux_carry, halted,
    output logic supported
);
    logic [8:0] sum;
    logic [7:0] add_result;
    always_comb begin
        supported = (opcode==8'h3e || opcode==8'hc6 || opcode==8'h76);
        sum = {1'b0, accumulator} + {1'b0, immediate};
        add_result = sum[7:0];
    end
    always_ff @(posedge clk) begin
        if (reset) begin
            pc<=0; accumulator<=0; sign<=0; zero<=0; parity<=0;
            carry<=0; aux_carry<=0; halted<=0;
        end else if (execute && supported && !halted) begin
            unique case (opcode)
                8'h3e: begin accumulator<=immediate; pc<=pc+16'd2; end
                8'hc6: begin
                    accumulator<=add_result; sign<=add_result[7];
                    zero<=(add_result==0); parity<=~^add_result;
                    carry<=sum[8]; pc<=pc+16'd2;
                    // Source Ops.ADI uses CalcSZPC and does not update AuxCarry.
                end
                8'h76: begin halted<=1'b1; pc<=pc+16'd1; end
                default: begin end
            endcase
        end
    end
endmodule
