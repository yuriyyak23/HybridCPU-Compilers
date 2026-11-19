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

Observed source divergences include `AOR` computing AND (and being called by ORI), source XOR/ORA_M deriving CY from result bit 0 through `CalcSZPC(byte)`, and XOR/ORA_M preserving AC; earlier INR/DCR helper issues also remain. The RTL follows the selected architectural behavior, so source-model equivalence is not implied. The integrated testbench exercises SUB/SBB, ANA/XRA/ORA, CMP M and the previous MOV/INR/DCR/HALT/IRQ cases. Current candidate-specific Icarus x86 runs are tracked separately in `artifacts/8080/RTL_TRACEABILITY_20261118.md`; the adapter benches themselves remain unqualified.

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

## Current candidate-side smoke evidence (2026-11-19)

The Windows `Test-Intel8080Rtl.ps1` adapter benches above still have status NOT RUN when `iverilog` is not on Windows PATH; they are separate from the runner candidate. The x86 golden harness invokes Icarus through WSL on `artifacts/8080/audit-20261116-codex-01/cw8080_subset_cpu_irq_daa_candidate.sv`. Its current 22-program corpus passed, including two architectural DCR auxiliary-carry boundary cases (`10h -> 0Fh` sets AC; `11h -> 10h` clears AC) and a flag/branch case covering INR/DCR carry preservation and JC/JNC/JNZ/JZ. This exposed and corrected a stale DCR predicate in that candidate snapshot; the editable bridge RTL already had the selected low-nibble-equals-zero predicate. Rev C marks AC affected by DCR but does not spell out its truth table; the candidate's rule is pinned by explicit tests. The run is `artifacts/8080/golden-runs/20261119-002013-025782/summary.log`, with model/source divergences retained in the audit and template. These directed checks do not qualify the legacy adapter benches, prove full instruction coverage, or validate cycle timing.

## DAD source projection and candidate check (2026-11-19)

`BuildDadBehaviorProjection` now pins the four source dispatch wrappers, `Registers.DAD`, `Flags.CalcCarryFlag(int)`, and their source hashes. The source projection records the 17-bit widened sum, low-16 HL result, CY bit, preserved S/Z/P/AC, and PC+1; source `return 10` is explicitly emulator cycles, not a hardware schedule. CircuitWeave admitted/lowered only `zext(HL)+zext(operand)` as one `i17` MLIR output at `artifacts/8080/dad-circuitweave-sum17-20261119/`. A direct 8080 assembly test exercised DAD B/D/H/SP, carry/no-carry results, retained flags and SHLD memory writes on the runner candidate; log: `artifacts/8080/runs/8080-run-20261119-003525-192155/simulation.log`. This is a directed partial-candidate check; it does not establish external CIRCT SystemVerilog conversion, whole-CPU verification, or cycle timing.

DAA (`0x27`) was added as a manual hardware extension because it is absent from the emulator dispatch. It follows the two-step decimal correction in the [Intel 8080 Assembly Language Programming Manual Rev C](https://mark-ogden.uk/files/intel/publications/98-004C%208080%20Assembly%20Language%20Programming%20Manual-Jan76.pdf): adjust low nibble using AC, then high nibble using adjusted A and CY; AC reflects low correction carry, CY is set on high correction carry and otherwise retained, and SZP are recomputed. The DAA bench includes `9B -> 01` with CY/AC set and an incoming-CY/no-new-high-carry case. The latest aggregate template is `artifacts/8080/hardware-model-8080-bridge-derived-daa-v5.cwtemplate.json`; all 166 source-dispatch entries appear in its instruction descriptors, plus manual DAA/SPHL mappings. Export now refuses to write if any source dispatch opcode lacks a descriptor. It registers eleven benches. RTL validation remains NOT RUN because Icarus Verilog is unavailable.

## Current candidate-side verification update (2026-11-18)

The `NOT RUN` notes above describe the adapter's older versioned RTL copies and the 2026-11-14 environment. A separate manually revised partial CPU candidate is now tested with Icarus under WSL; see `CircuitWeave-main/artifacts/8080/RTL_TRACEABILITY_20261118.md` and its per-run logs. The adapter copy and original template are not silently promoted by those candidate tests.

A narrow source-backed ADD projection is also available at `CircuitWeave-main/artifacts/8080/bridge-add-projection-20261118/`. CircuitWeave emits MLIR for the 9-bit sum only; the multi-output ADD block is a separate manual-schema SystemVerilog candidate and has an exhaustive 65,536-vector test. This is not a full generated or verified processor.

The ADD source projection is pinned to reviewed SHA-256 snapshots for `Ops.cs`, `Flags.cs`, and `Utils.cs`; any source edit blocks export until the equations are reviewed again. The x86 → 8080 → reference → RTL runner also passed an explicit `7Fh + 01h` smoke at `CircuitWeave-main/artifacts/8080/runs/x86-add-szpc-smoke-20261118/`; it is subset validation, not an independent conformance test.

The analogous ADC slice is under `CircuitWeave-main/artifacts/8080/bridge-add-adc-projection-20261118/`: source-pinned C# projection, CircuitWeave MLIR for `A + operand + CY_in` only, and separate manual-schema multi-output SV with a 131,072-vector exhaustive Icarus test. The x86 corpus exercises the available `ADC AL,BL` dispatch with carry-in clear/set. ADC opcodes `8Ah` and `8Fh` remain absent from the emulator's dispatch.

The SBB projection in `CircuitWeave-main/artifacts/8080/bridge-arithmetic-projections-sbb-20261118/` keeps the C# AC equation separate from Intel-spec RTL AC. C# omits CY-in when it computes AC; for `11h-01h-1`, source AC is 1 while RTL/spec AC is 0. CircuitWeave emits only the wrapped 16-bit subtract result; the manual-schema SV exhaustive test checks both AC interpretations.

## DAD evidence refresh (2026-11-19)

Use artifacts/8080/hardware-model-8080-evidence-dad-v7-20261119.cwtemplate.json as the newest aggregate editable snapshot. Its seven candidate evidence entries all report PASS, including the 22-case corpus and the new DAD run; the separate adapter HDL runner is still NOT RUN.

Latest direct candidate run: `artifacts/8080/runs/8080-run-20261119-003805-175702/`. The simulator differential compared modeled state and the complete 64 KiB memory; `image-byte-check.log` confirms the test image contains DAD B/D/H/SP at the asserted addresses. CircuitWeave emitted only the scalar i17 sum MLIR; full CPU RTL was not generated. See `artifacts/8080/dad-projection-validation-20261119.md`.

## x86 frontend/runner update (2026-11-19)

The latest committed smoke corpus has 24 programs: `artifacts/8080/golden-runs/20261119-004537-891806/summary.log`. New cases execute a single x86 `CALL`/`RET` subroutine through assembler, reference and RTL and test BL/CL absolute memory load/store lowered through HL. CALL checks return address bytes `CD 0E 00`, RET `C9`, result, `SP=0100h`, and the two stack bytes. Non-AL memory test asserts `LXI H` and `MOV M,r`/`MOV r,M` machine bytes. The translator treats H/L as internal scratch and does not promise they retain 8080 values.

## Latest aggregate snapshot (2026-11-19)

The newest editable aggregate is `artifacts/8080/hardware-model-8080-evidence-call-ret-v8-20261119.cwtemplate.json`. It records 166/166 dispatch descriptors and eight PASS candidate log entries, including the 24-case x86-to-8080 corpus at `artifacts/8080/golden-runs/20261119-004537-891806/summary.log`. The CALL/RET and non-AL absolute memory tests exercise an existing manual candidate through the frontend, assembler, partial reference and RTL. This does not change the adapter runner's NOT RUN status or claim a complete verified processor.

## Nested call runner evidence (2026-11-19)

The newest runner corpus now has 25 passing x86-shaped programs, including simple and nested CALL/RET. Nested coverage asserts each saved return address at `SP-4..SP-1`, restored `SP=0100h`, result A and memory, plus assembled CALL/RET bytes. See `artifacts/8080/golden-runs/20261119-005051-841369/summary.log` and aggregate template `artifacts/8080/hardware-model-8080-evidence-nested-call-v9-20261119.cwtemplate.json`. This remains a partial RTL candidate with a partial hand-written reference oracle.

## DAA exhaustive check (2026-11-19)

The bridge RTL and the separate x86-runner candidate each passed all 1024 A/AC-in/CY-in combinations using an architectural `POP PSW; DAA` path and alternating ready. This uncovered and fixed the runner candidate's original-A high-adjust shortcut for invalid BCD values; Rev C examines the high nibble after low correction. The failure, correction, rule and non-conformance limits are documented at `artifacts/8080/daa-exhaustive-validation-20261119.md`. Bridge/artifact RTL SHA-256 equality is recorded in `artifacts/8080/daa-exhaustive-bridge-run-20261119/rtl-hash-check.log`.

Latest editable template: `artifacts/8080/hardware-model-8080-evidence-daa-exhaustive-v10-20261119.cwtemplate.json`. Latest 25-case x86 corpus: `artifacts/8080/golden-runs/20261119-010031-541835/summary.log`. Neither exhaustive DAA vectors nor that corpus establish full CPU conformance or cycle timing.

## x86 DAA integration update (2026-11-19)

`DAA` and `STC` now pass through the bounded x86 frontend; DAA maps to 27h and STC to 37h. Four golden programs test packed-decimal boundaries and incoming carry. The latest corpus has 29 PASS programs at `artifacts/8080/golden-runs/20261119-143523-873163/summary.log`; the aggregate template is `artifacts/8080/hardware-model-8080-evidence-x86-daa-v11-20261119.cwtemplate.json`. DAA translation selects Intel 8080 Rev C semantics; x86 behavior for arbitrary invalid BCD values may differ. Complete CPU equivalence and timing are not implied.

## NOT and carry-control frontend coverage (2026-11-19)

The x86 subset adds `NOT AL`→CMA, `CLC`→STC+CMC, and direct `CMC`. The test keeps stale Z/P flags through NOT and confirms carry-only behavior through CLC/CMC. Latest 30-case corpus: `artifacts/8080/golden-runs/20261119-144129-023534/summary.log`; test detail: `artifacts/8080/x86-not-flags-validation-20261119.md`; latest template: `artifacts/8080/hardware-model-8080-evidence-x86-not-flags-v12-20261119.cwtemplate.json`.

## x86 TEST smoke coverage (2026-11-19)

The x86 runner supports the restricted `TEST AL,imm8/r8` forms by saving A in hidden L, executing ANI/ANA, and restoring A. The 33-program corpus passes against the partial reference and RTL candidate; see `artifacts/8080/golden-runs/20261119-144658-350519/summary.log`. Three cases assert zero/sign/register outcomes and bytes. x86 AF is undefined; 8080 AC is checked as 8080 behavior. No general TEST mapping or full-core claim is made.

## x86 CMP frontend extension (2026-11-19)

Non-AL compare forms are lowered by saving AL in hidden L and executing CMP/CPI with the selected register as accumulator. The latest four CMP-specific cases are included in the 37-case golden corpus. Unsigned/equality branch behavior is asserted; x86 AF/8080 AC equivalence and OF-based signed conditions are not claimed.

The x86-to-RTL candidate corpus now has 38 passing programs (artifacts/8080/golden-runs/20261119-150812-735796/summary.log), including CMP BL,AL preservation/encoding. This remains a partial manually authored candidate and partial-reference comparison, not full-core validation.


## WSL-backed bridge RTL runner update (2026-11-19)

`Test-Intel8080Rtl.ps1` now accepts `-Simulator wsl-iverilog` and uses WSL Icarus/VVP while keeping compiled programs and logs under the selected `artifacts/8080` output directory. A malformed SystemVerilog loop literal in the ADD/ADC bench was corrected. The integrated subset bench also had a bad test program: it loaded `01h` into D but executed `SUB B` using A; the program now loads A explicitly before SUB and its subsequent PC checks were shifted. The full eleven-bench run passed at `artifacts/8080/tests/rtl-validation-wsl-v5-20261119/rtl-test.log`. This suite validates the editable bridge RTL; it is separate from the x86 runner candidate.

## Source dispatch versus manual RAL

The template classifies RLC/RRC/RAR as source-dispatched and RAL (`17h`) as manual/spec-only. Current `Cpu._ops` has no reachable RAL dispatch and `Ops.RAL` is absent, although RTL implements the documented instruction.

## Current bridge RTL validation (2026-11-19)

The WSL-enabled runner compiled and executed all eleven benches with Icarus 12.0; see `artifacts/8080/tests/rtl-validation-wsl-v5-20261119/rtl-test.log` and scope/bench corrections in `artifacts/8080/bridge-rtl-wsl-validation-20261119.md`. The integrated test suite passes; its ALU bench reports 262,144 exhaustive vectors, while DAA has five directed cases here and the separate 1,024-vector bridge DAA sweep is retained in `artifacts/8080/daa-exhaustive-bridge-run-20261119/simulation.log`. This is still partial RTL verification.

Latest aggregate editable snapshot: `artifacts/8080/hardware-model-8080-bridge-derived-x86-cmp-wsl-rtl-v16-20261119.cwtemplate.json`. It records RTL runner PASS and corrects RAL (`17h`) to manual/spec-only because it has no reachable source dispatch or source method.
Run the current bridge suite with a fresh artifact path:

```powershell
.\Test-Intel8080Rtl.ps1 -Simulator wsl-iverilog -OutputDirectory C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\tests\rtl-validation-<unique-name>
```

The runner refuses an existing output directory and output paths outside `artifacts/8080`.

### SUB/SBB/CMP auxiliary carry

The Intel-manual carry-out interpretation is now implemented for SUB/SBB/CMP: ALU nibble subtraction's bit 4 is borrow, so AC receives its inverse. The updated integrated SUB/SBB/CMP checks and CPI test pass in the full eleven-bench Icarus/WSL run at rtifacts/8080/tests/rtl-validation-ac-sub-fix-rerun-20261119/rtl-test.log. This is partial behavior coverage; the independent x86 status mapping still must exclude AF equivalence for subtract.
