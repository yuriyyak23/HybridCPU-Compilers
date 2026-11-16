`timescale 1ns/1ps
module vector_pod_csr_tb;
  reg [11:0] csr_addr;
  reg write_enable;
  reg [63:0] write_value;
  reg [7:0] state_mask;
  reg [15:0] state_priority;
  wire [7:0] next_mask;
  wire [15:0] next_priority;
  wire write_address_valid;
  wire read_address_valid;
  wire [63:0] read_data;
  integer fd, count, rc;
  reg [7:0] exp_next_mask;
  reg [15:0] exp_next_priority;
  reg exp_address_valid;
  reg [63:0] exp_read_data;

  vector_pod_csr_write_projection dut_write (
    .csr_addr(csr_addr), .write_enable(write_enable), .write_value(write_value),
    .state_mask(state_mask), .state_priority(state_priority),
    .write_address_valid(write_address_valid), .next_mask(next_mask), .next_priority(next_priority)
  );
  vector_pod_csr_read_projection dut_read (
    .csr_addr(csr_addr), .state_mask(state_mask), .state_priority(state_priority),
    .read_address_valid(read_address_valid), .read_data(read_data)
  );

  initial begin
    fd = $fopen("vector-pod-csr-vectors.txt", "r");
    if (!fd) $fatal(1, "missing vector-pod-csr-vectors.txt");
    count = 0;
    while (!$feof(fd)) begin
      rc = $fscanf(fd, "%h %h %h %h %h %h %h %h %h\n",
        csr_addr, write_enable, write_value, state_mask, state_priority,
        exp_next_mask, exp_next_priority, exp_address_valid, exp_read_data);
      if (rc == 9) begin
        #1;
        if (next_mask !== exp_next_mask || next_priority !== exp_next_priority ||
            write_address_valid !== exp_address_valid || read_address_valid !== exp_address_valid ||
            read_data !== exp_read_data) begin
          $display("Mismatch vector=%0d addr=%h we=%b value=%h mask=%h priority=%h", count,
            csr_addr, write_enable, write_value, state_mask, state_priority);
          $display(" got write mask=%h priority=%h address_valid=%b; read address_valid=%b data=%h", next_mask, next_priority, write_address_valid, read_address_valid, read_data);
          $display(" exp write mask=%h priority=%h address_valid=%b; read data=%h", exp_next_mask, exp_next_priority, exp_address_valid, exp_read_data);
          $fatal(1, "VectorPod CSR RTL mismatch");
        end
        count = count + 1;
      end
    end
    $fclose(fd);
    $display("PASS: %0d VectorPod CSR vectors", count);
    $finish;
  end
endmodule
