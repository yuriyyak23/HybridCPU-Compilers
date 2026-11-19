# HB-INT-05 — opt-in legacy/Core HWIR migration guard

**State:** partial implementation, **NOT a completed migration**. Owner: HardwareBridge *regression reference only*. Backlog: [HB-INT-05](../../Docs/CircuitWeave_Pipeline_Integration_Plan.md) / [plan PR #4](https://github.com/yuriyyak23/HybridCPU-Compilers/pull/4). Depends on **CW-INT-06** (source-owned generic typed lowering) and **CW-INT-13** (stateful CPU semantics). Both remain **Blocked for migration proof** as of the audited Core master `555672232e46cb4e8b157e2d1b1db1e873c4194e`.

## Delivered scope

- `projection-inventory.v1.json` enumerates **60** actual `BridgeHwir*.cs` files from HybridCPU-Compilers master `94f59223b36659c9162620224f91349ac79fb896`; each has its Bridge Git blob SHA-1 and byte length. This is **not** the source-method/Golden-byte hash. Every unreviewed method's exact identity, vector count, native receipt and generic lowering parity is explicitly unresolved. Four selected projections have conservative reviewed source/shape notes.
- `parity_guard.py` is a bounded **read-only cross-artifact comparison**, not a C# compiler, IR optimizer, simulator, reader for Bridge sidecar, hardware executor, validator of full HWIR, native ToolBridge, evidence authority or producer. It accepts **already-created** canonical `CircuitWeave/HWIR/canonical/v1` `hwir/v0.1` artifacts and two harness-local observations. Both producer names are untrusted labels. The only successful status is `ReportOnly`, and independent RTL acceptance always remains `Blocked`. It does not claim either producer actually ran.
- `../Tests/test_parity_guard.py` contains synthetic **format/mutation tests**. They are NOT Golden C# vectors or independent Core lowering/native simulation. Existing Core/Bridge C# tests remain opt-in and unchanged. No `.csproj`, Golden C#, external schema or consumer switch is modified.

## Selected source-to-semantic-rule migration matrix

| Legacy entry / exact witness | Candidate behaviour and output | Generic Core rule / parity | Known exclusion |
| --- | --- | --- | --- |
| `BridgeHwirAddWordCompiler`; source `YAKSys_Hybrid_CPU.Core.ScalarAluOps.AddWord/SubWord/MultiplyWord` with `SignExtendWord`, Golden commit `6946ccab9d888f7dae0a4d8e18480f436138acd9`, file Git blob `c375dd8f9388fb09bf0cb8c4c09a06f3a25403bc` | 32-bit wrap then word sign extension, scalar candidate `hwir/v0.1`; source snapshot v1 | CW-INT-06 must admit the **exact selected Roslyn method and closure**, typed add/sub/mul, overflow + sign extension and emit canonical Core HWIR **without** calling `GoldenAddWordCandidateCompiler` or `BridgeHwirAddWordCompiler`; **Blocked**. Existing Core `RoslynHardwareFrontend.Extract` still invokes the bespoke `GoldenAddWordCandidateCompiler`. | Golden file is not full CPU closure; previous 8,292 per-operation vectors in P16 are **historical** and were **not rerun here** |
| `BridgeHwirSchedulerCycleCounter`; `MicroOpScheduler.PackBundleIntraCoreSmt`; scheduler SHA-256 `c7e675d76bb4eed9ecccb0b20c13c973895cce556e93c21457d966add731d8bf`, method SHA-256 `4ecfa34a01d71033b9d6c0b3f2ce752cb50ff8d6004b3a2ba3d6f1c5710e340d` | `scheduler_cycle_counter` 64-bit initial-zero enabled register: invocation attempt edge, increment only when bundle present and count=8 | CW-INT-13 must establish target clock, init/reset, stall, lifecycle, full method exact symbol ownership and sequential simulation; **Blocked** | Call-clock is an assumed abstraction; no proven physical clock or Processor top |
| `BridgeHwirSmtRanking`; `GetCreditRankedOrder`, `PackBundleIntraCoreSmt`, `TryInjectAssistCandidates`; Assist source SHA-256 `8515039c3d770382fd448439d2453c01bd42397f3b525b413d411161bf8035a8` | Sampled combinational rank candidate, explicit `unwritten_tail` input | CW-INT-13 must specify the 4th rank independent of assumptions; **Blocked** | Rank writes only 3 non-owner slots, consumers access 4; **never assume owner** |
| `BridgeHwirCsrAccess`; source `CsrFile.CheckReadAccess` (projection recorded by existing code) | `csr_access_policy` combinational candidate, `hwir/v0.1` | Exact byte-checked Roslyn witness, CSR masks/access and Core typed lowering required; **Blocked** | No full CSR state/retire/trap temporal parity |
| `BridgeHwirProjectedMemoryState` and `BridgeHwirPipelineMemoryLogicalEvent` | Sampled or abstract logical-event/memory projections, *not a live RAM* | Core memory ports/arbitration/order/timing contract and independent witness missing; **Blocked** | Partial scheduler and sampled memory cannot become complete CPU |
| Lane/packet/Assist `BridgeHwir*` family | Sampled typed-lane/admission results and candidate path | Require exact Core symbol/typed HWIR and native differential per slice; **Blocked** | HardPinned physical lane6 ownership, cycle/retire/replay not qualified |

The file inventory covers every legacy projection, but per-method parity for the other files is NOT asserted. Source hashes above are pins **from inspected code**; fresh upstream Golden bytes must be separately captured and checked before an individual lowering rule is migrated.

## Running the report-only dual-artifact guard

Requires Python 3.10+ (stdlib). The independently produced *real* Core canonical artifact/observations are currently unavailable for generic lowering; do not pass a copy of legacy HWIR under the Core name as evidence.

```sh
python3 -m unittest discover -v -s Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/Tests -p 'test_*.py'
python3 Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/Migration/parity_guard.py \
  --slice scalar \
  --source /read-only/capture/ScalarAluOps.cs \
  --source-commit <exact-40-hex-commit> --source-sha256 <actual-64-hex-sha256> \
  --source-path HybridCPU_ISE/CloseToHSL/Core/Execution/Scalar/ALU/ScalarAluOps.cs \
  --source-method AddWord \
  --legacy-hwir /independent/temp/legacy.hwir.json \
  --core-hwir /independent/temp/core-generic.hwir.json \
  --legacy-observations /independent/temp/legacy-observations.json \
  --core-observations /independent/temp/core-observations.json
```

The angle-bracket fields are placeholders and this example is **not** a passing invocation. CLI exits **0** only if source bytes, metadata, typed port/clock/reset/memory/overflow contracts and every supplied vector match. It prints only JSON to stdout; zero means **report-only comparison**, not Verified. Missing/unknown data, changed source, mismatched owner, vector or digest, duplicate JSON properties, path symlink, cancellation via timeout or stale native receipt exits **2**, emits a JSON `Blocked` refusal to stderr and never writes an output. Read limits: source 8 MiB, each HWIR 16 MiB, each observation 4 MiB, optional native receipt 1 MiB; time budget defaults to 15 seconds.

Harness-local observation v1 format (NOT a new HardwareBridge/Core interchange or evidence contract):

```json
{
  "schema": "hybridcpu.hardwarebridge.hb-int-05.observation/v1",
  "producer": "legacy-bridge",
  "source": {
    "commit": "40 lowercase hex chars",
    "sha256": "64 lowercase hex chars",
    "path": "exact source origin path",
    "method": "exact source origin member"
  },
  "hwirSha256": "SHA-256 of the actual canonical HWIR JSON file bytes",
  "vectors": [{
    "id": "case-0001",
    "inputs": {"operand_port": "0"},
    "outputs": {"output_port": "0"}
  }]
}
```

Core observation has `producer=core-generic` and its own HWIR file hash. Vector values are **unsigned decimal bit-patterns** (including signed ports), and must fit widths; names must exactly match all non-clock inputs and outputs of the selected top module. Matching rows **do not prove** that vectors were evaluated by those producers or independently derived from Golden. The Core project must supply separately pinned provenance and independent runtime/native evidence for any future qualification. Optional `--native-receipt` only checks matching legacy HWIR **file** hash, three actual pinned executable hashes, zero recorded exits and `StructuralOnly` scope; it does not run native tools or qualify receipt freshness itself.

## Migration / downstream gates

1. Merge compatible Core CW-INT-06 and CW-INT-13 implementations, with exact Csc closure/source ownership and source-method-level witnesses; no auto-attributes or modifications to Golden.
2. Independently run Core generic lowering on at least one real Golden method without bespoke compiler, and preserve old bounded C# / RTL regression tests.
3. Capture canonical HWIR from both producers, independent Golden bit/cycle observations and current-source native receipts in isolated directories; pin tool versions/executable hashes, exact source worktree bytes and commit separately. Then run this guard for scalar and, only after clock/memory contracts, scheduler/CSR.
4. Only after documented semantic + native parity and Core acceptance, remove **mandatory** references to particular legacy Bridge compilers in a coordinated, backward-compatible PR. **This PR intentionally does not touch** `HardwareBridge.Hwir.csproj`, the existing opt-in regression project references or consumers. In particular it does not conflict with ongoing HB-INT-01 portability PR #5 and CW-INT-02 PR #2.
5. Keep rank4, HardPinned lane6, full memory/CSR temporal semantics, retire/trap/replay, complete Processor, FPGA and ASIC signoff `Blocked` until independent evidence exists. Never promote Candidate / ReportOnly / NotRun / incomplete P16 to Verified.

**Source immutability:** this PR adds files only under HardwareBridge and an isolated CI test workflow; Golden repository was not mounted or modified. Its byte hash **before/after a real Golden execution** is still NotRun here. No live Core/native/.NET/Windows execution took place in this environment.
