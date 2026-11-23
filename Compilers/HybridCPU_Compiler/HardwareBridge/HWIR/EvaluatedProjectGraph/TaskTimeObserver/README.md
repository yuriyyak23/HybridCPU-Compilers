# HB R0: opt-in MSBuild Csc task-time observer (approved toy fixture)

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

A separate clean TEMP clone of the fixed Golden commit built successfully on Ubuntu WSL with the pinned RC2 SDK. The VMX projection-lineage PowerShell verifier succeeded separately on Windows; the Linux build disabled only its Windows-shell MSBuild target. The task logger recorded five successful project events and `strace` observed five Csc processes. A Linux inotify watcher copied each response file on `IN_CLOSE_WRITE`; all five copied bytes match the recorded lengths and SHA-256 digests. The main Csc PID 676 exec argv names the same response path as `005.rsp`, within the main project's task interval; its 124,246 bytes hash to `0c4cae5ac1aca2cdb6b6df1c7e7ab55fe98a773927760b568c563db3d0d17b74` and the process exited 0. [Receipt and immutable artifacts](../../../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-linux-csc-rsp-observation-20261122/receipt.json).

This is unqualified Linux observation, not a Windows OS-process/read-buffer witness or complete compile-time input closure. A separate Windows task-level build result is recorded below; source/reference/analyzer byte capture, generator outputs, independent Core admission, and trusted handoff remain open. It does not relax the R1-01 toy-only fixture gate or its refusal behavior.

### 2026-11-22 — Csc source bytes reconstructed from process reads

The exact main response file names 1,104 distinct `.cs` inputs. A bounded audit of the Csc process/thread tree (`PID 676`, 67 trace files) reconstructed complete `read`/`pread64` buffers for all 1,104 paths: 8,978,960 bytes total. Every reconstructed digest matches the corresponding post-build byte in the clean pinned Golden clone; the normalized response-file source path set equals the captured manifest set (1,104/1,104). Source archive and thread trace archive verification passed. [Receipt, source manifest, source bytes, raw traces, and validation](../../../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-linux-csc-source-read-observation-20261122/receipt.json).

Classification remains unqualified Linux observation. The package does not establish the 180 reference bytes, analyzer/config inputs, embedded resources, in-memory generator output, independent Core admission, Windows production execution, or trusted handoff.


## Windows Golden task execution and cross-runtime source/IL check (2026-11-22)

A Release build of the same clean pinned Golden clone succeeded on Windows and recorded five successful live MSBuild Csc task events. The main task projection contains 1,104 source, 180 reference, 10 analyzer, 2 analyzer-config and 3 embedded arguments; no `@response` token is present. The task event does not record Csc cwd or prove OS process/read-buffer bytes. Windows source bytes are post-build snapshots resolved against the build invocation directory, not Csc read-buffer evidence. Its source path set matches the Linux response manifest. Of 1,104 archived source files, 1,102 hashes match exactly; two generated `.cs` files differ only by CRLF/LF, while all normalized source text matches. Linux rebuild succeeded. A bounded PEReader comparison found 30,561 matching method definitions with no differences in method keys, raw signature blobs, raw IL byte streams, or `MethodAttributes`/`MethodImplAttributes` flags. Custom attributes, body headers, local signatures, exception regions, type/assembly metadata, and full PE/PDB semantics were not compared. PE/PDB and the two installed `csc.dll` files have different SHA-256 values. [Windows task observation receipt and artifacts](../../../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-windows-csc-task-observation-20261122/attempt-03/receipt.json).

This remains task-level software evidence. It does not establish Windows Csc process/read-buffer capture, complete external input bytes, analyzer side-effect closure, independent Core admission, trust or hardware verification.

## Windows five-process task/RSP bundle adapter (2026-11-22)

`windows_task_rsp_bundle_v1.py` validates an externally pinned five-event/five-process/five-response bundle. It rechecks each compiler image path/SHA, task/process/project/PID/RSP identity, response copy length/SHA, and ordered tokens from the copied response bytes against the matching successful MSBuild task command using Win32 `CommandLineToArgvW`. The dedicated `test_windows_task_rsp_bundle_v1.py` suite uses synthetic fixtures only. Positive validation of the current Windows observation writes only to TEMP; it is not a Core staging/import run.

The adapter keeps the evidence unqualified: Win32 process sampling is not a complete kernel trace; copying a response file while the process is present does not prove Csc consumed those bytes; `CommandLineToArgvW` token equality is not a Roslyn response-parser/read-buffer proof; cwd remains `NotRecorded`; input-byte closure, provenance, independent Core admission, trust and hardware verification are not established. It does not change the existing POSIX-oriented V2 capture/stager or the toy-only R1 gate.
