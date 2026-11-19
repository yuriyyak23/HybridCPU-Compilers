# HB-INT-02 — segmented Csc closure transport v2

**Owner:** HybridCPU-Compilers / HardwareBridge. **Seam:** S2. **Partner:** [CircuitWeave CW-INT-03](https://github.com/yuriyyak23/CircuitWeave/blob/master/docs/roadmap/circuitweave/12_CW_INT_Execution_Packages.md). **Status:** partial, unqualified software observation; full CPU closure and independent Core ingestion BLOCKED.

Baseline checked 2026-11-19: HybridCPU-Compilers master 94f59223b36659c9162620224f91349ac79fb896, Core master 555672232e46cb4e8b157e2d1b1db1e873c4194e. Planning [HB #4](https://github.com/yuriyyak23/HybridCPU-Compilers/pull/4) and [Core #1](https://github.com/yuriyyak23/CircuitWeave/pull/1) were merged; [HB-INT-01 PR #5](https://github.com/yuriyyak23/HybridCPU-Compilers/pull/5) was separately open. Golden source is never written.

## Producer and format

The independent Python standard-library producer HWIR/EvaluatedProjectGraph/segmented_csc_capture_v2.py consumes a **caller-supplied, isolated Csc observation** and the existing evaluation-only hardwarebridge.evaluated-project-graph/v2 graph. It does not invoke MSBuild, Csc, generators, native EDA or a target. It checks graph identity, **exact supplied Csc argv** and argvSha256, compiler-image bytes, supplied reference/analyzer/analyzer-config/additional-file byte pins, source maps and generator-emitted source bytes, project files, optional assets, and global properties. It compares every mapped evaluated Golden original with its isolated source byte-for-byte before capture; it rehashes all copied and original files afterwards.

Output: separate **hybridcpu.hardwarebridge.csc-closure-index/v2** index.json and binary chunk-00000000.bin etc. Each chunk <= 1 MiB. Each entry has ordered (nodeId,kind,logicalPath) identity, role, path, byte length, SHA-256 and sequential chunk lengths/hashes. closureRootSha256 hashes canonical JSON payload excluding the root field. Limits: 100 project nodes, 20,000 entries, 128 MiB per input, 8 GiB total, 16 MiB index, 8 MiB observation, 4 MiB graph. Processing of file bytes is bounded to 1 MiB blocks.

**Trust boundary:** All input identities, including asserted primary and owned-body source roles, are **unqualified software observations**; SHA-256 is NOT a signature or proof. PE references, compiler, analyzer/config, project and assets are ALWAYS metadata-only. The producer never infers executable ownership from a PE/type/PDB. The local verify command checks integrity only, not independent Roslyn source closure.

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

Capture requires an exclusively new **direct child of OS TEMP**; on exception partial output is removed. Exit 0 with root digest means only a local unqualified byte-transport result. Invalid inputs exit 2. For clean pinned Golden commit versus dirty worktree use two independent observed captures and compare **actual captured source hashes**; HEAD alone is NOT byte identity.

## Compatibility and blockers

BridgeSourceSnapshotV1 (hybridcpu.hardwarebridge.source-snapshot/v1; 128 docs/refs, 64 MiB) and sidecar v1 (16 MiB), Companion facts and Golden oracle **are unchanged**. v2 is an optional outer record, not a giant replacement JSON. Core CW-INT-03 must independently implement a strict versioned **v2 importer/rehash verifier**; do not install this producer as a Core evidence gate.

BLOCKED: independent Csc task/process provenance, generator execution and completeness attestation, actual approved isolated Processor Debug/Release and clean/dirty captures, full exact Csc closure, Core reader CW-INT-03, downstream CW-INT-04 independent ownership, RTL acceptance and physical signoff. NotRun: real .NET 11 / MSBuild / Csc / Golden Processor and EDA runs on a host without dotnet or approved tool pins. The included fixture tests exercise **transport only**; they are not real compiler/CPU outputs.

Filesystem pre/post rehash does not prevent every adversarial concurrent rename (notably Windows reparse races). SIGKILL can leave partial TEMP output; never trust partial folders. Do not execute any unapproved target in Golden.