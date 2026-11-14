# Intel 8080 RTL adapter assets

The initial RTL/testbench copies in the 8080 artifact tree are preserved. The working `cw8080_subset_cpu.sv` and `tb_cw8080_subset_cpu.sv` in this adapter now contain a later manual revision; their versioned artifact copies are:

| Current adapter file | Artifact path | SHA-256 |
|---|---|---|
| `cw8080_add.sv` | `artifacts/8080/rtl/cw8080_add.sv` | `ebab0adbaa68fb8e610fa4922f0e8608ec1a447cba268a2db973f5516d5e6ad` |
| `cw8080_subset_cpu.sv` | `artifacts/8080/rtl/cw8080_subset_cpu_mov_busstable.sv` | `3d8b762a66f20af08e1d0503df40c92057033cbf6f0806513eb1ee7e1bf6f79a` |
| `tb_cw8080_add.sv` | `artifacts/8080/tests/tb_cw8080_add.sv` | `0d1cc964f3d1b38f177f0146702a58a10a41ce6cb0f6a72b9cd398788587b41d` |
| `tb_cw8080_subset_cpu.sv` | `artifacts/8080/tests/tb_cw8080_subset_cpu_mov_busstable.sv` | `804864617546f6127b8357646c75ca81ad216d9439e3fbb223b068da19ad51cb` |

## Test runner

Run `./Test-Intel8080Rtl.ps1` with Icarus Verilog (`iverilog` and `vvp`) on PATH. Output defaults to `CircuitWeave-main/artifacts/8080/tests/rtl-validation`; the script refuses output paths outside `artifacts/8080` and refuses to replace its log or either simulation image. It hashes all inputs into the log, compiles the ALU and integrated CPU benches separately, then executes them. Build and simulator output stays in the selected artifact directory.

The available-tool check found no HDL simulator. The prior attempt is recorded as **NOT RUN** in `CircuitWeave-main/artifacts/8080/tests/rtl-test-20261114.log`. New MOV/HALT/IRQ test assertions have not been compiled or simulated.

## Scope

This is manually authored, partial RTL, not a complete or verified Intel 8080. The integrated module implements 63 MOV encodings (`0x40-0x7F` except HLT), ADD/ADC encodings `0x80-0x8F`, `MVI A`, `ADI`, `HLT`, `DI`, and `EI`. Six ADD/ADC encodings are absent from the emulator dispatch map; additional MOV opcodes are also manually implemented because the source `_ops` dictionary omits them. `MOV (HL)` reads and writes use the synchronous memory ready handshake. The testbench exercises source-mapped MOV register transfers, `MOV M,A`, `MOV A,M`, memory stalls and request/payload stability, HLT bus-idle behavior, EI delay through HLT, and IRQ wake-up with an injected DI opcode.

The implementation does not yet include the rest of the instruction set, complete architectural effects/flags, I/O instruction behavior, or a verified full interrupt service convention. No MLIR, general HWIR lowering or SystemVerilog generator is provided. The versioned `.sv` files and template statuses explicitly remain partial/unverified.

## INR/DCR extension (current working revision)

The integrated RTL now decodes all 16 INR/DCR register and `(HL)` encodings. Twelve have a matching dispatch entry in the inspected emulator source (`0x04`, `0x05`, `0x0D`, `0x14`, `0x15`, `0x1C`, `0x1D`, `0x24`, `0x25`, `0x35`, `0x3C`, `0x3D`); `INR C`, `INR L`, `DCR L`, and `INR M` are manual RTL extensions because those dispatch entries are absent. `(HL)` uses a read-modify-write on the agreed memory handshake. Hardware flags use architectural Z/S/even-P/AC and preserve CY.

This is not behaviorally equivalent to all source helper paths: `Utils.Increment` derives AC from the result low nibble, `Utils.Decrement` does not update AC, and `Ops.DCR_C` additionally calls `CalcSZPC(byte)` which changes CY. Those source semantics need separate emulator correction/compatibility choice before claiming equivalence. The new testbench adds register and memory wrap/flag checks, but remains uncompiled and unsimulated because an HDL simulator is unavailable. Versioned RTL/testbench artifacts are `artifacts/8080/rtl/cw8080_subset_cpu_mov_incdec.sv` and `artifacts/8080/tests/tb_cw8080_subset_cpu_mov_incdec.sv`; see their SHA-256 hashes in `hardware-model-8080-bridge-derived-incdec.cwtemplate.json`.
