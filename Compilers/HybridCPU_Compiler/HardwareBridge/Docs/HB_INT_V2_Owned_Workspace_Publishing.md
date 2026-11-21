# HB-INT-02/03/06 — opt-in v2 owned workspace publication

**Scope:** HardwareBridge read-only software facts -> portable Core CW-INT-03 transport. The new publisher is not a compiler, Core reader, independent source verifier, EDA runner or verification authority.

Baseline for this follow-up: HybridCPU-Compilers `master@77947f82cd61ed5248c0ae48d722d7fbfdf29a9a` after merged [HB-INT-03 PR #10](https://github.com/yuriyyak23/HybridCPU-Compilers/pull/10); CircuitWeave `master@26724fc70a1cb7c403aa50d8e0f41383202a22ac`. [Six-package HardwareBridge plan](CircuitWeave_Pipeline_Integration_Plan.md), [Core CW-INT-03 reader](https://github.com/yuriyyak23/CircuitWeave/pull/3).

## Production interface

`BridgeV2OwnedWorkspacePublisher.Publish(transport, inputs, roots, liveSources, goldenRoot, token)` requires:
- Already built `BridgeV2Transport` from `BridgeSegmentIndexV2.Write`, including a byte-exact canonical compact UTF-8 Core-compatible index and JSONL chunks.
- Caller-owned actual source/reference/compiler `BridgeV2Input` bytes, explicit roots and **one current absolute live file pin per `source` row**.
- Source-origin value `original` or `working` only for current files inside the explicit Golden root. `generated` files must be **outside** Golden. At least one source must be inside Golden.
- Fully qualified, existent, non-reparse Golden root; no source or root path auto-discovery, no implicit paths from git HEAD.

The publisher checks original SHA/size before and after staging and separately rehashes the published bytes. It returns an unqualified `BridgeV2PublishedWorkspace(Root, IndexPath, IndexSha256, ClosureSha256)`, not an accepted binding or signed receipt. Index/chunk/file bytes and hashes preserve the existing Core PR #3 schema. The publisher does **not** change the opt-in v2 or default v1 wire formats, or the distinction between SourceSnapshotDigest and CompanionSourceInputDigest.

Output is **only** in a newly allocated OS-temp child directory (random name `hb-int-v2-*`); the API does not allow callers to choose an output path or overwrite an existing file. It rejects OS temp nested under the Golden root, reserved `index.json` collisions, path traversal, case collisions, unknown source origins and observed symlink/reparse ancestors. On an exception it attempts to remove only the newly created output directory. On success, caller owns cleanup once independent Core ingestion ends; no automatic deletion before Core can read.

**Use with Core:** pass the actual bytes of `IndexPath`, the published `Root` and an **externally trusted** expected closure SHA-256 into the Core-owned `BridgeSegmentIndexImporterV2.Import`. Do not merely copy the producer's `ClosureSha256` as independent expected value. Core must rehash files and independently verify source/ownership/manifest/locks; this publisher only stages byte evidence.

## Negative checks / trust

Isolated `BridgeV2OwnedWorkspaceTests` runs actual disk operations against a **synthetic test Golden** and tests byte equality, deterministic output content, Golden source SHA before/after, stale source, altered candidate input, missing source, invalid original/generated role, reserved output collision, cancellation, symlink rejection where supported and cleanup. No synthetic test Csc or PE/PDB receipt is reported as a real build.

There are residual race limits for hostile processes running as the same user: path preflights cannot replace OS-level secure directory handles / no-follow flags on every platform. This opt-in transport must remain **unqualified** and runs only in a separately approved, isolated capture environment; a malicious concurrent rename or host compromise is a **Blocked** qualification gate, not a reason to relax checks.

## Commands / gates

    dotnet --info
    dotnet restore Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.V2.ContractTests.csproj
    dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.V2.ContractTests.csproj -c Release

The existing [HB-INT-06 QA harness](HB_INT_06_QA_Matrix.md) and full Companion self-tests remain separate. A passing isolated publisher test does NOT unblock exact real Csc-task invocation capture, generated/analyzer/MSBuild closure provenance, Windows reparse/race assurance, source timebase or a full Processor closure. Those require HB-INT-02/06 and independent Core CW-INT-03/04 receipts on actual Golden source bytes. Hardware RTL/FPGA/ASIC/signoff are never Bridge-owned.


## R3-02 (2026-11-21) — bounded sealing and rehash handoff

Producer accepts **only unqualified bytes**. Caller-owned index, chunk arrays and file bytes are frozen before validation; supplied transport row descriptors must byte-for-byte agree with the regenerated canonical inventory. The publisher rejects file/directory prefix conflicts and verifies every expected staged file and directory, plus absence of untracked descendants, before and after sealing. It reopens live source pins before handoff; deterministic disk mutation tests exercise changes after initial preflight and after staging.

After flushing new files, the publisher marks staged files read-only. On Unix it also restricts owned directories to user read/execute. These are discretionary access protections, **not immutable CAS, a cryptographic signature, or an unbreakable lock**. On Windows a read-only directory attribute is not an ownership/security boundary; a process with suitable privileges (and a same-user process capable of chmod/attribute changes on Unix) can still replace data. The consumer must rehash all files at the moment of independent Core import. No assertion of hostile concurrent rename immunity, source qualification, Golden provenance authority, or AcceptedRTL follows from this producer.

On exception, cleanup addresses only expected output files and directories in the new owned TEMP root; it never recursively deletes an unknown descendant or follows a detected reparse point. If an untracked path appears, the producer refuses and intentionally leaves that unknown path for explicit quarantine/removal. The original refusal is preserved; the test fixture cleans its *own* injected file. This is a fail-closed outcome, not a clean successful publication. The API never accepts a destination chosen by the caller.

The production implementation still depends on path-based .NET APIs for source reads and stage writes. A hostile same-user concurrent replacer can race between path checks and opens. To attain hard no-follow/identity guarantees across the entire lifecycle requires OS-specific handle-relative open/rename discipline and an isolated non-hostile staging principal; a path check is not a substitute. The real Golden capture is **NotRun** until separately approved, isolated and source-SHA-inventoried. Cross-repository Core replay remains **R2-02 dependency**, not implemented here.

### R3-02 test scope and platform qualification

The existing `HardwareBridge.V2.ContractTests.csproj` host compiles the real publisher with conditional test-only disk mutation seams. Tests exercise canonical index/role/refusal, stale source before and after staging, changed output bytes, missing chunk, unknown staged file, path traversal, file-versus-directory collision, forged `Verified` field, wrong rows, malformed/duplicate roots, cancellation after files were staged, and reentrant publication in distinct TEMP roots. Real file-symlink and directory-ancestor-symlink rejection are now mandatory on **both** Linux and Windows CI runners; missing Windows link privilege fails the job instead of counting an unexecuted test as success. This is **not** a test of untrusted concurrent hostile-process race immunity.

The dedicated `hb-int-v2-workspace.yml` matrix runs on Ubuntu and Windows using SDK `11.0.100-rc.2.26504.105`, with stdout/stderr hashes, test-host binary hashes, and before/after entire Git-tracked checkout SHA inventory. No private Core token is required; no Golden source checkout is restored or built.

```sh
dotnet restore Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.V2.ContractTests.csproj
dotnet build Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.V2.ContractTests.csproj -c Release --no-restore
dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.V2.ContractTests.csproj -c Release --no-build --no-restore
```

**Open dependency:** R2-02 owns the paired Core `BridgeSegmentIndexImporterV2.Import` replay with externally trusted expected closure and explicit private Core read permission. A success from this Bridge-only host cannot mark that integration TestedInCI.

## R3-02 post-merge repair — strict compile / seal gate (2026-11-21)

This change is strictly additive to merged PR #36 and does not revise Core-v2 wire bytes or
software-origin authority. The prior pinned workflow run
[`44836903358`](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44836903358)
failed on **both** Ubuntu and Windows at `CS0161` in `Publish`. Its catch branches now
throw `BridgeV2RefusalException(InvalidPin)` explicitly after scoped cleanup;
the compiler is not required to infer non-returning control flow from an attribute.
The Unix-only `File.SetUnixFileMode` mutation checks `OperatingSystem.IsWindows()`
*inside the lambda* to establish a correct CA1416 platform guard.

The bounded post-seal check verifies every staged file has `FileAttributes.ReadOnly`
and, on Unix, verifies that no owned directory grants owner-write mode. Test-only
`AfterSealForTests` clears a file's seal while preserving its bytes and must return
the typed `InvalidPin` refusal, cleaning the owned output. An injected `IOException`
inside a multi-block file copy must also return a typed refusal and remove its
partially written own TEMP. An ordinary source and its symlinked ancestor must
both be refused on both OS CI runners. Tests never open, write, or build the
actual Golden checkout and never import the private Core consumer.

**Security limits:** `ReadOnly` and Unix mode are advisory DAC checks, not
privileged OS hardening against an attacker able to alter attributes or replace
path components concurrently. No hard no-follow handle-relative identity or
safe same-user hostile race guarantee is asserted. A foreign/untracked injected
descendant is deliberately not recursively destroyed by production cleanup; a
test fixture owns and removes its own injected path. Core independent byte
rehash and externally trusted closure expectation remain R2-02 dependencies.

**Acceptance:** Build, actual test-host execution, current-head Ubuntu and
Windows job URLs, stdout/stderr SHA-256 and PE/PDB SHA-256 must be reported
only after the jobs complete. Previous failing runs do **not** qualify this
branch. Real approved Golden software capture, Csc provenance and Core paired
acceptance remain Blocked/NotRun until separate owner receipts.

**Exact-head CI lease:** For pull requests the workflow explicitly checks out
`github.event.pull_request.head.sha`, not GitHub's synthesized PR merge commit.
Both OS jobs check the resulting git HEAD against the supplied immutable head SHA,
then compare the complete tracked-source SHA inventory before and after the
isolated build. The same-byte source replacement mutation must execute on
both operating systems. A missing symlink privilege fails that OS job rather
than silently awarding a PASS.
