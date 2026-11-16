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


## Register/memory ALU extension (current working revision)

The integrated RTL decodes every register/(HL) arithmetic and logical opcode from `0x80` through `0xBF`: ADD, ADC, SUB, SBB, ANA, XRA, ORA, and CMP. CMP updates flags without writing A; arithmetic includes carry/borrow and auxiliary nibble carry/borrow; logical operations clear CY and AC according to Intel 8080 Assembly Language Programming Manual Rev C. `CMP M` is covered by the integrated test program. The source dispatch contains only 29 of the 64 encodings; the generated template lists the 35 manual RTL-only opcodes per family.

Observed source divergences include `ORA_M` not clearing CY/AC, and earlier INR/DCR helper issues. The RTL follows the documented architectural behavior, so source-model equivalence is not implied. The integrated testbench now exercises SUB/SBB, ANA/XRA/ORA, CMP M and the previous MOV/INR/DCR/HALT/IRQ cases. No HDL compiler/simulator is currently available; these tests are authored but not run.

Current versioned copies are `artifacts/8080/rtl/cw8080_subset_cpu_mov_incdec_alu.sv` and `artifacts/8080/tests/tb_cw8080_subset_cpu_mov_incdec_alu.sv`; the matching editable template is `artifacts/8080/hardware-model-8080-bridge-derived-alu-v2.cwtemplate.json`. Hashes and per-group source/manual status are recorded in the template.


## Immediate-load extension

The opcode mask `(opcode & 0xC7) == 0x06` now routes all `MVI r/(HL)` encodings through the shared immediate fetch state. Register destinations update after the immediate byte handshake. `MVI M` then holds a write request to HL with the captured immediate until `mem_ready`. Seven MVI encodings have source dispatch entries; `MVI D` (`0x16`) is a manual extension. The directed testbench exercises MVI D and MVI M, including its fifth acknowledged memory write.

Current files are `artifacts/8080/rtl/cw8080_subset_cpu_mov_incdec_alu_mvi_mem.sv`, `artifacts/8080/tests/tb_cw8080_subset_cpu_mov_incdec_alu_mvi_mem.sv`, and `artifacts/8080/hardware-model-8080-bridge-derived-mvi-mem-v2.cwtemplate.json`. The latest HDL runner log is `artifacts/8080/tests/rtl-validation-alu-mvi-mem-v1/rtl-test.log` and says NOT RUN because Icarus Verilog is unavailable. The template preserves that status and log hash.


## 16-bit register-pair extension

The core now exposes a 16-bit stack pointer and decodes `LXI`, `INX`, `DCX`, and `DAD` across BC, DE, HL, and SP. LXI reads low then high immediate bytes through the memory request/ready interface. INX/DCX wrap without modifying flags. DAD writes HL and carry from a 17-bit sum, preserving the other flags. Source dispatch covers all LXI and DAD encodings, 3/4 INX encodings, and 2/4 DCX encodings; the absent INX SP, DCX DE, and DCX SP mappings are explicitly manual. A separate pair-CPU bench tests pair wrap, immediate byte order, DAD carry, flag preservation, and fetch stalls.


The latest versioned set is `artifacts/8080/rtl/cw8080_subset_cpu_mov_alu_mvi_pairs_16bit.sv`, `artifacts/8080/tests/tb_cw8080_subset_cpu_mov_alu_mvi_pairs_16bit.sv`, `artifacts/8080/tests/tb_cw8080_pair_cpu_16bit.sv`, and `artifacts/8080/hardware-model-8080-bridge-derived-pairs-16bit-v1.cwtemplate.json`. Validation status is recorded in `artifacts/8080/tests/rtl-validation-pairs-16bit-v1/rtl-test.log`; the runner did not run simulation because Icarus Verilog is unavailable.


## Direct branch extension

`JMP` (including the source's `0xCB` alias) and all eight conditional branches now fetch low then high target bytes on the memory handshake. The Jcc decode maps NZ/Z/NC/C/PO/PE/P/M to the 8080 flags; taken branches load PC from the little-endian target and untaken branches continue after the immediate. Branches do not modify flags. A directed bench covers taken JZ, untaken JNZ, and JMP under alternating ready stalls. Calls/returns and stack effects remain unsupported.

Latest branch artifact set: `artifacts/8080/rtl/cw8080_subset_cpu_mov_alu_mvi_pairs_branches.sv`, `artifacts/8080/tests/tb_cw8080_subset_cpu_mov_alu_mvi_pairs_branches.sv`, `artifacts/8080/tests/tb_cw8080_pair_cpu_branches.sv`, `artifacts/8080/tests/tb_cw8080_branch_cpu_branches.sv`, and `artifacts/8080/hardware-model-8080-bridge-derived-pairs-branches-v1.cwtemplate.json`. The runner records all four benches but reports NOT RUN while Icarus Verilog is unavailable.


## CALL/RET/RST stack extension

A stack memory FSM handles CALL, all conditional calls, RET, all conditional returns, RST, PUSH and POP. CALL/RST write the high return byte to `SP-1` then low to `SP-2`; only after both acknowledged writes does SP decrement by two and PC load the target. RET reads low at SP and high at SP+1, then increments SP by two. Conditional untaken calls consume their 16-bit operand without touching the stack; conditional untaken returns continue at the next opcode. The stack benches cover nested calls, untaken conditional call, taken conditional return, RST 5, register pair and PSW transfers, and ready stalls.

## Stack and I/O extension (latest partial revision)

PUSH/POP BC, DE, HL and PSW now use the same acknowledged stack interface. PSW follows the architectural 8080 byte layout (S,Z,0,AC,0,P,1,C); the emulator's `Flags.PSW` exposes its raw internal byte and uses a different declared AC bit, so round-trip equivalence with that class is not claimed. The dedicated bench covers BC byte ordering and PSW flag/A restoration.

IN (`0xDB`) and OUT (`0xD3`) now consume an immediate 8-bit port and use the separate `io_req/io_ready` interface. IN samples `io_rdata` into A; OUT holds A and the port stable until acknowledged. This is an explicit hardware-interface decision: source `Ops.IN_D8` and `Ops.OUT` are TODO stubs and do not specify transferred data. The directed bench checks port and data, PC advancement, and wait stability. The integrated model and both new benches are copied to versioned files in `artifacts/8080`; the matching template marks the RTL partial and the HDL checks NOT RUN when Icarus is absent.

The same revision decodes RLC/RRC/RAL/RAR, CMA, STC and CMC. Rotates update A/CY and preserve SZPA; CMA preserves flags; STC/CMC only update CY. The rotate bench covers chained old-carry/new-carry behavior and flag preservation. Latest versioned outputs are `artifacts/8080/rtl/cw8080_subset_cpu_mov_alu_mvi_stack_pushpop_io_rotate_v2.sv`, `artifacts/8080/tests/tb_cw8080_rotate_cpu_stack_pushpop_io_rotate_v2.sv`, and `artifacts/8080/hardware-model-8080-bridge-derived-stack-pushpop-io-rotate-v2.cwtemplate.json`. Runner status remains NOT RUN because no supported HDL simulator is installed.

All eight immediate ALU encodings are now decoded: ADI, ACI, SUI, SBI, ANI, XRI, ORI and CPI. They fetch their operand through the memory handshake and share the register ALU datapath; CPI leaves A unchanged. The emulator source has flag differences for ADI/SUI/SBI and ANA-family operations, called out in the template. The eight NOP aliases present in the dispatch table are also decoded. A new bench covers the full immediate ALU sequence. The latest aggregate template is `artifacts/8080/hardware-model-8080-bridge-derived-imm-alu-v2.cwtemplate.json`; its runner has nine benches registered but reports NOT RUN because Icarus Verilog is unavailable.

The memory-address FSM now also handles STAX/LDAX through BC/DE, STA/LDA at a 16-bit immediate address, SHLD/LHLD as two acknowledged byte transfers in little-endian order, XTHL through stack reads and writes without changing SP, XCHG, PCHL, and SPHL. The addressed-memory bench checks those transfers with alternating ready stalls.

DAA (`0x27`) was added as a manual hardware extension because it is absent from the emulator dispatch. It follows the two-step decimal correction in the [Intel 8080 Assembly Language Programming Manual Rev C](https://mark-ogden.uk/files/intel/publications/98-004C%208080%20Assembly%20Language%20Programming%20Manual-Jan76.pdf): adjust low nibble using AC, then high nibble using adjusted A and CY; AC reflects low correction carry, CY is set on high correction carry and otherwise retained, and SZP are recomputed. The DAA bench includes `9B -> 01` with CY/AC set and an incoming-CY/no-new-high-carry case. The latest aggregate template is `artifacts/8080/hardware-model-8080-bridge-derived-daa-v5.cwtemplate.json`; all 166 source-dispatch entries appear in its instruction descriptors, plus manual DAA/SPHL mappings. Export now refuses to write if any source dispatch opcode lacks a descriptor. It registers eleven benches. RTL validation remains NOT RUN because Icarus Verilog is unavailable.
