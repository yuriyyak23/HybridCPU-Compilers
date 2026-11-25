# HB R0: opt-in MSBuild Csc task-time observer (approved toy fixture)

Collect the project node, task arguments, response-file and generated-order observations in the same reviewed session. MSBuild task events and a logger are insufficient evidence of OS-level compiler reads. See [current status](../../../Docs/current/STATUS.md).

**Scope:** software compilation facts only, always **unqualified**. No changes to
Golden HybridCPU-v2, no hardware lowering, no native EDA, no independent proof
claim. The existing external \`hybridcpu.hardwarebridge.csc-observation/v2\`
importer remains unchanged and independently callable.

## What is actually observed

\`CscTaskLogger\` is a normal MSBuild \`ILogger\`, loaded explicitly via
\`-l:HardwareBridge.CscObservation.CscTaskLogger,<pinned-assembly>\`.
It subscribes to **live** \`TaskStarted\`, \`TaskCommandLineEventArgs\`,
and \`TaskFinished\` events, correlating their build node/project/task
identities. A missing or duplicate command event, unsuccessful task or
ambiguous project causes refusal. It records the original task command line,
not a command invented from design-time evaluation, /getItem or console text.

\`task_csc_observation.py\` accepts only two disjoint, nonlinked TEMP
directories: an immutable fixture baseline and a NEW isolated build copy.
It requires an observed Csc task that references the exact pinned SDK
\`csc.dll\`; exact ordered parsed argv is kept in the *existing* v2 schema,
including project, source, MSBuild-generated source, compiler, analyzer and
reference file pins. The existing v2 importer rehashes independently staged
bytes and performs strict option, origin and path validation. It refuses
unsupported aliases, unknown switches, duplicate paths and stale bytes.

**Unproven by a task event alone:** actual OS \`execve\` argv, compiler
process working directory, response files that may disappear before
post-build collection, source-generator completeness beyond the fixture,
and complete Golden closure. This subset REFUSES unobserved response files;
it does **not** silently expand them from late/missing bytes. The adapter
also refuses commands outside a conservative simple POSIX quoting subset
and any Csc arguments the existing v2 importer does not admit.

A task-event receipt and verified transport digest are **not** hardware
verification, not a proof of a Golden build, and not a basis for promoting
\`Observed\`/\`Candidate\` to \`Verified\`.

## Running the approved real software compilation

CI job:
\`.github/workflows/hb-r0-tasktime-csc-observer.yml\`

SDK: \`11.0.100\`. Linux Ubuntu 24.04 + Python 3.13.
The local equivalent, **only on a prepared host with that exact SDK**:

\`\`\`bash
python Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/TaskTimeObserver/run_fixture.py
\`\`\`

The script makes a new TEMP baseline copy and isolated build copy of two
toy projects. The Generator project uses the SDK-installed
\`Microsoft.CodeAnalysis.dll\` rather than fetching an unpinned generator
package; the Consumer project references it as a source generator and
requests real emitted source files. It separately builds the logger to
TEMP, invokes the genuine SDK-managed \`dotnet build\` for both toy projects
with a live logger, pins the \`csc.dll\` SHA and \`/version\`, writes raw
stdout/stderr and exit receipts, then attempts a strict existing v2 capture.
It runs source/flags/generated/reference pin mutations and refuses anything
not replayable. It checks fixture baseline file hashes before and after.

CI uses \`HB_CSC_RECEIPTS_DIR\` to persist raw receipts as artifacts.
\`git status --porcelain --untracked-files=all\` is checked before and
after the job. No Csc/restore/build is launched in the source checkout's
Golden project. No Golden checkout is performed at all.

\`\`\`bash
cd Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/TaskTimeObserver
python -m unittest -v test_task_csc_observation
python ../test_segmented_csc_capture_v2.py -v
\`\`\`

Negative JSON-parser tests are **synthetic**, not executed-Csc proof.
The CI build/test and its raw event receipt must genuinely succeed before
the **toy only** gate can be reported as Passed.

## Boundaries and explicit blockers

- **Golden execution: Blocked/NotRun.** Requires R0-01 compiler build
  restoration, owner approval, pinned read-only Golden source and a fresh
  isolated writable clone, plus source pre/post hashes and Git status.
  This package does not inject targets into original Golden.
- **Full effective OS process argv / current working directory: Blocked.**
  An MSBuild \`TaskCommandLineEventArgs\` event is associated with the
  Csc task execution but is not an OS-level syscall witness. A separately
  reviewed, fully pinned process-level observer would be required for
  stronger claims. No fabricated process receipt is emitted here.
- **Response file expansion: Blocked** unless the byte sequence was
  captured while live; this version refuses every \`@rsp\` rather than
  reading potentially stale/deleted response files after completion.
- **Generator completeness: Blocked** if emitted files cannot be located,
  if the analyzer is absent or output is inconsistent. The fixture has
  a real source generator, but that is not proof of HybridCPU generators.
- **Core independent qualification: NotRun**, owned by CircuitWeave
  (CW-INT-03/04 and downstream). A successful Bridge v2 transport is
  always unqualified and does not authorize hardware intent.

## Reviewer acceptance policy

Do not mark the R0 task fully complete merely because this branch has a
green unit test. Require a GitHub Actions run URL, actual build/logger
receipts, SDK tool version + SHA, source/gen/reference pins, v2 root hash,
negative mutations and source no-write confirmation. Treat any failed
invocation, skipped step, generated-source absence, unknown switch, or
missing raw evidence as **Failed/Blocked/NotRun**, never \`Passed\`.

## Windows Golden task-event projection (2026-11-22)

project_windows_task_events.py is a separate Windows-only parser for an externally SHA-pinned live CscTaskLogger event receipt. It uses shell32!CommandLineToArgvW, preserves token ordering and refuses any @response argument. It records working directory as NotRecorded and keeps execution, provenance, and admission at NotEstablished/NotEstablished/NotAdmitted. It does not hash or stage files and is not an OS execution witness. Golden task projection evidence is stored under CircuitWeave-main/artifacts/processor-20261114/golden-pinned-csc-task-capture-20261122/; this tool is an inventory adapter only, separate from the toy R1 gate.

## Pinned Golden Linux process and response-file observation (2026-11-22)

A separate clean TEMP clone of the fixed Golden commit built successfully on Ubuntu WSL with the pinned RC2 SDK. The VMX projection-lineage PowerShell verifier succeeded separately on Windows; the Linux build disabled only its Windows-shell MSBuild target. The task logger recorded five successful project events and `strace` observed five Csc processes. A Linux inotify watcher copied each response file on `IN_CLOSE_WRITE`; all five copied bytes match the recorded lengths and SHA-256 digests. The main Csc PID 676 exec argv names the same response path as `005.rsp`, within the main project's task interval; its 124,246 bytes hash to `0c4cae5ac1aca2cdb6b6df1c7e7ab55fe98a773927760b568c563db3d0d17b74` and the process exited 0. Receipt and immutable artifacts (historical receipt ID `artifacts/processor-20261114/golden-linux-csc-rsp-observation-20261122/receipt.json`, unavailable for this review).

This is unqualified Linux observation, not a Windows OS-process/read-buffer witness or complete compile-time input closure. A separate Windows task-level build result is recorded below; source/reference/analyzer byte capture, generator outputs, independent Core admission, and trusted handoff remain open. It does not relax the R1-01 toy-only fixture gate or its refusal behavior.

### 2026-11-22 — Csc source bytes reconstructed from process reads

The exact main response file names 1,104 distinct `.cs` inputs. A bounded audit of the Csc process/thread tree (`PID 676`, 67 trace files) reconstructed complete `read`/`pread64` buffers for all 1,104 paths: 8,978,960 bytes total. Every reconstructed digest matches the corresponding post-build byte in the clean pinned Golden clone; the normalized response-file source path set equals the captured manifest set (1,104/1,104). Source archive and thread trace archive verification passed. Receipt, source manifest, source bytes, raw traces, and validation (historical receipt ID `artifacts/processor-20261114/golden-linux-csc-source-read-observation-20261122/receipt.json`, unavailable for this review).

Classification remains unqualified Linux observation. The package does not establish the 180 reference bytes, analyzer/config inputs, embedded resources, in-memory generator output, independent Core admission, Windows production execution, or trusted handoff.


## Windows Golden task execution and cross-runtime source/IL check (2026-11-22)

A Release build of the same clean pinned Golden clone succeeded on Windows and recorded five successful live MSBuild Csc task events. The main task projection contains 1,104 source, 180 reference, 10 analyzer, 2 analyzer-config and 3 embedded arguments; no `@response` token is present. The task event does not record Csc cwd or prove OS process/read-buffer bytes. Windows source bytes are post-build snapshots resolved against the build invocation directory, not Csc read-buffer evidence. Its source path set matches the Linux response manifest. Of 1,104 archived source files, 1,102 hashes match exactly; two generated `.cs` files differ only by CRLF/LF, while all normalized source text matches. Linux rebuild succeeded. A bounded PEReader comparison found 30,561 matching method definitions with no differences in method keys, raw signature blobs, raw IL byte streams, or `MethodAttributes`/`MethodImplAttributes` flags. Custom attributes, body headers, local signatures, exception regions, type/assembly metadata, and full PE/PDB semantics were not compared. PE/PDB and the two installed `csc.dll` files have different SHA-256 values. Windows task observation receipt and artifacts (historical receipt ID `artifacts/processor-20261114/golden-windows-csc-task-observation-20261122/attempt-03/receipt.json`, unavailable for this review).

This remains task-level software evidence. It does not establish Windows Csc process/read-buffer capture, complete external input bytes, analyzer side-effect closure, independent Core admission, trust or hardware verification.

## Windows five-process task/RSP bundle adapter (2026-11-22)

`windows_task_rsp_bundle_v1.py` validates an externally pinned five-event/five-process/five-response bundle. It rechecks each compiler image path/SHA, task/process/project/PID/RSP identity, response copy length/SHA, and ordered tokens from the copied response bytes against the matching successful MSBuild task command using Win32 `CommandLineToArgvW`. The dedicated `test_windows_task_rsp_bundle_v1.py` suite uses synthetic fixtures only. Positive validation of the current Windows observation writes only to TEMP; it is not a Core staging/import run.

The adapter keeps the evidence unqualified: Win32 process sampling is not a complete kernel trace; copying a response file while the process is present does not prove Csc consumed those bytes; `CommandLineToArgvW` token equality is not a Roslyn response-parser/read-buffer proof; cwd remains `NotRecorded`; input-byte closure, provenance, independent Core admission, trust and hardware verification are not established. It does not change the existing POSIX-oriented V2 capture/stager or the toy-only R1 gate.

## C# Windows task/RSP bundle assembler (2026-11-24)

`CscTaskRspBundleAssembler.csproj` is an additive .NET 11 implementation of the same versioned Windows bundle. It validates externally pinned task-event, process, crosswalk and receipt bytes, the pinned compiler image, and response-copy hashes and token order before publishing a new direct TEMP output file. It does not execute Csc, observe cwd, or claim response consumption. On the retained pinned Golden Windows task/process/RSP evidence, its 281,939-byte output matched `windows_task_rsp_bundle_v1.py` exactly; bundle and receipt live under Core `artifacts/processor-20261114/hb-rsp-bundle-dotnet-golden-20261124/`. Its 3-test Windows suite is synthetic. The format remains an auxiliary observation bundle and is not raw `csc-observation/v2` / raw HB closure V2 input because cwd and source-byte inventory are not bound.

## Evaluated graph configuration globals (2026-11-24)

`Test-EvaluatedGraphGlobals.ps1` is a Windows PowerShell fixture for the additive `--global-property=NAME=VALUE` CLI arguments. It asserts that existing Debug and `EnableInternalTestHooks=true` defaults remain intact, while a Release override changes conditioned Compile items and project-node identity. It writes only beneath a new TEMP directory. The pinned Golden Release evaluation receipt is `CircuitWeave-main/artifacts/processor-20261114/hb-evaluator-globals-release-20261124/receipt.json`.

The fixture validates project evaluation only. It does not run targets or establish generated source, analyzer side effects, Csc input closure, or response-file consumption.

### Same-run Release source argument mapping audit

Pinned Windows RSP argv maps to the Release ProjectGraph inventory for all five Golden projects when the three MSBuild target-generated C# files per project are included; `/embed:` arguments are not source-file arguments. The 15 target-generated file snapshots, five `project.assets.json` hashes, per-project source counts and joined-evidence pins are recorded in Core `artifacts/processor-20261114/hb-evaluator-globals-release-20261124/receipt.json`. This remains mapping/snapshot evidence. Analyzer and generator-produced inputs, Csc read/consumption and full input closure remain open; no observation V2 is emitted.

## C# source argument mapper (2026-11-24)

`CscSourceArgumentMapper.csproj` is a standalone .NET 11 Windows utility. It validates pinned `evaluated-project-graph/v2` and copied RSP bytes, binds the selected project node to the observed cwd, parses with Win32 `CommandLineToArgvW`, and requires exact source-operand/Compile-set equality. It reports only a path mapping, not source hashes or Csc read/consumption. The synthetic fixture passes exact mapping and rejects stale graph/RSP pins and changed Compile membership. On the pinned Golden Release root task it refuses because the RSP includes target-generated `HybridCPU_ISE.GlobalUsings.g.cs` absent from evaluation-only Compile items; see Core receipt `artifacts/processor-20261114/hb-evaluator-globals-release-20261124/receipt.json`.

### External target-generated source inventory

The mapper accepts a strict externally SHA-pinned `hardwarebridge.csc-generated-source-inventory/v1` manifest bound to the evaluated graph SHA and per-row project/node identity. For the pinned Golden five-project Release run, 15 `AssemblyAttributes`, `AssemblyInfo` and `GlobalUsings` files were independently hashed, then all five RSP source sets matched Compile + generated rows (1,104 / 29 / 18 / 9 / 31). Snapshot map documents and receipt are in Core `artifacts/processor-20261114/hb-csc-source-map-golden-20261124/`. This is exact path-set mapping; ordinary source hashes, source-generator outputs, response consumption and full compiler closure remain unestablished.

### Windows/.NET source-byte snapshot — 2026-11-24

Added standalone C# `CscSourceByteSnapshotter` to bind the five pinned Release source maps, their exact source-map-set manifest, identical pre/post source manifests, and the pinned 15-row target-generated source inventory. It verifies current bytes before publishing an unqualified snapshot and refuses stale pins, changed files, path drift, missing rows, or malformed map membership. Build: `dotnet build HWIR/EvaluatedProjectGraph/TaskTimeObserver/CscSourceByteSnapshotter.csproj -c Release -p:BaseIntermediateOutputPath=<new TEMP> -p:OutputPath=<new TEMP>` under SDK `11.0.100`, 0 warnings/errors; assembly SHA-256 `66fc476a3a76918614919f08eac0091cca70557ed6f5a4f534d3f66582ef92ef`. Synthetic PowerShell fixture PASS (positive package plus five negative cases).

On Golden commit `6946ccab9d888f7dae0a4d8e18480f436138acd9`, tree `243a67c8ef462c1da2d1f06df9bee2c31508cb40`, isolated source clone `g6946-1279ce2a`, the C# utility matched all 1,176 mapped source bytes against pre/post manifest pin `752c3ed30ccdccce4e211df4e1c331cc13b7cc9bc218452d96a5694ff1d27e36`, plus all 15 generated sources against inventory pin `5fc5fd6d86548c6937b3a9ff8b078147ca717582fafe44e2490d800149864ff1`. Graph SHA `0b962bf78bad350dfa959ef30fdf3689a9c8f40257b7863fe352e1bce013576a`; source-map-set manifest SHA `b75c8dac1e85f9780bd71d06c2226453177c1294a6a6b45cd624f6999117be1c`.

Published snapshot: `CircuitWeave-main/artifacts/processor-20261114/hb-csc-source-byte-snapshot-golden-20261124/`; snapshot manifest SHA-256 `b6ffc1a4acc8515525699c88f1474b6d9aba0eccd1e86d0c6708eb232e26b362`; publication manifest SHA-256 `66da487a02c2677c2b8490ae20d79f3f836b2424132ba678fdcc9ddfc983f420`; receipt SHA-256 `0d0c6ef4d95c2663cd18a81170ca49e044a0407d4c4f409e5072423d90fc57eb`. All 1,201 published data/evidence files were rehashed after copy (12,632,592 bytes). Snapshot is current-byte inventory only: Csc read/response consumption, complete compiler input closure, provenance, independent admission, trust, RTL, and Processor verification remain NotEstablished/NotAdmitted. In particular, this does not replace the absent current-run analyzer/source-generator outputs or create V2 observation.

### Snapshot scope wording correction — 2026-11-24

Independent review found that the first snapshot manifest field `ordinaryCurrentBytesMatchPinnedManifest` could be read as covering every row in the 3,530-row repository manifest. The implementation verifies the 1,176 ordinary source rows selected by the five Csc maps. The C# claim field is now explicitly named `mappedOrdinarySourceBytesMatchPinnedManifest`; the fixture asserts this bounded claim. The v2 snapshot supersedes the earlier publication without overwriting it. Updated artifact: `CircuitWeave-main/artifacts/processor-20261114/hb-csc-source-byte-snapshot-golden-20261124-v2/`; snapshot manifest SHA-256 `b737a88524af791e5e715ccb87f14dcc6d9fbb31fe1ea9cc1c28d27513b7a55f`; publication manifest SHA-256 `30db8b5505d19c680133ab8fea9fa9bff1e397008eed0ba2267bfadb9ed5ae84`; receipt SHA-256 `362d0a7b1b98b09437f785bc4494e24eb3ece0ed3f9dffffbc670f8b0625b49d`. Final C# source SHA-256 `666e5437c9a497b2e6c360bede3380566a16713ff71410dc96739789e8b3bfe7`, fixture SHA-256 `628601c28b095f68837fbd55306f05c36a8470d5741e9a032b142bdb3e2324ee`, SDK 11.0.100 Release build 0 warnings/errors. All 1,201 published rows rehashed (12,632,597 bytes). Scope remains an unqualified current-byte snapshot; it does not establish Csc read/consumption or complete compiler closure.

## C# Windows task-event projector — 2026-11-24

The earlier `project_windows_task_events.py` paragraph documents the historical Python utility; the canonical Windows projection implementation is now the Bridge-owned .NET 11 `CscWindowsTaskEventProjector.csproj`. It accepts the same SHA-pinned task-event V1, uses `CommandLineToArgvW`, refuses unknown/duplicate JSON members and uncaptured response files, and emits byte-identical projection V1. Its 14-check fixture reproduced the saved Golden projection; receipt is in Core `artifacts/processor-20261114/hb-csharp-windows-task-projection-20261124/`. The Python implementation remains only a parity reference for this projection. This does not remove Python from the remaining R0 orchestration/observation V2 assembly and does not establish OS argv, Csc input reads, provenance, trust, or hardware qualification.

### C# Observation V2 assembler (2026-11-24)

`CscObservationV2Assembler.csproj` builds on .NET 11 and consumes SHA-pinned raw task-time evidence plus a pinned role selector. Frozen retry-11 input reconstructed the saved observation byte-for-byte (`17a5b815...`); an altered graph pin refused without creating output. See Core receipt `artifacts/processor-20261114/hb-csharp-observation-v2-assembler-20261124/receipt.json`. This is saved-evidence parity only. It does not show Csc read consumption or trusted/independent admission. Python capture orchestration remains transitional; Windows application runtime target is .NET/C#.

The producer remains standalone: main `EvaluatedProjectGraph` command wiring and a C# assembly-manifest producer are open. Frozen fixture used a prepared manifest. Do not treat this as a complete Windows runtime migration; Python orchestration remains transitional.

`EvaluatedProjectGraph assemble-csc-observation-v2` now invokes the assembler and passed frozen byte parity. The assembly manifest still comes from fixture preparation; live C# manifest production and full capture remain open.

The CLI now offers `prepare-csc-observation-v2-manifest` to derive the pinned manifest from graph/crosswalk/metadata/probe inputs. Frozen fixture manifest SHA `5b2d4fe8...` yielded byte-exact V2 output; a wrong primary role failed closed. A fresh Golden execution and independent Core admission remain open.

The V2 `owned-body` field is preserved for compatibility and only reflects ProjectReference graph reachability; independent Core call-graph ownership remains required. Generated-source root is now cross-bound to pinned build metadata in both prepare and assemble commands.

The assembled V2 observation was passed through the existing C# `capture-v2` writer and Core `HardwareBridgeCscCaptureImporterV2`/raw stager. Core passed 13 importer/staging checks with the exact pinned index/root, but admission remains `NotAdmitted`. See backlog `csharpCaptureV2IndependentCoreImport20261124`; this does not prove Csc reads or hardware behavior.

A new clean pinned clone was built on RC2 SDK with the C# task logger and Csc probe. Five task events/processes/RSPs succeeded, 50 generator outputs were captured, and all 3,530 tracked source hashes remained stable. Raw receipt is in Core `artifacts/processor-20261114/golden-windows-csharp-fresh-capture-20261124/`. Crosswalk production and independent Core import remain open.

Follow-up (2026-11-24): the main CLI now wires `produce-csc-execution-crosswalk-v1` to the C# producer. It requires externally pinned `HB_PINNED_GIT_EXE` and `HB_PINNED_GIT_SHA256`, rejects untracked and ignored non-`obj`/`bin` worktree inputs, compares evaluated project-reference edges and bounds aggregate file count/bytes. Fresh pinned-build crosswalk SHA `34ca2d16650fce43c242220515a3f5328f04cb48f78f7727e21cba0b9d38142a`; metadata SHA `bfdb087d138b379ab4c14524af0295d4b08efe2edbcfabbe315b6d042eccfabd`. The resulting observation/capture hashes are unchanged (`6c672f05...` / `26f9b193...`) and the Core import/stage passed 13 checks as CandidateOnly/NotAdmitted; the test was rerun from the published snapshot. Windows .NET 11 CLI Release build and help launch passed with 0 errors (3,119 warnings). Core also provides `eng/circuitweave.ps1`, a Windows PowerShell launcher requiring only .NET 11. Wrong facts/Git pins, untracked worktree input and generated-root overlap were refused. Fresh assembler replay retained exact observation SHA; legacy metadata schema remains accepted for historical fixtures. Receipt: `artifacts/processor-20261114/golden-windows-csharp-crosswalk-v2-20261124/receipt.json`, SHA-256 `64c37c6ab805e04af00655e22214865cc0f91f7f7ece1b186803ce5188db4a35`. Csc read-consumption, trusted handoff, RTL and Processor verification remain open.
