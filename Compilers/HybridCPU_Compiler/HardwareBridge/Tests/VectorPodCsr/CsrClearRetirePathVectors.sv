`timescale 1ns/1ps
module csr_clear_retire_path_tb;
  reg instruction_valid;
  reg [15:0] canonical_opcode;
  reg retire_batch_apply_event;
  reg [31:0] current_overflow_count;
  reg [31:0] current_underflow_count;
  reg [31:0] current_div_by_zero_count;
  reg [31:0] current_invalid_op_count;
  reg [31:0] current_inexact_count;
  wire [31:0] next_overflow_count;
  wire [31:0] next_underflow_count;
  wire [31:0] next_div_by_zero_count;
  wire [31:0] next_invalid_op_count;
  wire [31:0] next_inexact_count;
  integer fd, count, rc;
  reg [31:0] exp_overflow_count;
  reg [31:0] exp_underflow_count;
  reg [31:0] exp_div_by_zero_count;
  reg [31:0] exp_invalid_op_count;
  reg [31:0] exp_inexact_count;

  processor_csr_clear_retire_event_projection dut (
    .instruction_valid(instruction_valid), .canonical_opcode(canonical_opcode),
    .retire_batch_apply_event(retire_batch_apply_event),
    .current_overflow_count(current_overflow_count), .current_underflow_count(current_underflow_count),
    .current_div_by_zero_count(current_div_by_zero_count), .current_invalid_op_count(current_invalid_op_count),
    .current_inexact_count(current_inexact_count), .next_overflow_count(next_overflow_count),
    .next_underflow_count(next_underflow_count), .next_div_by_zero_count(next_div_by_zero_count),
    .next_invalid_op_count(next_invalid_op_count), .next_inexact_count(next_inexact_count)
  );

  initial begin
    fd = $fopen("csr-clear-retire-path-vectors.txt", "r");
    if (!fd) $fatal(1, "missing csr-clear-retire-path-vectors.txt");
    count = 0;
    while (!$feof(fd)) begin
      rc = $fscanf(fd, "%h %h %h %h %h %h %h %h %h %h %h %h %h\n",
        canonical_opcode, instruction_valid, retire_batch_apply_event,
        current_overflow_count, current_underflow_count, current_div_by_zero_count,
        current_invalid_op_count, current_inexact_count,
        exp_overflow_count, exp_underflow_count, exp_div_by_zero_count,
        exp_invalid_op_count, exp_inexact_count);
      if (rc == 13) begin
        #1;
        if (next_overflow_count !== exp_overflow_count || next_underflow_count !== exp_underflow_count ||
            next_div_by_zero_count !== exp_div_by_zero_count || next_invalid_op_count !== exp_invalid_op_count ||
            next_inexact_count !== exp_inexact_count)
          $fatal(1, "CSR_CLEAR decode-to-retire mismatch at vector=%0d opcode=%h valid=%b apply=%b",
            count, canonical_opcode, instruction_valid, retire_batch_apply_event);
        count = count + 1;
      end
    end
    $fclose(fd);
    $display("PASS: %0d CSR_CLEAR decode-to-retire event vectors", count);
    $finish;
  end
endmodule
