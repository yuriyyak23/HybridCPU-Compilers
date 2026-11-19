# HardwareBridge to HardwareBridge_intel8080 transfer audit

## Applicable capabilities represented in this bridge

- Roslyn source and semantic inventory for the seven `emu8080.Core` files, including source coordinates, dependencies and observed opcode dispatch entries: `Intel8080/Program.cs`, `Model.cs`, `Invoke-Intel8080Analysis.ps1`.
- MSBuild project inventory and project metadata capture without building or executing the imported emulator: `ProjectInventory/Program.cs`, `Intel8080/Invoke-Intel8080ProjectInventory.ps1`.
- Shared source snapshot and bounded sidecar codec: `SourceSnapshot/BridgeSourceSnapshotV1.cs`, `BridgeSidecarCodecV1.cs`.
- Root `Directory.Build.props` confines each target project’s intermediate and binary outputs to `HardwareBridge_intel8080/.artifacts/{obj,bin}/<ProjectName>`. `Directory.Build.targets` excludes old local `obj`/`bin` trees and the custom `.artifacts` tree from default C# compilation, preventing generated assembly attributes from being compiled twice.
- Semantic inventory, isolated evaluated-project-graph and import-inventory utilities under `SemanticInventory/` and `HWIR/`; these are provenance/audit utilities and do not lower C# to hardware.
- Editable partial hardware model export, manually authored SystemVerilog, handshake testbenches, source maps and artifact-path/hash safeguards under `Intel8080/`.

The current target tree was inventoried against the read-only `HardwareBridge` tree, excluding `.git`, `bin`, `obj` and `.artifacts`. Twenty-one common analysis/snapshot files are byte-identical; `SemanticInventory/README.md` differs. The source root also provides `Directory.Build.props`, adapted into this target; 8080-specific analysis, RTL and tests remain target-owned. The other absent source files belong to Companion/CIL projection, pinned scalar AddWord HWIR candidates, HybridCPU scheduler/admission/power/CSR models, or their golden/test fixtures, not a generic processor importer. The newer vector-pod CSR branch was separately inspected and is excluded because it models HybridCPU-specific state.

Build check (2026-11-14): all seven target `.csproj` files build successfully with the installed .NET 11 SDK. Logs are retained under `.artifacts/build-logs/transfer-audit-v4/`. No build was run for the original `HardwareBridge` or imported emulator project.

`Intel8080/Build-Bridge.ps1` now repeats that target-only build in a fresh, uniquely named log directory under `.artifacts/build-logs/bridge-<UTC timestamp>/`; it validates every project path is inside the target root and never builds the imported emulator or source bridge. Executed successfully for all seven projects on 2026-11-14.

The target analysis and export scripts were also run end to end against the read-only emulator sources on 2026-11-14. Fresh outputs are `artifacts/8080/bridge-transfer-audit-20261114/` (selected files, source inventory, opcode/method IR candidates and report), `artifacts/8080/project-inventory-transfer-audit-20261114.json` and `artifacts/8080/hardware-model-8080-transfer-audit-20261114.cwtemplate.json`. The new model digest matches the generated inventory digest, covers 166/166 observed dispatch entries, and expressly reports partial/unverified RTL, `NOT RUN` simulation and no qualified MLIR lowering. Analysis reported seven files/types and 174 Ops methods; it did not build or execute the imported source project.

The original generic `Tests/SemanticInventory.Tests.ps1` was adapted as `SemanticInventory/Test-SemanticInventory.ps1`: binary and fixture paths now point to this target and `artifacts/8080/tests/`. It passed the partial-type/member-ownership/dependency, project-reference, selected-generator/replay, stale-input, and output-boundary cases on 2026-11-14. Logs and fixtures remain under `artifacts/8080/tests/semantic-inventory-<guid>/`; the imported emulator project is not built or executed.

Tool availability rechecked on 2026-11-14: `iverilog`, `vvp`, Verilator, slang, Yosys, svlint, CIRCT `circt-opt`, `mlir-opt` and `firtool` are unavailable on PATH. Consequently no SystemVerilog/MLIR parser or simulator result is claimed; source-to-template analysis and the C# bridge tests are the checks that did run.

## Deliberately not imported as 8080 functionality

- `HardwareBridge.Companion.csproj`, `CompilerFacts/CompanionBridgeProjectionV1.cs`, `CompilerFacts/BridgeCompilerFactsV1.cs`, `CompilerFacts/README.md`, `Docs/*`, `GoldenOracle/*` and `Tests/BridgeSelfTests.cs` belong to an opt-in software PE/PDB/CIL body-world bridge and the HybridCPU scalar AddWord golden oracle. The shared source snapshot and bounded JSON sidecar codec are already in this target. The Companion facts types do not parse the 8080 source or add hardware lowering; adding them would expose an unconnected, software-only schema.
- `HWIR/BridgeHwirAddWordCompiler.cs` and its neighboring `BridgeHwir*` lowering components require CircuitWeave's pinned `GoldenAddWordCandidateCompiler`, HybridCPU bindings and scalar `AddWord`/`SubWord`/`MultiplyWord` contracts. The source README explicitly limits the path to those candidates. It is not a CPU instruction or processor HWIR backend.
- `CompilerFacts/CompanionBridgeProjectionV1.cs` depends on HybridCPU Companion/CIL types and emitted PE/PDB body-world results. Reusing it would add a different compilation/import route and would not establish 8080 semantics.
- The subsequently present `HWIR/BridgeHwirVectorPodCsr.cs` and `Tests/VectorPodCsr/` are pinned to HybridCPU's VexcpMask/VexcpPri CSR addresses (`0x900/0x901`), source hashes and HybridCPU revisions. They model a vector-pod exception-mask/priority plane with no Intel 8080 architectural counterpart, clock/reset/arbitration behavior explicitly absent. Importing that feature into the 8080 bridge would assert unrelated state and CSR semantics, so it remains excluded after inspection.
- `Tests/BridgeSelfTests.cs`, golden oracle files, and sample bindings exercise the HybridCPU scalar candidate and its admission policy, not the Intel 8080 emulator.
- `HWIR/CompareProjectReferenceProperties.ps1` consumes the original bridge's capture/graph schemas, expects its specific Debug/test-hook property set and contains fixed HybridCPU checkout paths. Its bounded comparison job does not add 8080 source facts; the target already has an isolated evaluated-project-graph and import-inventory path. It is therefore not copied as-is or represented as a portable generic evaluator.

These components remain useful as references for future bounded candidate admission and provenance, but are not copied or represented as general C#-to-MLIR/SystemVerilog support. The 8080 RTL remains manually authored and partial; no qualified C#-to-MLIR lowering is available here.

## Current Intel 8080 extension

The integrated RTL now covers MOV `0x40-0x7F` except HLT, eight NOP aliases, INR/DCR register and `(HL)` families, the full register/(HL) ALU range `0x80-0xBF`, all eight MVI destinations, all eight immediate ALU instructions, STAX/LDAX via BC/DE, STA/LDA direct address, SHLD/LHLD word transfers, XTHL/XCHG/PCHL/SPHL, LXI/INX/DCX/DAD over BC/DE/HL/SP, JMP/Jcc, CALL/CC/RET/RC/RST, PUSH/POP BC/DE/HL/PSW, IN/OUT using the explicit port ready interface, RLC/RRC/RAL/RAR, CMA/DAA, STC/CMC, HLT, DI and EI, plus the agreed memory handshake and basic IRQ contract. Source-derived model metadata distinguishes dispatch-backed opcodes from manual additions and flags divergences. All 166 observed source dispatch opcode entries are represented in instruction descriptors; this is a mapping audit, not functional verification. DAA and SPHL are manual additions absent from the emulator. Export fails if a source opcode lacks a descriptor. The latest versioned model is `artifacts/8080/hardware-model-8080-bridge-derived-daa-v5.cwtemplate.json`.

IN/OUT payload behavior is a hardware decision because source Ops methods are TODO. PSW packing is architectural and differs from the emulator's raw `Flags.PSW` storage. Immediate arithmetic flag behavior also differs in the source for AC updates. Residual unsupported behavior and verification limits are listed in `implementedSubset.unsupported` and `validation`. At the time of this 2026-11-14 audit the RTL remained partial/unverified and the native Windows runner was `NOT RUN`; the 2026-11-19 WSL suite update below supersedes that runner status only and does not establish a complete processor.

## Bounded source-backed ADD projection update (2026-11-18)

The earlier statement that this bridge emitted no MLIR is superseded for one narrow case only. `Model.cs::BuildAddBehaviorProjection` pins `Ops.cs`, `Flags.cs`, and `Utils.cs` to reviewed SHA-256 snapshots, then applies Roslyn text-pattern checks to `Ops.ADD`, its `CalcSZPC`/`CalcAuxCarryFlag`/parity helper closure and only source-dispatched opcodes 80h/81h/82h/87h. It verifies all 256 parity table entries and writes `add-behavior-projection.json` from `Invoke-Intel8080Analysis.ps1`. Hash changes fail closed pending review; the patterns are not a control-flow equivalence proof.

`AddMlirExport` constructs a typed, single-output CircuitWeave HWIR module for the 9-bit widened sum and lowers it with `ScalarCirctMlirLowerer`. This does not include flags, PC/FSM sequencing, memory, opcode decode, or any other instruction. `emit_add_projection_sv.py` emits a separate multi-output combinational ADD candidate from a strict schema; it is not CircuitWeave-generated RTL. The current scalar backend permits one output, and firtool/circt-opt/mlir-opt were unavailable in WSL, so SV was not emitted from MLIR.

## Bounded source-backed ADC projection update (2026-11-18)

`BuildAdcBehaviorProjection` extends this proof-of-concept to `A + operand + CY_in`, including the carry-in contribution to auxiliary carry, SZP/CY, PC+1, and parity-helper source traces. It pins the reviewed `Ops.cs`/`Flags.cs`/`Utils.cs` hashes and validates six actual C# dispatch entries (`88h`, `89h`, `8Bh`-`8Eh`). The source dispatch omits `8Ah ADC D` and `8Fh ADC A`; these are not attributed to the emulator even though the RTL follows the broader architectural family.

`AdcMlirExport` sends only the widened sum equation through CircuitWeave's scalar HWIR/MLIR path. `emit_adc_projection_sv.py` is a separate candidate-only manual schema emitter; its Icarus bench checks all 131,072 combinations of A/operand/CY and the full PC increment domain. Neither output implements the ADC opcode decoder, memory protocol, or CPU sequencing. See `CircuitWeave-main/artifacts/8080/bridge-add-adc-projection-20261118/` for projection, MLIR, candidate SV, logs, and integration validation.

## Bounded source/spec-split SBB projection update (2026-11-18)

`BuildSbbBehaviorProjection` pins the same reviewed source snapshots, records all seven source-dispatched SBB forms (`99h`-`9Fh`), and preserves the C# helper's wrapped 16-bit subtract result, borrow/CY, SZP, PC+1, and source AC formula. It also records a separate `AC_spec_next` selected for RTL from Intel's two's-complement SBB bit-A3 carry rule. For `A=11h, operand=01h, CY_in=1`, both produce result `0Fh`, but source AC is 1 while spec-selected RTL AC is 0 because the source helper omits CY-in from its AC calculation. C# does not dispatch `98h SBB B`; RTL's general group mask expands that encoding.

`SbbMlirExport` lowers only the source-equivalent 16-bit wrapped subtraction result via CircuitWeave scalar HWIR/MLIR. `emit_sbb_projection_sv.py` separately emits a candidate-only manual-schema SV slice with both source AC and spec AC outputs; Icarus exhausts all 131,072 A/operand/CY combinations and the 16-bit PC domain. This does not lower the full instruction or CPU. Validation artifacts are in `CircuitWeave-main/artifacts/8080/bridge-arithmetic-projections-sbb-20261118/`.

Validation and source trace: `CircuitWeave-main/artifacts/8080/bridge-add-projection-20261118/source-add-projection-validation-20261118.md`. The new exhaustive Icarus test passed 65,536 A/operand pairs and the complete 16-bit PC space. No source emulator project was built or executed. Full ADD instruction integration, machine-cycle correspondence, RTL synthesis/formal proof, and CPU equivalence remain unproven.

## Bounded source/spec-split SUB/SUI projection update (2026-11-18)

`BuildSubBehaviorProjection` source-pins the reviewed `Ops.cs`/`Flags.cs`/`Utils.cs` snapshots and records `SUB B` (`90h`) plus immediate `SUI` (`D6h`). The source `SUB_B` helper computes result, CY and SZP and writes AC using the no-borrow nibble equation; its immediate `SUI` handler computes result/CY/SZP but does not write AC, preserving its previous value. The architecture-selected RTL recomputes AC for both forms. At `A=11h, operand=01h, AC_prev=0`, SUI source effect is AC=0 while the selected 8080 AC equation yields 1. Source trace/equations are in `artifacts/8080/bridge-arithmetic-projections-sub-20261118/sub-behavior-projection.json`.

`SubMlirExport` lowers only the shared wrapped 16-bit subtract result through CircuitWeave scalar HWIR/MLIR. `emit_sub_projection_sv.py` emits a separate source-hash-checked manual-schema candidate for result, SZP, borrow, source/spec AC and handler PC deltas. Icarus exhausted 262,144 operand/mode/prior-AC combinations. The x86 golden corpus adds `SUB AL,BL` and `SUB AL,01h`; for the SUI source/spec mismatch it excludes only reference AC and retains an explicit `AC=1` assertion plus full comparison of remaining architectural state and all memory. These checks do not establish instruction-cycle timing or complete CPU conformance.

## Bounded source/spec-split logical projection update (2026-11-18)

`BuildLogicalBehaviorProjection` pins Ops.cs/Flags.cs/Utils.cs and binds nine source dispatch entries (`A0`, `A7`, `AA`, `AF`, `B0`, `B6`, `E6`, `EE`, `F6`), including immediate `EE XRI` and its wrapper PC effects. It preserves the actual handler equations and helper calls: `AOR` computes AND and is called by `ORI_D`; `XOR`, `XRI`, and `ORA_M` call `CalcSZPC(byte)`, whose byte `CalcCarryFlag` maps CY to result bit 0 and leaves their AC untouched; `ORA_B` clears both flags. Architectural logical operations are recorded separately. See `artifacts/8080/bridge-logical-projections-xri-20261119/logical-projection-validation-xri-20261119.md`.

CircuitWeave admits and lowers isolated 8-bit AND/OR/XOR result slices in `artifacts/8080/logical-circuitweave-xri-20261119/`. Separate manual-schema source/spec SV passed 786,432 Icarus vectors. The 22-case x86 RTL corpus includes discriminating ORI (`F6`), XRI (`EE`), DCR AC boundaries, and a combined INC/DCR/branch case, checks their emitted bytes, and asserts selected result/flags. ORI and DCR AC explicitly exclude only source-divergent fields from the partial C# reference comparison; explicit architectural expectations remain active. None of these checks is full-CPU equivalence or cycle-level validation. Latest corpus: `artifacts/8080/golden-runs/20261119-002013-025782/summary.log`.

## Candidate RTL DCR auxiliary-carry correction (2026-11-19)

Independent review found that the x86 runner's candidate snapshot used the inverse DCR AC predicate. The specialized bridge RTL already used the selected 8080 nibble-borrow rule (old low nibble equals zero); the runner snapshot was corrected to match. Two x86-to-RTL cases now exercise `10h -> 0Fh` (AC=1) and `11h -> 10h` (AC=0), and assert DCR A opcode `3Dh`. Both exclude AC only from the partial source-reference comparison because `Utils.Decrement` leaves AC unchanged; explicit architectural expectations remain active. Both focused post-fix runs and the full corpus passed. The Intel Rev C manual states that DCR affects AC but does not provide a truth table; the two explicit boundary assertions pin the selected rule, and silicon-level confirmation remains open. The full candidate remains partial and cycle timing/IRQ semantics are not established.

The previously unregistered `demo_x86_flags_branch.asm` case also now passes without any reference-field exclusions. It checks INR/DCR carry preservation, JC/JNC after arithmetic, CPI/JNZ/JZ behavior, final A/B and all five flags. The `CMP 0,0` result has AC=1, matching Intel Rev C's documented SUB self example (carry from bit A3); assembled INC/DCR/branch opcodes are asserted in the image. Current template: `artifacts/8080/hardware-model-8080-evidence-dcr-v5-20261119.cwtemplate.json`.

## Source-pinned DAD projection and scalar CircuitWeave lowering (2026-11-19)

`BuildDadBehaviorProjection` source-pins `Ops.cs`, `Registers.cs`, `Flags.cs` and `Cpu.cs`, checks DAD B/D/H/SP dispatch (`09h/19h/29h/39h`), their shared `Registers.DAD` call, carry helper, and exact state writes. It records `sum17 = zero_extend_1(HL) + zero_extend_1(operand)`, `HL_next=sum17[15:0]`, `CY_next=sum17[16]`, S/Z/P/AC preservation and `PC+1`; the C# wrapper's 10-cycle return is kept as emulator metadata. CircuitWeave accepts and lowers only the single i17 sum output to MLIR at `artifacts/8080/dad-circuitweave-sum17-20261119/`; no external firtool/CIRCT conversion is claimed. The direct assembly regression exercises all four DAD opcodes, `FFFF+1`, `8000+8000`, `1234+1111`, flag preservation and SHLD output against the partial Python reference and candidate RTL: `artifacts/8080/runs/8080-run-20261119-003525-192155/`. This does not prove full CPU equivalence or cycle timing. The current editable template is `artifacts/8080/hardware-model-8080-evidence-dad-v6-20261119.cwtemplate.json`.

## DAD evidence refresh (2026-11-19)

The latest immutable aggregate template is artifacts/8080/hardware-model-8080-evidence-dad-v7-20261119.cwtemplate.json (166/166 dispatch descriptors, six source-pinned projections, eight scalar MLIR artifacts). Export checks are recorded in artifacts/8080/template-export-validation-dad-v7-20261119.log.

The newer direct candidate run is `artifacts/8080/runs/8080-run-20261119-003805-175702/`: state differential passed for modeled registers, flags, and all 65536 memory bytes; `image-byte-check.log` separately asserts assembled DAD B/D/H/SP opcodes. This remains a directed test of the manually authored partial candidate and Python reference, not a full 8080 conformance result. Details: `artifacts/8080/dad-projection-validation-20261119.md`.

## x86 end-to-end corpus extension (2026-11-19)

The x86 smoke path now reaches the candidate's existing CALL/RET stack engine: the translator passes `CALL label`/`RET`, the assembler encodes `CD lo hi`/`C9`, and the partial reference pushes the post-call PC high byte at `SP-1` then low byte at `SP-2`, with matching RET reads. A directed subroutine test checks final A/B, restored SP, stored result, both stack bytes and assembled opcode bytes.

Absolute x86 `MOV` from/to `[address]` is now accepted for mapped AL/BL/CL/DL. AL uses direct LDA/STA; BL/CL/DL use HL as a scratch pointer via LXI H and MOV with M. This clobbers 8080 H/L, which are not part of the translator's exposed x86 register mapping. The full 24-program corpus passed against candidate RTL and the partial Python reference, with explicit assertions and full modeled-state/64 KiB-memory comparison. Run evidence: `artifacts/8080/golden-runs/20261119-004537-891806/summary.log`. This exercises one non-nested call and selected memory forms; it does not establish general x86 calling convention or all stack boundary cases.

## Latest x86 integration evidence snapshot (2026-11-19)

The newest aggregate template is `artifacts/8080/hardware-model-8080-evidence-call-ret-v8-20261119.cwtemplate.json`, validated in `artifacts/8080/template-export-validation-call-ret-v8-20261119.log`. It retains 166/166 source-dispatch descriptor mapping and eight candidate-evidence entries, including both the prior corpus and the 24-case run with CALL/RET and non-AL absolute memory operations. The new instruction paths are manual frontend/assembler/reference additions over the existing partial RTL; they are not C#-derived hardware implementation.

The same 24-case run is in `artifacts/8080/golden-runs/20261119-004537-891806/summary.log`. Focused Python, PowerShell parse and whitespace checks are in `artifacts/8080/verification-x86-call-ret-20261119.log`. The CALL test is one non-nested subroutine starting from SP=0100h; nested calls, stack wraparound, arbitrary x86 ABI behavior, and independent reference validation remain unproven.

## Nested CALL/RET corpus update (2026-11-19)

The latest runner corpus is 25/25 PASS at `artifacts/8080/golden-runs/20261119-005051-841369/summary.log`. It adds a nested two-level CALL/RET case that asserts both saved return words, final SP restoration, A and stored result, plus machine-byte checks for both calls and both returns. Aggregate editable evidence is `artifacts/8080/hardware-model-8080-evidence-nested-call-v9-20261119.cwtemplate.json`, validated at `artifacts/8080/template-export-validation-nested-call-v9-20261119.log`. Stack wraparound and broader stack/ABI conformance are still untested.

## Exhaustive DAA verification update (2026-11-19)

A 1024-vector DAA test through `POP PSW; DAA` exposed a mismatch in the standalone runner candidate on invalid BCD `FAh`: the prior high-correction shortcut inspected original A rather than the accumulator after step 1. The candidate now follows the Rev C two-step rule. Both corrected candidate and editable bridge RTL passed all 256 A values × 2 incoming AC × 2 incoming CY, including flag outputs and ready stalls. The existing v7 template copy's SHA-256 matches the bridge RTL. Full finding, oracle and limitations: `artifacts/8080/daa-exhaustive-validation-20261119.md`; raw logs under `artifacts/8080/daa-exhaustive-run-20261119-v2/` and `artifacts/8080/daa-exhaustive-bridge-run-20261119/`. The current 25-case x86 corpus was rerun after the candidate fix and passed at `artifacts/8080/golden-runs/20261119-010031-541835/summary.log`.

The latest editable aggregate is `artifacts/8080/hardware-model-8080-evidence-daa-exhaustive-v10-20261119.cwtemplate.json`, whose 13 candidate-evidence entries all pass; export validation is `artifacts/8080/template-export-validation-daa-exhaustive-v10-20261119.log`. DAA remains a manual extension absent from the C# dispatch.

## x86 DAA path and latest corpus (2026-11-19)

The bounded x86 frontend now accepts `DAA` and `STC`; the 8080 assembler emits 27h and 37h; the partial reference implements STC and the tested Rev C DAA two-step rule. Four new decimal-correction programs cover 99+01, 99+99, incoming carry after 10+F0, and STC followed by DAA. All 29 x86-to-8080-to-reference-to-RTL programs passed: `artifacts/8080/golden-runs/20261119-143523-873163/summary.log`. x86 DAA behavior on arbitrary invalid BCD inputs is not claimed identical; the translation contract selects 8080 Rev C semantics.

Latest editable aggregate template: `artifacts/8080/hardware-model-8080-evidence-x86-daa-v11-20261119.cwtemplate.json` (166/166 dispatch descriptors, 14 candidate evidence entries all PASS), validated at `artifacts/8080/template-export-validation-x86-daa-v11-20261119.log`. The actual 8080 DAA block additionally passed all 1024 A/AC/CY states in both candidate and bridge RTL, as documented in `artifacts/8080/daa-exhaustive-validation-20261119.md`. DAA remains absent from C# dispatch and is a manual/spec extension.

## x86 NOT and carry-control integration (2026-11-19)

The frontend accepts `NOT AL`, `CLC`, `CMC`: NOT AL maps to CMA (2Fh) without flag changes; CLC expands as STC then CMC to clear only CY; CMC maps to opcode 3Fh. The 30-program candidate corpus passed with a discriminating flag-staleness case and byte assertions: `artifacts/8080/golden-runs/20261119-144129-023534/summary.log`. Unit/regression details: `artifacts/8080/x86-not-flags-validation-20261119.md`.

Latest aggregate editable template: `artifacts/8080/hardware-model-8080-evidence-x86-not-flags-v12-20261119.cwtemplate.json`; evidence validation: `artifacts/8080/template-export-validation-x86-not-flags-v12-20261119.log`. The subset still rejects NOT on non-AL registers and does not implement NEG.

## x86 TEST frontend evidence (2026-11-19)

The bounded x86 subset added `TEST AL,imm8/r8` lowering via L scratch and 8080 ANI/ANA, with A restoration. Latest RTL runner corpus: `artifacts/8080/golden-runs/20261119-144658-350519/summary.log` (33/33). x86 AF is undefined; 8080 AC semantics are retained and asserted. Details and limits: `artifacts/8080/x86-test-integration-validation-20261119.md`. The aggregate template is v13. This does not add TEST to the source C# opcode dispatch or establish a complete core.

## Bounded x86 CMP register destinations (2026-11-19)

The artifacts frontend now lowers CMP to mapped BL/CL/DL via hidden L, preserving AL and retaining CMP/CPI flags. Four new unsigned/equality branch vectors are in `artifacts/8080/golden-runs/20261119-150500-119723/summary.log`. 8080 AC is not x86 AF on subtraction; OF-dependent signed branches are unsupported. This is a manual toolchain extension, not source-derived opcode coverage.

The latest x86 golden corpus is 38/38 PASS at artifacts/8080/golden-runs/20261119-150812-735796/summary.log, including CMP BL against immediate, another register, and AL via the hidden L save path. Current aggregate snapshot: artifacts/8080/hardware-model-8080-evidence-x86-cmp-al-v15-20261119.cwtemplate.json. RTL syntax remains unchecked and the bridge RTL runner remains NOT RUN.


## Bridge HDL runner and source-map corrections (2026-11-19)

The bridge RTL runner now supports WSL Icarus/VVP through `-Simulator wsl-iverilog`; actual suite output and status will be preserved under `artifacts/8080/tests/`. During first execution, Icarus surfaced an invalid `0x80` literal in the add bench; the integrated CPU bench then exposed its own test-program error, corrected by explicitly loading A before SUB. These findings were testbench defects; the resulting RTL checks still need a clean complete rerun before claiming PASS.

The source map now keeps RAL opcode `17h` out of C# dispatch provenance: the source has no `Cpu._ops` entry or `Ops.RAL` method; RTL RAL is manual Intel 8080-spec coverage.

## Bridge RTL WSL suite PASS (2026-11-19)

`Test-Intel8080Rtl.ps1 -Simulator wsl-iverilog` compiled and executed all 11 benches successfully with Icarus 12.0. Full log: `artifacts/8080/tests/rtl-validation-wsl-v5-20261119/rtl-test.log`; report: `artifacts/8080/bridge-rtl-wsl-validation-20261119.md`. The runner integrates WSL path conversion and stores compile/simulation artifacts under the approved artifact root. v16 template records PASS and the exact log hash.

The successful suite uses corrected SV benches for ALU literal syntax, SUB's A source, POP PSW expected result, and XTHL stack byte expectations. Those corrections fixed erroneous test inputs/assertions and did not change bridge RTL. Bench scope remains directed/partial, not full opcode equivalence. The template now marks RAL 17h manual-only rather than C#-mapped.

The bridge RTL now derives SUB/SBB/CMP AC from the carry-out of the equivalent two's-complement nibble addition (inverting widened subtract bit 4). Updated tests cover 01-FF borrow, SBB borrow-in, CMP flags, and CPI; all eleven bridge benches pass at artifacts/8080/tests/rtl-validation-ac-sub-fix-rerun-20261119/rtl-test.log. The source C# behavior and x86 AF are not thereby made equivalent. Template snapshot v17 records updated RTL/testbench hashes.
