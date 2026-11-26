# HardwareBridge current status

Reviewed 2026-11-27 from tracked GitHub objects and hosted run metadata. No new local build, replay or native execution was performed. This register supersedes the status summaries in the integration plan and modernization audit. Protocol documents remain authoritative for their own formats. “Implemented” describes code at the baseline. Execution claims below are scoped to the named command, artifact and revision.

| Repository | Reviewed commit / dated reference | Role |
| --- | --- | --- |
| HybridCPU-Compilers, master | `8749de2c1c5cefbae82c22ab7d418f57d640bd71` | Software Core, CIL, Roslyn and opt-in Bridge producer |
| CircuitWeave | `8022bce4a6cfe89dda807630070c73094d893958` | Hardware reader, binding, HWIR, qualification and publication owner |
| HybridCPU-v2 | `6946ccab9d888f7dae0a4d8e18480f436138acd9` | Golden ISE source reference, retained from 2026-11-25 |
| CircuitWeave.Studio | `b92923b64adb9e1fcbefff15732ea8da68865853` | Downstream viewer, retained from 2026-11-25 |
| TraceWeave-Studio | `ab9f3f577653c1dd616e284b8442d0f01f0ba25e` | Reported CompilerFacts viewer, retained from 2026-11-25 |

The strict R0-02 workflow specifies Core `b5c44685197a40df99a4d48891120b12475c6ca5` and Golden `6946ccab9d888f7dae0a4d8e18480f436138acd9`. Runtime custody of owner approval variables and a scoped credential was not established. The reviewed Core HEAD above is a source-review reference, independent of this workflow's approval pin. R2-02 replay has separate dispatch inputs, receipt gate and workflow. Its current dispatch policy validates event, repository, master ref, mode and input pin syntax. It does not explicitly check `github.ref_protected` or compare those inputs to R0-02’s fixed owner variables. Treat that as an unresolved policy difference. See [R0-02](../R0_Core_Readonly_CI_Gate.md) and [replay](../HB_INT_06_CrossRepo_Replay.md).

## Profiles and dependency direction

| Profile | Tracked project shape | Evidence and scope |
| --- | --- | --- |
| Portable DTO/codec and protocol harnesses | Explicitly linked Bridge sources, without the production Companion dependency | Historical hosted Windows v1/v2 PASS at `d95ff907171fda783897d1a87febdc37f2bcce08`, each exit 0 |
| HWIR project default | `HardwareBridge.Hwir.csproj` defaults `EnableBridgeHwirLegacyFixtures` off and has no unconditional CircuitWeave references | Static project boundary only in this audit |
| Explicit HWIR migration fixtures | Enable flag plus exact Core root, HEAD and SDK. Four conditional Core project references (Roslyn Frontend, HWIR, Bindings, Contracts). Separate from Companion | Historical bounded runs only. Current R3-03 and independent temporal qualification BLOCKED |
| Production Companion | `HardwareBridge.Companion.csproj` explicitly includes immediate SourceSnapshot, CompilerFacts and GoldenOracle C# sources and defines `HARDWAREBRIDGE_COMPANION` | Historical Windows restore PASS (exit 0), build FAIL (exit 1) at `d95ff907171fda783897d1a87febdc37f2bcce08`. Current tracked prerequisite remains missing. Build at reviewed SHA NOT RUN |

MSBuild reference arrows point from consumer to dependency: **Companion → Roslyn → Cil → software Core → Platform.Contracts**. Roslyn also references **Analyzers**. The software processing order is **Roslyn admission and PE/PDB emit → restricted CIL import using software Core IR/contracts → Bridge projection**. CircuitWeave owns independent source verification and hardware processing. [Companion project](../../HardwareBridge.Companion.csproj) · [Roslyn project](../../../Roslyn/HybridCPU.Compiler.Roslyn.csproj) · [Cil project](../../../Cil/HybridCPU.Compiler.Cil.csproj) · [software Core project](../../../Core/HybridCPU.Compiler.Core.csproj).

Projects target `net11.0`. Roslyn declares `LangVersion=13.0`, package `Microsoft.CodeAnalysis=5.9.0` and conditional `Microsoft.CodeAnalysis.CSharp=5.12.0`. These are declarations. A fresh evaluated assets/deps graph and actual assembly SHA-256 were unavailable for this audit. In SDK-host mode `UseSdkRoslynForCompanion=true` disables the CSharp package declaration and `SdkRoslynHost.targets` binds both actual compiler assemblies from the selected SDK with specific identity. The software input profile defaults to C# 13 and gates C# 15 on explicit SDK-host identity. The current CircuitWeave root SDK is stable `11.0.100`. The main R0-01/HB-INT-06 pin is `11.0.100-rc.2.26504.105`; R3-01 vectors and TaskTimeObserver fixture workflows specify stable `11.0.100`, and managed smoke uses `11.0.x`. Record SDK, actual Csc path/version/hash, Roslyn assembly hashes, ordered options, generators/references and PE/PDB before claiming reproducibility.

HB-local output defaults are `.artifacts/obj/<project>` and `.artifacts/bin/<project>`, with `ArtifactsPath` override. This local props file does not govern external project checkouts. Qualification uses isolated output roots and before/after byte inventories for all read-only repos. A before/after check is a bounded observation and does not provide OS isolation or rule out intervening writes.

## Changes since the previous review

- HB master incorporates the Compiler vNext documentation update. The checked tree still has no tracked `Core/IR/Artifacts/**`; [#38](https://github.com/yuriyyak23/HybridCPU-Compilers/issues/38) and [#40](https://github.com/yuriyyak23/HybridCPU-Compilers/issues/40) are open. No Actions run was returned for this exact HB SHA at review time. The earlier restore/build and preflight exits remain in [historical evidence](evidence-index.json).
- CircuitWeave merged a [contract and qualification documentation reconciliation](https://github.com/yuriyyak23/CircuitWeave/pull/38). Its consumer register describes bounded v1/v2 import, narrow independent Roslyn re-emission and separate binding/verification authority. The reviewed CLI explicitly refuses v3/v4 before CWR3 composition after the bounded linkage audit. It reports candidate linkage, with task provenance `NotEstablished` and independent replay `NotRun`. Current positive paired execution remains BLOCKED. [CW compatibility](https://github.com/yuriyyak23/CircuitWeave/blob/8022bce4a6cfe89dda807630070c73094d893958/docs/current/COMPATIBILITY.md) · [CLI source](https://github.com/yuriyyak23/CircuitWeave/blob/8022bce4a6cfe89dda807630070c73094d893958/src/CircuitWeave.Cli/QualifiedBridgeCandidatePipeline.cs).
- The tracked WordDispatch blob remains 70,397 bytes versus its inventory pin of 68,443 bytes. This confirms the current mismatch without a new guard execution. The previous local and hosted failures are historical.
- Supplied Studio progress reports describe repeatable synthetic OpenROAD CLI placement (`CompletedUnqualified`) and a later explicit UI action whose first full host attempt stopped at `GLIBC_PRIVATE`. A successful post-fix run, accepted-RTL producer linkage and physical qualification were not established by the supplied reports. This downstream work does not close an HB producer or CW admission gate. [CW publication boundary](https://github.com/yuriyyak23/CircuitWeave/blob/8022bce4a6cfe89dda807630070c73094d893958/docs/architecture/Studio_Interchange.md).

## Integration gates

| Package | Scope and remaining gate |
| --- | --- |
| HB-INT-01 / CW-INT-02 | Portable vs pinned legacy project separation. Exact pin validation and positive opted-in builds are distinct checks |
| HB-INT-02 / CW-INT-03 | Segmented capture, projection and staging implemented. R2 canonical staging and actual paired import need reviewed inputs and byte receipts |
| HB-INT-03 / CW-INT-03/04 | v1/v2 DTO and codec checks exercised. Executed-Csc linking and output supplements remain diagnostic. Independent binding re-emission has its own narrower admission and receipts |
| HB-INT-04 | Scalar vectors, raw trace and scalar session are distinct Golden protocols. Live pinned observation and independent replay require separately supplied identity and evidence |
| HB-INT-05 / CW-INT-06/13 | Historical hosted guard stopped at source size. The tracked mismatch remains. R3-03 needs independent scalar HWIR and separate temporal verification |
| HB-INT-06 / R0-01, R0-02, R2-02 | Historical hosted jobs failed before Companion or paired reader could complete. Reviewed HB master remains unprotected and the current positive pairing is unconfirmed. Re-run only through the respective reviewed build and protected replay paths |

[Compatibility matrix](COMPATIBILITY.md) · [Machine-readable compatibility](compatibility.json) · [Blockers](blockers.md) · [Evidence index](evidence-index.json) · [Document inventory](document-inventory.md).

## Evidence rules for maintainers and coding agents

- Capture branch, exact HEAD, tracked/untracked state, tool identity and input pins before execution. Keep dirty-tree observations separate from clean checkout receipts.
- Preserve approved pin custody and the protected fail-closed gate. Use independently issued expectation pins. Remain on v1 by default until both owners review compatibility and an exact pinned paired execution passes.
- Keep real declarations and production graphs. No stubs, removed callers, fabricated PE/PDB, synthetic proof, or hardware authority upgrades. Record independently observed results and precise skips.
- Update protocol documentation and the current registry in the same change as a relevant implementation or evidence change. Cite immutable public receipts or allowlisted receipt identifiers. Keep private bytes, credentials and raw private logs out of the repository.
