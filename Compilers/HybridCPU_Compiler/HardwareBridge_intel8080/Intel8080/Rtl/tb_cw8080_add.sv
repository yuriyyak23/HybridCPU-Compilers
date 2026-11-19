`timescale 1ns/1ps
module tb_cw8080_add;
    logic clk = 0;
    always #5 clk = ~clk;
    logic reset, load, add_immediate, carry_enable, carry_in;
    logic [7:0] operand;
    logic [7:0] accumulator;
    logic sign, zero, parity, carry, aux_carry;
    logic [7:0] a_vec, b_vec, result_vec;
    logic cin_vec, carry_enable_vec, s_vec, z_vec, p_vec, c_vec, ac_vec;
    int directed_checks = 0;
    int exhaustive_checks = 0;
    logic [7:0] opcode, reg_a, reg_b, reg_c, reg_d, reg_e, reg_h, reg_l, reg_m;
    logic dec_carry_in, supported, writes_a;
    logic [7:0] dec_result;
    logic dec_sign, dec_zero, dec_parity, dec_carry, dec_aux_carry;
    logic exec_reset, execute, exec_supported;
    logic [7:0] exec_opcode, exec_b, exec_c, exec_d, exec_e, exec_h, exec_l, exec_m, exec_a;
    logic exec_s, exec_z, exec_p, exec_cy, exec_ac;
    logic imm_reset, imm_execute, imm_supported, imm_s, imm_z, imm_p, imm_cy, imm_ac, halted;
    logic [7:0] imm_opcode, imm_byte, imm_a;
    logic [15:0] imm_pc;

    cw8080_add_register_slice dut (.*);
    cw8080_immediate_exec imm_dut (.clk(clk), .reset(imm_reset), .execute(imm_execute),
        .opcode(imm_opcode), .immediate(imm_byte), .pc(imm_pc), .accumulator(imm_a),
        .sign(imm_s), .zero(imm_z), .parity(imm_p), .carry(imm_cy),
        .aux_carry(imm_ac), .halted(halted), .supported(imm_supported));
    cw8080_add_adc_exec exec_dut (.clk(clk), .reset(exec_reset), .execute(execute),
        .opcode(exec_opcode), .b(exec_b), .c(exec_c), .d(exec_d), .e(exec_e),
        .h(exec_h), .l(exec_l), .memory_hl(exec_m), .accumulator(exec_a),
        .sign(exec_s), .zero(exec_z), .parity(exec_p), .carry(exec_cy),
        .aux_carry(exec_ac), .supported(exec_supported));
    cw8080_add_adc_decode decoder (.opcode(opcode), .a(reg_a), .b(reg_b), .c(reg_c),
        .d(reg_d), .e(reg_e), .h(reg_h), .l(reg_l), .memory_hl(reg_m),
        .carry_in(dec_carry_in), .supported(supported), .writes_a(writes_a),
        .result(dec_result), .sign(dec_sign), .zero(dec_zero), .parity(dec_parity),
        .carry(dec_carry), .aux_carry(dec_aux_carry));
    cw8080_alu_add alu_check (.a(a_vec), .b(b_vec), .carry_in(cin_vec),
        .carry_enable(carry_enable_vec), .result(result_vec), .sign(s_vec),
        .zero(z_vec), .parity(p_vec), .carry(c_vec), .aux_carry(ac_vec));

    task automatic check(input bit condition, input string label);
        directed_checks++;
        if (!condition) begin $error("FAIL %s", label); $fatal(1); end
    endtask

    task automatic tick;
        @(posedge clk); #1;
    endtask

    initial begin
        reset=1; load=0; add_immediate=0; carry_enable=0; carry_in=0; operand=0;
        exec_reset=1; execute=0; exec_opcode=0; exec_b=0; exec_c=0; exec_d=0;
        exec_e=0; exec_h=0; exec_l=0; exec_m=0;
        imm_reset=1; imm_execute=0; imm_opcode=0; imm_byte=0;
        tick();
        check(imm_pc==0 && imm_a==0 && !halted, "immediate sequencer reset");
        imm_reset=0; imm_execute=1; imm_opcode=8'h3e; imm_byte=8'hff; tick();
        check(imm_supported && imm_pc==2 && imm_a==8'hff, "MVI A immediate and PC advance");
        imm_opcode=8'hc6; imm_byte=1; tick();
        check(imm_pc==4 && imm_a==0 && imm_z && imm_p && imm_cy && imm_ac==0, "ADI carry and preserve AC");
        imm_opcode=8'h76; tick();
        check(halted && imm_pc==5, "HLT stops with PC at the next sequential byte");
        imm_opcode=8'h00; tick();
        check(halted && imm_pc==5 && imm_a==0, "halted sequencer holds state");
        tick();
        check(exec_a==0 && {exec_s,exec_z,exec_p,exec_cy,exec_ac}==0, "instruction slice reset");
        exec_reset=0; exec_b=8'h01; exec_opcode=8'h80; execute=1; tick();
        check(exec_supported && exec_a==1 && !exec_cy, "ADD B updates A");
        exec_b=8'hff; exec_opcode=8'h88; tick();
        check(exec_a==0 && exec_z && exec_p && exec_cy && exec_ac, "ADC B uses old CY and writes flags");
        exec_opcode=8'h90; tick();
        check(!exec_supported && exec_a==0 && exec_z && exec_p && exec_cy && exec_ac, "unsupported opcode preserves state");
        execute=0;
        reset=1; load=0; add_immediate=0; carry_enable=0; carry_in=0; operand=0;
        tick();
        check(accumulator==0 && {sign,zero,parity,carry,aux_carry}==0, "synchronous reset");
        reset=0; load=1; add_immediate=0; operand=8'h0f; tick();
        check(accumulator==8'h0f && {sign,zero,parity,carry,aux_carry}==0, "load operand preserves flags");
        add_immediate=1; operand=8'h01; tick();
        check(accumulator==8'h10 && !sign && !zero && !parity && !carry && aux_carry, "half carry");
        operand=8'hf0; tick();
        check(accumulator==0 && !sign && zero && parity && carry && !aux_carry, "zero carry even parity");
        reset=1; tick(); reset=0;
        load=1; add_immediate=0; operand=8'hff; tick();
        add_immediate=1; carry_enable=1; carry_in=1; operand=0; tick();
        check(accumulator==0 && zero && parity && carry && aux_carry, "ADC carry-in");
        load=0; carry_enable=0; carry_in=0;
        $display("PASS %0d directed register checks", directed_checks);

        reg_a=8'h10; reg_b=8'h01; reg_c=8'h02; reg_d=8'h03; reg_e=8'h04;
        reg_h=8'h05; reg_l=8'h06; reg_m=8'h07; dec_carry_in=1'b1;
        for (int op=8'h80; op<=8'h8f; op++) begin
            opcode=op[7:0]; #1;
            check(supported && writes_a, "ADD/ADC opcode accepted and writes A");
            if (op<8'h88) check(dec_result==reg_a+((op[2:0]==0)?reg_b:(op[2:0]==1)?reg_c:(op[2:0]==2)?reg_d:(op[2:0]==3)?reg_e:(op[2:0]==4)?reg_h:(op[2:0]==5)?reg_l:(op[2:0]==6)?reg_m:reg_a), "ADD source register decode");
            else check(dec_result==reg_a+((op[2:0]==0)?reg_b:(op[2:0]==1)?reg_c:(op[2:0]==2)?reg_d:(op[2:0]==3)?reg_e:(op[2:0]==4)?reg_h:(op[2:0]==5)?reg_l:(op[2:0]==6)?reg_m:reg_a)+1, "ADC source and carry decode");
        end
        opcode=8'h90; #1;
        check(!supported && !writes_a, "SUB opcode remains unsupported");

        for (int av=0; av<256; av++) begin
            for (int bv=0; bv<256; bv++) begin
                for (int ce=0; ce<2; ce++) begin
                    for (int ci=0; ci<2; ci++) begin
                        logic [8:0] expected_sum;
                        logic [7:0] expected_result;
                        a_vec=av[7:0]; b_vec=bv[7:0];
                        carry_enable_vec=ce[0]; cin_vec=ci[0]; #1;
                        expected_sum={1'b0,a_vec}+{1'b0,b_vec}+(ce ? ci : 0);
                        expected_result=expected_sum[7:0];
                        if (result_vec !== expected_result || s_vec !== expected_result[7] ||
                            z_vec !== (expected_result==0) || p_vec !== (~^expected_result) ||
                            c_vec !== expected_sum[8] ||
                            ac_vec !== ((av%16)+(bv%16)+(ce?ci:0)>15)) begin
                            $error("ALU mismatch a=%02x b=%02x ce=%0d ci=%0d",av,bv,ce,ci);
                            $fatal(1);
                        end
                        exhaustive_checks++;
                    end
                end
            end
        end
        $display("PASS ADD/ADC opcode decode vectors 80-8F; SUB 90 rejected");
        $display("PASS sequential writeback, ADC carry feedback, unsupported-state hold");
        $display("PASS MVI A, ADI, HLT sequencing and halted hold");
        $display("PASS %0d exhaustive RTL ALU vectors", exhaustive_checks);
        $finish;
    end
endmodule
