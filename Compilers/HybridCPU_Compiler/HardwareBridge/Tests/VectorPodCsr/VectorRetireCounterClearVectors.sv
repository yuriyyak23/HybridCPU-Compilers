`timescale 1ns/1ps
module vector_retire_counter_clear_tb;
  reg effect_present;
  reg clears_architectural_exception_state;
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

  retired_csr_exception_counter_transition dut (
    .effect_present(effect_present),
    .clears_architectural_exception_state(clears_architectural_exception_state),
    .current_overflow_count(current_overflow_count),
    .current_underflow_count(current_underflow_count),
    .current_div_by_zero_count(current_div_by_zero_count),
    .current_invalid_op_count(current_invalid_op_count),
    .current_inexact_count(current_inexact_count),
    .next_overflow_count(next_overflow_count),
    .next_underflow_count(next_underflow_count),
    .next_div_by_zero_count(next_div_by_zero_count),
    .next_invalid_op_count(next_invalid_op_count),
    .next_inexact_count(next_inexact_count)
  );

  initial begin
    fd = $fopen("vector-exception-counter-vectors.txt", "r");
    if (!fd) $fatal(1, "missing vector-exception-counter-vectors.txt");
    count = 0;
    while (!$feof(fd)) begin
      rc = $fscanf(fd, "%h %h %h %h %h %h %h %h %h %h %h %h\n",
        effect_present, clears_architectural_exception_state,
        current_overflow_count, current_underflow_count, current_div_by_zero_count,
        current_invalid_op_count, current_inexact_count,
        exp_overflow_count, exp_underflow_count, exp_div_by_zero_count,
        exp_invalid_op_count, exp_inexact_count);
      if (rc == 12) begin
        #1;
        if (next_overflow_count !== exp_overflow_count ||
            next_underflow_count !== exp_underflow_count ||
            next_div_by_zero_count !== exp_div_by_zero_count ||
            next_invalid_op_count !== exp_invalid_op_count ||
            next_inexact_count !== exp_inexact_count)
          $fatal(1, "retire counter clear mismatch at vector=%0d", count);
        count = count + 1;
      end
    end
    $fclose(fd);
    $display("PASS: %0d retire counter-clear vectors", count);
    $finish;
  end
endmodule
