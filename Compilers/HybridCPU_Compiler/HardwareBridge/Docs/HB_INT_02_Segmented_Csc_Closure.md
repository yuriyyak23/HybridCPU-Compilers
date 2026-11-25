# HB-INT-02 — segmented Csc closure transport v2

> **Baseline 2026-11-25.** Capture can transport large unqualified source lists, including generated/analyzer artifacts if supplied and ordered by the capture contract. Canonical manifest ordering is separate from actual Csc argument ordering. Complete Processor and independent replay remain distinct gates. See [current status](current/STATUS.md).

**Owner:** HybridCPU-Compilers / HardwareBridge. **Seam:** S2. **Partner:** [CircuitWeave CW-INT-03](https://github.com/yuriyyak23/CircuitWeave/blob/master/docs/roadmap/circuitweave/12_CW_INT_Execution_Packages.md). **Status:** partial, unqualified software observation; full CPU closure and independent Core ingestion BLOCKED.

Baseline checked 2026-11-19: HybridCPU-Compilers master 94f59223b36659c9162620224f91349ac79fb896, Core master 555672232e46cb4e8b157e2d1b1db1e873c4194e. Planning [HB #4](https://github.com/yuriyyak23/HybridCPU-Compilers/pull/4) and [Core #1](https://github.com/yuriyyak23/CircuitWeave/pull/1) were merged; [HB-INT-01 PR #5](https://github.com/yuriyyak23/HybridCPU-Compilers/pull/5) was separately open. Golden source is never written.

## Producer and format

The independent Python standard-library producer HWIR/EvaluatedProjectGraph/segmented_csc_capture_v2.py consumes a **caller-supplied, isolated Csc observation** and the existing evaluation-only hardwarebridge.evaluated-project-graph/v2 graph. It does not invoke MSBuild, Csc, generators, native EDA or a target. It checks graph identity, **exact supplied Csc argv** and argvSha256, compiler-image bytes, supplied reference/analyzer/analyzer-config/additional-file byte pins, source maps and generator-emitted source bytes, project files, optional assets, and global properties. It compares every mapped evaluated Golden original with its isolated source byte-for-byte before capture; it rehashes all copied and original files afterwards.

Output: separate **hybridcpu.hardwarebridge.csc-closure-index/v2** index.json and binary chunk-00000000.bin etc. Each chunk <= 1 MiB. Each entry has ordered (nodeId,kind,logicalPath) identity, role, path, byte length, SHA-256 and sequential chunk lengths/hashes. closureRootSha256 hashes canonical JSON payload excluding the root field. Limits: 100 project nodes, 20,000 entries, 128 MiB per input, 8 GiB total, 16 MiB index, 8 MiB observation, 4 MiB graph. Processing of file bytes is bounded to 1 MiB blocks.

**Trust boundary:** All input identities, including asserted primary and owned-body source roles, are **unqualified software observations**; SHA-256 is NOT a signature or proof. PE references, compiler, analyzer/config, project and assets are ALWAYS metadata-only. The producer never infers executable ownership from a PE/type/PDB. The local verify command checks integrity only, not independent Roslyn source closure.

The verifier refuses unknown input kinds, unsupported project roles and an index
without a primary project. A graph must contain its root project when captured.
`verify --output <directory> --graph <original-graph.json>` additionally
checks the graph bytes against the captured graph SHA-256, matches every command
node and its global properties, and requires the primary role exactly for
nodes whose project equals the graph root. Multiple graph nodes may describe
the same root project with different global properties. The optional graph
check validates the captured relationship; it does not attest actual execution
or independently authorize source ownership.

## Observation input contract and transition

The caller must supply an externally observed full Csc argument vector from a **separately authorized, isolated build**, never fabricate an executable Csc invocation from MSBuild evaluation or its -getItem/-getProperty API.

Minimal illustrative v2 observation shape (hash placeholders are not runnable receipts):

~~~json
{
  "schema": "hybridcpu.hardwarebridge.csc-observation/v2",
  "graphSha256": "<sha256 graph bytes>",
  "projects": [{
    "nodeId": "<exact evaluated graph node id>",
    "role": "primary",
    "workingDirectory": "<absolute isolated directory>",
    "projectSha256": "<sha256 original project bytes>",
    "compiler": {"path": "<absolute compiler image>", "sha256": "<sha256>"},
    "argv": ["/target:library", "/langversion:preview", "/define:FEATURE", "/nullable:enable", "/out:obj/P.dll", "/generatedfilesout:<absolute generator dir>", "<absolute isolated source>.cs"],
    "argvSha256": "<sha256 of canonical JSON argv string array>",
    "sources": [{
      "path": "<absolute isolated source>",
      "logicalPath": "project/A.cs",
      "sha256": "<sha256>",
      "origin": "evaluated",
      "originalPath": "<absolute matching Golden Compile file>"
    }],
    "generatorSources": [{
      "path": "<absolute generator-emitted .cs file>",
      "logicalPath": "generated/Generator/Hint.cs",
      "sha256": "<sha256>"
    }],
    "filePins": []
  }]
}
~~~

For each graph node there must be exactly one observed Csc entry. The root project is primary; another project's role must be explicitly owned-body or metadata-only (NEITHER establishes actual hardware ownership). Each Csc source argv must be mapped. Every evaluated Compile item must map to one original and have identical bytes; additional command-line sources must use origin msbuild-generated and no originalPath. All .cs files under the observed generatedfilesout directory must be declared with unique logical paths. For file switches /reference:, /analyzer:, /analyzerconfig:, /additionalfile:, filePins entries are required with kind, exact absolute path and sha256. The argvSha256 input is SHA-256 over canonical UTF-8 JSON of the array (compact separators, ASCII escapes). Global properties come from the bounded graph.

Unknown/ambiguous versions, response files, aliases, resources/link/key inputs, missing generator outputs, changed defines/options, stale PE hashes and linked inputs are refused. All Csc switches are preserved in argv; not every possible switch is independently classified. A captured command is **not** a verified executed command. The existing audit_csc_log.py output cannot substitute for full argv; migration requires an approved true Csc-task observer.

## Commands

~~~powershell
python Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/segmented_csc_capture_v2.py capture --graph "<absolute graph.json>" --observation "<absolute observation.json>" --golden-root "<absolute read-only Golden root>" --output "<new direct TEMP child>"
python Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/segmented_csc_capture_v2.py verify --output "<existing TEMP directory>"
python -m unittest -v Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/test_segmented_csc_capture_v2.py
~~~

Capture requires an exclusively new **direct child of OS TEMP**; on exception partial unqualified output may remain. Exit 0 with root digest means only a local unqualified byte-transport result. Invalid inputs exit 2. For clean pinned Golden commit versus dirty worktree use two independent observed captures and compare **actual captured source hashes**; HEAD alone is NOT byte identity.

## Compatibility and blockers

BridgeSourceSnapshotV1 (hybridcpu.hardwarebridge.source-snapshot/v1; 128 docs/refs, 64 MiB) and sidecar v1 (16 MiB), Companion facts and Golden oracle **are unchanged**. v2 is an optional outer record, not a giant replacement JSON. Core CW-INT-03 must independently implement a strict versioned **v2 importer/rehash verifier**; do not install this producer as a Core evidence gate.

BLOCKED: independent Csc task/process provenance, generator execution and completeness attestation, actual approved isolated Processor Debug/Release and clean/dirty captures, full exact Csc closure, Core reader CW-INT-03, downstream CW-INT-04 independent ownership, RTL acceptance and physical signoff. NotRun: real .NET 11 / MSBuild / Csc / Golden Processor and EDA runs on a host without dotnet or approved tool pins. The included fixture tests exercise **transport only**; they are not real compiler/CPU outputs.

Filesystem pre/post rehash does not prevent every adversarial concurrent rename (notably Windows reparse races). SIGKILL can leave partial TEMP output; never trust partial folders. Do not execute any unapproved target in Golden.

## R1-02 hardening (21 November 2026)

The `hybridcpu.hardwarebridge.csc-closure-index/v2` wire contract,
its unqualified disposition and the 1 MiB binary chunk limit are retained.
Reads now use bounded descriptor-based full-byte SHA-256 hashing and test
device/inode, mode, size, mtime, ctime and the pathname both before and after
each read. Lexical traversal, noncanonical absolute paths, symlink/reparse
ancestors, copied-source hardlinks to Golden originals, duplicate/case-folded
paths, duplicate JSON members and NaN/Infinity are refused. The filename
does not enter the digest. This catches observed concurrent mutation, not a
hostile mutation hidden by the filesystem; immutable read-only mounts/ACLs
and reviewed OS isolation remain host/operator prerequisites.

The producer's own byte consistency, an observed Csc event, a process exit
of zero, and a matching digest cannot qualify source method ownership.
The separate paired-CLI external task crosswalk is a **producer-side
structural precondition only**; its genuine event source and independent
review are outside this owner's files.

## R1-02 Windows Python 3.12 path/handle identity regression (post-merge #29)

Observed R1-02 own fixture CI on merged PR #29:
[run 44838110045](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44838110045)
had Linux synthetic PASS but Windows synthetic FAIL (49 errors/failures, mostly
`file replaced between check and open`). The follow-up diagnostic
[run 44854407920](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44854407920)
showed unequal `st_ctime_ns` across Windows `Path.lstat()` and `os.fstat()`
for newly created synthetic source/graph/observation bytes even when
`st_dev`, `st_ino`, `st_mode`, `st_size` and `st_mtime_ns` matched.

The R1-02 reader now binds a Windows pathname to its opened descriptor by
nonzero OS file ID, volume ID, file type, size and modification time. It
**does not compare Windows ctime across the different API namespaces**.
Instead it preserves the entire initial pathname stat and the entire initial
descriptor stat, independently checks **both complete states** after read,
and rechecks path/descriptor identity after read. POSIX retains the original
all-field cross-handle comparison. A missing Windows file ID, changed
descriptor/path metadata, changed content hash or renamed/reparse ancestor
still refuses input; this is not a fallback to hash-only acceptance.

The synthetic test gates cover newly-created graph/observation files, file ID
replacement, modification-time drift and same-size mid-read restoration.
No versioned closure JSONL wire bytes change. The fixture regression
does **not** prove the source mount cannot be modified adversarially;
Windows host-level read-only snapshot/ACL isolation is still required.
The genuine R1-01 task-time/rsp-open-read witness, R0-01 Companion source
facts, separately approved exact Golden clean/dirty SHA + worktree snapshots,
and Core independent Roslyn acceptance are external **Blocked/NotRun**
dependencies. Neither the synthetic fix nor a zero exit promotes Candidate,
Observed, or Unqualified into Verified or AcceptedRTL.

On failed capture the TEMP output may remain, including chunks or even an
unqualified index written before a later validation failed. Capture does not
recursively delete caller-named paths: a concurrent actor may have replaced
the directory. Do not treat retained bytes as a successful capture. Inspect
ownership before manual cleanup. This change does not establish a secure
snapshot or protect against adversarial concurrent filesystem changes.

The deprecated `project_csc_to_core_v2.py` and staged `stage_core_v2.py` also retain failed TEMP work instead of recursively deleting paths that another actor could have replaced. A failed legacy projection may leave even an index; never use a directory from a failed command as a completed projection. The staged path is moved to its final target only after validation succeeds. These measures do not establish an atomic snapshot or prevent adversarial path replacement during operation; inspect ownership before manual cleanup.


### 2026-11-22 — Windows actual Csc launch and response-backed closure

A disposable pinned `CscToolExe` launch shim observed five child Csc runs in a separate isolated checkout of Golden commit `6946ccab9d888f7dae0a4d8e18480f436138acd9` (tree `243a67c8ef462c1da2d1f06df9bee2c31508cb40`), SDK `11.0.100-rc.2.26504.105`, Release. Each response file was copied and held read-only through child exit; five task events crosswalked to five child PIDs, all exit 0, and response arguments matched expanded task arguments. Same-build evaluated Compile facts and Csc source token sets match 1,191/1,191. Full Bridge producer capture contains 2,218 rows / 86,594,404 bytes with closure `0046f8dde1a242beb95e2c587c4f7d96e54ee20b17d2b33ae9d9415bddc271dc` and index SHA `6cb4dc2e4fb15cebb8a3a629ec6521c5535423c9970ea88fe34f7fccdfda6f4e`; full evidence and limitations are in Core `artifacts/processor-20261114/golden-windows-csc-cwd-observation-20261122/retry-11-short-gen-path/csc-execution-crosswalk.json` and `golden-windows-csc-capture-20261122/capture-v2`. Direct Csc rebuild matched 44/44 instrumented PE/PDB hashes. This does not establish compiler read buffers or analyzer side effects, and it is not Core admission/trusted handoff. Stage/Core verification remains blocked pending a separately owner-approved exact Processor root-selector manifest.


The post-slice Bridge discovery run passed: `python -m unittest discover -v`, 177 tests, 12 platform-dependent skips, exit 0, Bridge HEAD `e0fdfdebd537ecfbd67b5fd853940f061a5c41ae`. Receipt (historical receipt ID `artifacts/processor-20261114/bridge-hbint02-discovery-windows-capture-20261122.receipt.json`, unavailable for this review), log SHA-256 `0de90f4d2357d1c2c3540bf548662904ade03b90eeac7ad699fe4739cc3b096c`. This is local producer regression evidence only; hosted CI, independent Core admission and hardware qualification remain open.


**Graph provenance correction:** the pinned graph manifest uses values captured by an injected `BeforeTargets=CoreCompile` target in the observed build; this was not a separate MSBuild `ProjectGraph` evaluation. `compileFiles` contains 1,176 Golden-origin inputs. The other 15 Csc source operands are build-generated `obj` files marked separately as `msbuild-generated`; the crosswalk verifies all 1,191 target Compile items against the response-derived Csc source set. Fixture test success, same-build target evidence and instrumented OS child execution remain separate evidence classes.

### 2026-11-24 correction — C# Golden raw handoff independently imported by Core

The historical pinned Windows capture above is now replayed by the additive .NET 11 `SegmentedCscCaptureV2` writer. Its index SHA/root match the saved Python producer exactly (`6cb4dc2e4fb15cebb8a3a629ec6521c5535423c9970ea88fe34f7fccdfda6f4e` / `0046f8dde1a242beb95e2c587c4f7d96e54ee20b17d2b33ae9d9415bddc271dc`). Core independently imported and staged the published C# snapshot with 13 checks, 2,218 rows, 2,241 chunks and 86,594,404 bytes; raw import remains `UnqualifiedByteInventory / NotAdmitted`, staged result `CandidateOnly / NotAdmitted`. This supersedes only the previous “no independent Core reader/import receipt” statement for this historical bundle. It does not prove response consumption/read buffers, trust, approved roots, source admission or hardware behavior. Full evidence: Core `artifacts/processor-20261114/hb-v2-csharp-golden-replay-20261124/receipt.json`, SHA-256 `8be85cc6d0594b353ec2ad9bdad1cd86ae988d7fad2958a2a5db66d41c6712b5`. The latest 2026-11-24 run still requires a pinned target-generated input role before current-run mapping can pass.
