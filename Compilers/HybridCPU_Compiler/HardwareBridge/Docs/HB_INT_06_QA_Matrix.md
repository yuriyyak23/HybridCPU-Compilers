# HB-INT-06 — cross-repo QA matrix and no-source-write evidence

**Scope:** read-only HardwareBridge software facts and test-only orchestration. **Not** an EDA executor, independent verifier, AcceptedRTL gate, Golden runner or ASIC signoff.
Owner: HybridCPU-Compilers / HardwareBridge. Backlog: HB-INT-06. Plans: [Bridge PR #4](https://github.com/yuriyyak23/HybridCPU-Compilers/pull/4) and [Core PR #1](https://github.com/yuriyyak23/CircuitWeave/pull/1).

## Exact audited refs and protocol versions (2026-11-19)

| Input | Exact identity | Scope |
|---|---|---|
| Bridge initial master | 94f59223b36659c9162620224f91349ac79fb896 | original base; reconciled atop f0d728a5939f0c7507df9e828d2af2f8fac8dac9 with HB-INT-01/02/03/05 |
| Core master / paired pin | 21706d19ede6a1ff87230fa2e41071f4f32d72a7 | CW-INT-03 strict v1/v2 bounded contract host merged; no independent source admission |
| Golden source checkout | 6946ccab9d888f7dae0a4d8e18480f436138acd9 | read-only HEAD checked by test harness |
| Windows SDK | 11.0.100-rc.2.26504.105 | pinned to Core global.json; wrong SDK hard fails |
| Roslyn package | Microsoft.CodeAnalysis.CSharp 5.12.0 | Roslyn project pinned package; actual restore required |
| Sidecar | hybridcpu.hardwarebridge.sidecar/v1 | unchanged valid wire shape; stricter duplicate member rejection |
| Source | hybridcpu.hardwarebridge.source-snapshot/v1 | SHA-256 self-consistency, not independent evidence |
| Software facts | hybridcpu.hardwarebridge.compiler-facts/v1 | Candidate/Rejected only |
| Golden observation | hybridcpu.hardwarebridge.golden-observation/v1 | allowlisted Observed/Blocked only |
| Segmented index | circuitweave.bridge-source-closure-index/v2 | opt-in software candidate codec and Core contract reader; full actual Csc closure unqualified |
| QA receipt | hybridcpu.hardwarebridge.qa-receipt/v1 | test-only, never hardware acceptance |

## Integration matrix

| Stage | Responsible test / owner | Honest outcome |
|---|---|---|
| Standalone Companion | actual dotnet run HardwareBridge.SelfTests.csproj | runtime exit and stdout/stderr/executable SHA required |
| v1 sidecar + strict parser | same executable self-tests | known schemas, current source digest, reject malformed/duplicate |
| Actual fixture Csc PE/PDB | same self-tests | real Roslyn/CIL compile-only; wrong PDB and roots rejected |
| Golden no-write | Run-HbInt06.ps1 | compare Git HEAD, full file SHA inventory and git status before/after |
| Mutation probes | HbInt06.MutationTests.ps1 | real synthetic Git checkout and actual pwsh timeout |
| v2 segmented *codec contract* | HardwareBridge.V2.ContractTests.csproj | actual production codec test + chunk/hash/role mutations; no Golden capture |
| v2 actual Csc capture qualification | HB-INT-02/03 + CW-INT-03/04 | **Blocked**: pinning current Csc closure and independent verified source is incomplete |
| Strict Core importer contract | CW-INT-03 Core-owned test at exact pin | requires read-only `HB_INT_06_CORE_READ_TOKEN`; missing GitHub Actions cross-repo permissions explicitly Blocked; scoped to synthetic/fixtures, not full Bridge Producer→Core proof |
| Actual Bridge→Core provenance replay | HB-INT-02/03 + CW-INT-03/04 | **Blocked** pending qualified actual Csc closure and companion artifact replay |
| Independently qualified source closure | CW-INT-03 | **Blocked**: producer checksums cannot qualify |
| Golden oracle/RTL evidence | CW-INT-10/11/12 | **Blocked**: needs independent receipts |
| Native tool pins / approval | CW-INT-14 | **NotRun**: no native Bridge execution |
| FPGA and ASIC target qualification | CW-INT-15/16 | **Blocked**: separate target and signoff gates |

## Exact byte audit

The test harness scans **every present file outside .git** in the selected
Golden checkout. For each ordinal-sorted relative path, the digest preimage
is UTF-8 path + TAB + file decimal length + TAB + uppercase file SHA-256 + LF.
It separately records HEAD and Git porcelain status including untracked files.
An altered byte, missing/extra file, source link/junction, changed Git status or
wrong HEAD fails the no-write audit. This is a present-file SHA inventory,
**not** source-closure admission: a HEAD hash alone cannot characterize dirty
inputs, SDK options, references or generated Csc source.

All build/restore/temp files and raw stdout/stderr must be produced under a fresh
external output root. The test harness refuses a pre-existing output root, an
output within Bridge/Golden/Core, and linked existing output ancestors.
The synthetic Companion fixtures compile only in-memory C# and do not load
or execute emitted PE. Golden source is never built or restored.

Each actual process receipt includes executable path/SHA, ordered argv, exit
code, stdout SHA, stderr SHA and status; a zero-step or forged PASS is invalid.
Timeout kills the process tree and is Blocked. Missing executable is NotRun.
Output report is the unqualified software-only QA receipt, not an evidence DAG.

## Negative, mutation and regression

**Executable synthetic probes:** edited Golden fixture file, accidental generated
source write, wrong pinned SHA, output traversal, zero-step/fake executable,
missing receipt hashes, purported unauthorized native PASS, actual timeout,
and linked source when the host permits creating a symlink.

**Companion tests:** v1 positive serialization and expected source digest;
unknown v2 schema; duplicate JSON property; duplicate exact roots (including
tampered duplicate roots with a recomputed digest); incompatible reference
roles; traversal; stale current source; corrupted payload; mismatched PE/PDB;
missing imported graph; ambiguous root metadata; cancellation.

**Not yet covered by actual Native/Golden integration:** source closure with
actual HybridCPU-v2 PE/PDB and exact Csc options, tampered v2 chunk covered by isolated codec + Core importer fixtures, while actual Golden-produced v2 chunks remain unqualified, Core importer/independent source verification, missing
approved EDA runner, native pin/cancellation controls and independent RTL
acceptance. Do not call any of these PASS on the strength of fixture checks.

## Release and compatibility

A zero exit from Run-HbInt06.ps1 means **standalone test-only QA passed**
and Golden bytes were unchanged. The receipt still explicitly marks
Core/v2/native/RTL/physical as Blocked or NotRun. Exit 1 means a failure
(including wrong SDK, wrong SHA, changed source or timeout); exit 2 means
the .NET executable was unavailable. With the explicit RequirePaired switch,
missing Core checkout, wrong SHA or absent actual Core test project is a
failure, not a skipped integration PASS.

The existing v1 payload fields, order, names and status enums are unchanged.
Only malformed inputs with duplicate JSON keys or duplicate exact roots with
recalculated checksums are now refused. Valid canonical v1 is retained.
There is no automatic v1-to-v2 schema migration. The bounded v2 codec and Core reader are merged as *unqualified* source fact contracts; next require an actual Golden Csc closure producer and independent Core source verifier with immutable replay evidence before any default switch.

**Limitations:** test compilation is a bounded AddWord software facts fixture,
not complete Processor Csc closure. The file audit binds checked-out bytes,
not an external missing sibling, missing LFS binary, SDK/analyzer environment
or unmaterialized source. A Windows runner job or documented command does not
count as executed until its actual logs/exit/hashes are available.

## Live Windows evidence 2026-11-19

- [Windows run 44557126414](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44557126414) on PR #12: external exact prerelease .NET 11 SDK Passed exit 0; v1 codec Passed exit 0; v2 codec Passed exit 0; synthetic no-write/timeout/mutation Passed exit 0. Actual Companion Csc PE/PDB **Failed exit 1** due pre-existing missing `HybridCPU.Compiler.Core.IR.Artifacts` and `CompilerEmissionPackage`; never substituted fixture for Csc.
- Full Golden present-file source SHA before and after: `3A024E3A8E3C470CCFC1F87C2479E11B0827AED3342921FBED1D3A42A88712E5`, unchanged.
- `v2-codec-contract` stdout SHA256: `2710E72A922677E72F96873D2836945AF8047D22898602AC3AA96C32510662D7`; `v1-protocol-tests` stdout SHA256: `4D3434F50A7DE62D4A3EDF74283FF9002A489E4DF7AF11F5EE7FD52E7F3D4E61`; `no-write-mutations` stdout SHA256: `1070B3D160E26FD6BC947FF5DC36E2C49D5B04976AE17C7F11392C9E833E33AF`; actual separate command exits 0, 0, 0.
- Core cross-repo checkout **Failed/Blocked** (`git fetch` exit 128, `Repository not found`) because the default Bridge workflow token cannot access the pinned Core repository. The workflow now requires an explicit least-privilege repository secret `HB_INT_06_CORE_READ_TOKEN`; missing secret yields fail-closed exit 2 and **never** a Core importer PASS. A real Core contract receipt remains NotRun until read access is explicitly provided and tests execute.
- No RTL independent acceptance, Golden Processor source semantics or physical signoff was executed; these gates remain Blocked.

## 2026-11-20 — owner-operated pinned private Core CI gate (R2-02)

**Live base:** Bridge `master@797b6dd4fc1c7a44f958ebb22d43e745d0e6938c`; Core `master@18caf04768df6abf31f5517dbb489e7fea2e1c7e` (this new gate pins exactly that Core commit, not the earlier `21706d1...`); Golden `6946ccab9d888f7dae0a4d8e18480f436138acd9`; SDK `11.0.100-rc.2.26504.105`. Only a deliberately reviewed workflow change may rotate any of these SHA/version pins. Pinned `actions/checkout`, `actions/setup-dotnet`, `actions/upload-artifact` are immutable GitHub action commit SHAs rather than moving major tags. Core importer is the actual `tests/CircuitWeave.CWINT03.BridgeImport/CircuitWeave.CWINT03.BridgeImport.csproj` checked into that private Core revision; it uses synthetic byte fixtures and **does not validate a real Golden compiler export**.

**Repository owner steps (required; cannot be completed by an assistant without the actual credential):**
1. Create a **fine-grained personal access token** (or approved installation access token) that can read **only** `yuriyyak23/CircuitWeave` with **Repository permissions → Contents: Read-only**; repository metadata is implicitly readable. No workflows/actions write, issue write, administration, package write or broader organization permissions. For a time-limited credential, arrange explicit rotation before expiry; do not reuse the default Bridge `GITHUB_TOKEN`, which is repository-scoped.
2. In **HybridCPU-Compilers → Settings → Secrets and variables → Actions → New repository secret**, set the **exact name** `HB_INT_06_CORE_READ_TOKEN` and paste the token value privately. Never place it in workflow YAML, issue/PR body, diagnostic command, `git remote -v`, artifact, or CI log. Treat this token as a secret and revoke/rotate if leaked. Review the permissions and expiry in GitHub's token settings.
3. Review/merge this Draft PR when appropriate; run the protected workflow on a **trusted master push** or `workflow_dispatch` on `master`. **Pull-request runs intentionally receive no private Core credential**: the named required paired job terminates **Blocked** (exit 2), not skipped or green, even when the owner has installed the secret. This avoids running editable PR code with private Core read credentials. Do not mark a PR's skipped or fixture-only checks as cross-repo qualification. Owners must configure branch protection/check expectations accordingly; a mandatory paired check on PR cannot pass under this trust policy. For premerge private Core testing a separately reviewed trusted-base workflow is required.
4. Check the exact run's `Pinned Core importer contract host` job. Missing token and unauthorized/expired token (checkout step failure) are **Blocked**; wrong SHA, shallow checkout, dirty checkout or missing Core test project are **Blocked**, not synthetic PASS. The authenticated read-only checkout has `persist-credentials: false`; Core history is full-depth to reject shallow, with its own path/workspace; no public unauthenticated private-Core URLs are used.

**Runs/tests:** the paired job invokes `Run-HbInt06.ps1 -RequirePaired` **without** `-CoreOnly`, running v1, v2, Companion, synthetic mutations and the real **Core-owned** CW-INT-03 reader executable in a fresh `RUNNER_TEMP` output root. It hashes Golden before/after, and newly Core before/after, rejects dirty Core writes, source links and wrong HEAD, and collects exact executable/argv/exit/stdout/stderr SHA-256 receipts. Both test families' failures remain visible individually. It never executes Golden PE, HWIR-to-RTL lowering, native EDA or proof. Even success is only `unqualified software-facts-only`, not `Verified`.

**Artifact policy:** paired job uploads **only** `hb-int-06-paired-summary-no-source/summary.json` (allowed SHA, argv, exit, statuses, before/after tree digests). Raw compiler output, private Core checkout, Golden/Bridge source file inventory, Csc source bytes, test fixtures and Git credentials remain on the ephemeral runner, **not** in paired Actions artifacts. Never pass token through argv; checkout owns ephemeral authentication. The standalone public Bridge job still uploads its existing short-retention raw Bridge-only diagnostics.

**Negative evidence:** `HbInt06.CoreGate.ps1 -Mode SelfTest` (synthetic Git only) refuses missing credential, simulated expired/unauthorized checkout outcome, truncated/invalid/wrong SHA, path escape, missing project, dirty source and shallow checkout. Real token expiration is **not simulated as live authentication**; it must be demonstrated by a failed authenticated Core checkout in an actual runner. Core-owned strict v1/v2 tests separately refuse malformed schema, altered source/chunk bytes, missing artifacts, and incompatible roles. If Core reader must expose additional consumer APIs, create **a separate Core-owned issue/PR under R2-02**; never edit its reader in this Bridge PR.

**Known failure on baseline run:** [Bridge Actions 44634030755](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44634030755) on `master@797b6dd4fc1c7a44f958ebb22d43e745d0e6938c`: paired failed at missing `HB_INT_06_CORE_READ_TOKEN`; standalone v1/v2/mutations passed but `companion-selftests` failed `exit=1`. Owner of Compiler/Core.IR artifacts must fix Companion separately (HB-INT-01/06 compiler dependency issue); this CI supply-chain PR intentionally does not modify Golden or compiler contracts. Missing secret remains Blocked, new PR CI statuses are not pre-claimed as Passed.
