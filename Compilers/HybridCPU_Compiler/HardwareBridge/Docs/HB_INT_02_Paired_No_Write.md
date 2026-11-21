# HB-INT-02 R1-02 — paired no-write clean/dirty segmented capture

**Scope:** additive opt-in orchestration in HardwareBridge. Neither a new C# compiler
nor an executed-Csc attestation. The existing `csc-closure-index/v2`, sidecar/v1,
CW-INT-03 consumer and Golden project files are unchanged.

## Current boundary (20 November 2026)

The production `segmented_csc_capture_v2.py` checks exact caller-provided v2
graph/observation, source/generator/reference/analyzer/project/assets pins and
argv hashes, but does **not** prove the task ran. The separately developed
R1-01 MSBuild task-time logger branch exposes
`hybridcpu.hardwarebridge.msbuild-csc-task-events/v1` with task events and a
raw `CommandLine` string. That format is **not** an accepted, independently
replayable `argv[]` plus all source/file pin relation. Do not parse the
command line heuristically and claim a Csc execution. Until a versioned,
reviewed R1-01 converter/attestation is available, **executed Csc = Blocked**.
The 2026-11 P16 Debug 6-project / 1436-input / 50-generator observation is
historical context, not a tested HB-INT-02 golden capture.

## Authorized opt-in procedure

1. The owner explicitly authorizes two independent, *immutable read-only*
   Golden source snapshots: (a) clean pinned commit with exact HEAD and
   clean worktree, (b) separately frozen dirty overlay with its own complete
   worktree files/bytes and HEAD. Snapshot creation/approval is outside this
   tool. Do **not** run checkout, restore, generated files or build targets
   in the original Golden tree. Never synthesize dirty bytes from HEAD.
   Verify the mounts/ACLs are read-only at the host level.
2. In a new, separately authorized isolated TEMP build workspace for **each**
   snapshot, run the externally approved R1-01 software compiler capture
   (when it is implemented/reviewed) and independently produce
   `hardwarebridge.evaluated-project-graph/v2` and
   `hybridcpu.hardwarebridge.csc-observation/v2` **for that snapshot**.
   The observation must cover each evaluated node and actual Csc source,
   msbuild-generated input, emitted generator `.cs`, project, reference,
   analyzer/config, assets and full compiler switches; independently bind
   tool identities and the executed task event to the exact expanded argv.
   Absence/ambiguity => **Blocked**, not a fixture substitution. Keep
   restore/build, Csc and generated output entirely in new TEMP.
3. After the build has completed, pass the two independently captured graphs,
   observations and frozen read-only snapshot roots to the pair runner.
   It checks each root's complete file-byte inventory before and after both
   captures and receipt publication, along with exact Git HEAD and
   `git status --porcelain=v1 -uall` digest using `GIT_OPTIONAL_LOCKS=0`.
   The sole enumerated exception is `.git` *administrative metadata*, which
   is tracked via Git HEAD/status but is not software source content.
   Any symlink/reparse/linked ancestor, missing source, changed file, invalid
   schema/hash/role, nonunique path, unknown switch or timeout is refused.
4. Pick **three new, distinct, nonexistent direct children of OS TEMP**:
   clean closure, dirty closure, and private receipt. The runner writes only
   there; no Golden path is ever opened for write. The local receipt records
   exact Git commit, source inventory SHA, Git status SHA, observed argv digest,
   ordered raw chunk SHA list, root digest and output workspace paths for each
   byte set. Do NOT publish the local receipt, chunks, source paths or source
   bytes as general CI artifacts. Only publish test log exit/hash and sanitized
   summary metrics.
5. Keep the two closures and receipts unqualified. A SHA, green synthetic
   test, Git status, or Csc task event cannot confer Core/RTL verification.
   For a real gate also require independently reviewed R1-01 execution
   evidence and Core-owned rehash/reader; otherwise report **Blocked**.

Example (run only with the owner-approved frozen snapshot inputs and genuinely
observed v2 input files; placeholders are not runnable evidence):

```bash
python3 Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/paired_csc_no_write.py \
  --clean-root "$CLEAN_READONLY_ROOT" --dirty-root "$DIRTY_READONLY_ROOT" \
  --clean-graph "$CLEAN_EVALUATED_GRAPH" --dirty-graph "$DIRTY_EVALUATED_GRAPH" \
  --clean-observation "$CLEAN_EXECUTED_CSC_OBSERVATION" \
  --dirty-observation "$DIRTY_EXECUTED_CSC_OBSERVATION" \
  --clean-commit "$PINNED_CLEAN_COMMIT" --dirty-commit "$PINNED_DIRTY_BASE_COMMIT" \
  --clean-output "$TMPDIR/hb02-clean-UNIQUE" \
  --dirty-output "$TMPDIR/hb02-dirty-UNIQUE" \
  --receipt-output "$TMPDIR/hb02-receipt-UNIQUE"
```

The CLI requires Git on the host, explicitly pinned exact 40-character lowercase
commit IDs and readable Git status for both snapshots; snapshots may share
the **same** commit while having different content. Outputs must not exist
before invocation. Exit `0` proves only unqualified byte-transport. Exit `2`
denotes a typed refusal or missing prerequisite. Interrupted capture removes
any created TEMP outputs, subject to SIGKILL/platform limitations.

## Resource limits and negative gates

| Boundary | Behavior |
| --- | --- |
| 1 MiB per HB02 binary chunk | ordered raw SHA, full replay |
| 128 MiB/file, 20,000 rows, 8 GiB total HB02 | reject oversize, no truncation |
| 20,000 files / 8 GiB per snapshot inventory | bounded streaming read; refuse oversize |
| 8192 Core transition rows | typed refusal before claiming Core compatibility |
| Same byte inventory or closure root for clean/dirty | refuse rather than assert overlay |
| Missing/changed generator, reference, project, source or command input | refuse |
| Duplicate logical identity/JSON member, wrong originalPath/role, symlink | refuse |
| Drift after first capture or after receipt publish | remove partial outputs |
| Cancellation / exception | remove owned TEMP output directories |

Run fixtures (these are **not** an executed compiler):

```bash
python3 -m unittest -v Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/test_segmented_csc_capture_v2.py Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/test_paired_csc_no_write.py
```

This exercises 1001 source files *per* synthetic snapshot plus mutation cases
without collecting Golden content. Git status is also tested against two
synthetic repositories with the same HEAD but a modified dirty overlay.
The dedicated `hb-int-02-paired-no-write.yml` job publishes only synthetic
test logs with raw exit and SHA-256. Genuine clean/dirty Golden capture,
R1-01 task-time-to-argv linkage, Core independent acceptance and full
Processor closure remain **Blocked/NotRun** until separately authorized.


## R1-02 external task witness (fail-closed production CLI)

The production paired CLI now additionally requires:
`--clean-witness`, `--dirty-witness`,
`--clean-witness-sha256` and `--dirty-witness-sha256`.
Each lowercase reviewed SHA-256 must cover the **exact bytes** of a
separately supplied strict JSON file:

```json
{"schema":"hybridcpu.hardwarebridge.r1-02-external-task-crosswalk/v1",
 "graphSha256":"<SHA256 of raw graph>",
 "observationSha256":"<SHA256 of raw v2 Csc observation>",
 "events":[{"nodeId":"<evaluated node id>",
 "argvSha256":"<SHA256 of canonical argv>",
 "compilerSha256":"<Csc executable SHA256>",
 "workingDirectory":"<isolated absolute cwd>",
 "eventSha256":"<SHA256 of externally recorded R1-01 task event bytes>",
 "stdoutSha256":"<SHA256 of executed output bytes>",
 "stderrSha256":"<SHA256 of executed error bytes>",
 "eventFile":"<absolute TEMP raw task event file>",
 "stdoutFile":"<absolute TEMP raw stdout file>",
 "stderrFile":"<absolute TEMP raw stderr file>",
 "exitCode":0,"eventSource":"R1-01"}]}
```

Every observed project must match exactly one external event identity,
Csc argv SHA, compiler image SHA and isolated working directory. Unknown
schema/fields, duplicate members/events, changed witness bytes, nonzero
exit, missing event coverage or absent reviewed pins => Blocked (exit 2).
This crosswalk is **not an independently verified process attestation**:
the R1-01 owner must supply actual immutable true task/process event bytes,
the exact argv expansion linkage and the reviewed provenance of
`eventSha256` / stdout / stderr. Synthetic test fixtures only check the
validator and MUST NOT be treated as actual Csc execution.

The CLI uses `require_git=True` and `require_witness=True` and refuses
a clean snapshot with nonempty `git status --porcelain=v1 -uall`, or a
dirty snapshot without visible Git changes; both are additionally pinned
by complete before/after source-file byte inventories. Dirty content is
never inferred from HEAD. Combined Core v2 transition is capped at 8192
rows and 512 MiB; the existing 20,000-entry/8 GiB HB02 transport itself
is unchanged. The Python `pair(require_witness=False)` fixture API stays
for synthetic transport tests, not as a production certification path.
The resulting receipt remains unqualified and its `executionGate`
remains Blocked; Core-owned source qualification is NotRun here.

Dependency: R1-01 must provide an actual recorded true Csc event plus
immutable reviewed witness SHA and contract mapping; R2-01/Core must
independently accept/re-derive any future strengthened wire version.
No R1-01, R2-01, Golden, Core or shared roadmap files are edited.

The event, stdout and stderr paths must already exist under OS TEMP; each
is opened read-only and full-byte hashed against its declared SHA, with
pre/post file identity checks. No actual event file has been approved or
provided in this R1-02 PR. The new refusal JSON
`hybridcpu.hardwarebridge.r1-02-refusal/v1` exposes a stable
`reasonCode` and `reasonDigestSha256`, without dumping private paths,
source bytes, tokens or PDBs to CI logs.

## R1-02 residual inventory DoS guard after merge #29

The complete Git clean/dirty byte inventory now caps **directories as well
as files**, with 20,000 visited directories maximum (including root) and a
300-second monotonic wall clock limit per inventory. A folder-only tree used
to evade the file-count cap is refused, as is a capture that expires while
enumerating files. No partial inventory digest is accepted. Existing 20,000
files / 8 GiB, per-file descriptor byte hashing, Git HEAD/porcelain and
before/after comparisons remain mandatory. Synthetic mutation tests shrink
the budgets instead of generating resource-intensive hostile trees. The
real Golden gate is still Blocked/NotRun without a separately approved,
immutable source/worktree identity and actual R1-01 OS witness.
