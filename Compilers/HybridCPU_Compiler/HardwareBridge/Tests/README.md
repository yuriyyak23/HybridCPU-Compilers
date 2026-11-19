# Self-tests (isolated)

Run from a workspace containing the repository's existing .NET 11 SDK and Roslyn/CIL prerequisites:

```sh
dotnet --info
dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.SelfTests.csproj -c Release
dotnet build Compilers/HybridCPU_Compiler/HardwareBridge/HardwareBridge.Companion.csproj -c Release
```

The zero-external-test-package runner also invokes `BridgeIndexV2Tests.Run`: v1→v2 deterministic fixture, Core-compatible sorted JSONL/chunk checks, 1,200 synthetic source rows, duplicate/unknown/deep JSON, missing/truncated chunk, changed Csc/PE/PDB/source/role/SDK, cancellation and malformed path. Synthetic byte pins are explicitly test-only, not evidence of an actual Csc/Golden capture. See `Docs/HB_INT_03_V2_Interchange.md`. The runner tests deterministic bytes, defensive copy, duplicate/traversal/empty identities, reference roles, budgets, cancellation, malformed/stale/tampered sidecar, exact signed bit patterns, overflow, upper 32 bits, unknown/untrusted names, throwing delegate and a real **compile-only** Companion/CIL AddWord fixture with PE/PDB/closure rejection. It never executes the newly emitted assembly. The runner reads the explicitly known test-runtime reference via `typeof(object).Assembly.Location`; production bridge code never opens a file.

`BridgeSelfTests.cs` is fully `#if HARDWAREBRIDGE_TESTS`-guarded. Only this test csproj defines that symbol, and only the opt-in Companion csproj defines `HARDWAREBRIDGE_COMPANION`. `Directory.Build.props` redirects generated metadata to the parent project's normally excluded `obj/` and `bin/` paths, so the main project's recursive compilation does not ingest nested generated `.cs` files.

If the SDK/runtime/reference closure is unavailable, record tests as **NOT RUN**, not PASS.

## HB-INT-06: pinned Windows / .NET 11 source-no-write contract QA

Full [versioned integration matrix, limits, migration and blocked gates](../Docs/HB_INT_06_QA_Matrix.md).
From **PowerShell 7.3+ on Windows** with git and the pinned .NET SDK
**11.0.100-rc.2.26504.105**, use independent checkouts and an entirely
**new output directory outside all source repos**. Example (edit the root
paths for your environment):


    $bridge = 'C:\work\HybridCPU-Compilers'
    $golden = 'C:\work\HybridCPU-v2'
    $output = Join-Path $env:TEMP 'hb-int-06-new-run'
    pwsh -NoProfile -File "$bridge\Compilers\HybridCPU_Compiler\HardwareBridge\Tests\Run-HbInt06.ps1" -BridgeRoot $bridge -ExpectedBridgeSha (git -C $bridge rev-parse HEAD) -GoldenRoot $golden -ExpectedGoldenSha 6946ccab9d888f7dae0a4d8e18480f436138acd9 -ExpectedSdkVersion 11.0.100-rc.2.26504.105 -OutputRoot $output
    Write-Host "HB-INT-06 exit code: $LASTEXITCODE"
    Get-FileHash "$output\hb-int-06-qa-receipt.json" -Algorithm SHA256

The runner requires a real Git checkout of Golden at the pinned HEAD and
checks every present non-.git file SHA and Git status before/after, including
untracked/generated files. It rejects linked source and writes only under
the fresh output directory. Actual dotnet and pwsh argv, executable digest,
exit code and stdout/stderr SHA are recorded in the JSON report; raw logs
are saved alongside it. Missing SDK is **NotRun** (exit 2), wrong SDK/commit,
mutated source, command failure or timeout is nonzero. **0 means standalone
QA only**, not full HB-INT-06 completion, no Core acceptance and no RTL proof.

CI: [.github/workflows/hb-int-06-no-write.yml](../../../../.github/workflows/hb-int-06-no-write.yml)
runs on Windows and pins Golden 6946ccab9d888f7dae0a4d8e18480f436138acd9.
The Bridge checkout uses the **exact PR commit**, not a stale documentation
SHA. CI uploads the raw receipt even on failure.

For **paired Core importer** tests only after CW-INT-02/03 publish a real
owned test project, supply Core root, its exact pinned SHA and a safe
repo-relative importer project with the RequirePaired switch. For example,
the additional arguments are:

    -CoreRoot 'C:\work\CircuitWeave' -ExpectedCoreSha 555672232e46cb4e8b157e2d1b1db1e873c4194e -CoreImporterProject '<actual-Core-owned-test.csproj>' -RequirePaired

The placeholder is intentionally **not an implemented project**. The
optional paired workflow is disabled until both the pin and project path
are explicitly reviewed. Missing v2 segmented producer, actual CPU Csc
closure, Core independent source verifier, native tool approvals and
downstream RTL/physical receipts remain **Blocked/NotRun**, never PASS.

**Separate isolated protocol host:** `Tests/HardwareBridge.ProtocolTests.csproj` compiles the actual unchanged Bridge v1 codec/source/facts/oracle implementations without Roslyn/CIL dependencies. It runs before `HardwareBridge.SelfTests.csproj`. Both are required to be green for standalone QA; the isolated host must **not** be used to waive an upstream failing `HybridCPU.Compiler.Core` build or the real Csc PE/PDB gate. An external `global.json` locks the exact prerelease SDK because Windows hosted runners may also carry a newer GA SDK. Raw logs and exit states from both hosts are kept separately.

## HB-INT-04 raw trace (non-executable fixture)

The same standalone runner now executes `GoldenRawTraceTests.Run()` before the original scalar/Companion tests. It checks raw source/assembly digest binding, original-bit signed words, before/after continuity, step/clock/context/lane and ordered memory/retire events, explicit nondeterministic choices, canonical deterministic JSON, cancellation, mutation, duplicate/unknown JSON fields and fail-closed CPU timing/rank4 requests. Fixture assembly bytes are deliberately **not** a compiled PE: these tests check the DTO/codec only, not Golden execution, verified equivalence or hardware acceptance. If .NET 11 is absent report `NotRun`; do not call synthetic tests independent Golden receipts.

### Isolated HB-INT-04 codec gate

```sh
dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/GoldenRawTrace.ContractTests.csproj -c Release
```

This .NET 11-only project compiles the actual production snapshot, fixed AddWord word DTO and raw trace codec directly, with **no** ProjectReference to the software compiler, Roslyn, CircuitWeave or native tools. It checks the new contract independently while the original Companion/CIL integration suite is still exercised separately. The baseline compiler Core may fail with unrelated `HybridCPU.Compiler.Core.IR.Artifacts` missing types (`CS0234/CS0246`): this is an explicit **Failed/Blocked integration gate**, not grounds for marking it green or removing the original tests. The CI workflow keeps that failure visible in a separate job.

## HB-INT-03 Candidate method graph mutations

`BridgeIndexV2Tests.Run()` now also covers graph/root/PE/PDB internal-consistency refusals on synthetic Candidate payloads. Run the isolated production codec host with `dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.V2.ContractTests.csproj -c Release`. Results are software contract tests only and do **not** waive the independently failing Companion Csc build; see [policy](../Docs/HB_INT_03_Candidate_Graph_Coherence.md).
