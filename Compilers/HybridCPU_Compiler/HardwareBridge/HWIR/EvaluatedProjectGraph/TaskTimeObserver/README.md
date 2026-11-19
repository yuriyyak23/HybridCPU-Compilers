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

SDK: \`11.0.100-rc.2.26504.105\`. Linux Ubuntu 24.04 + Python 3.13.
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
