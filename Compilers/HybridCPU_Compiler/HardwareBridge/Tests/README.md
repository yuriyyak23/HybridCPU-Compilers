# Self-tests (isolated)

Run from a workspace containing the repository's existing .NET 11 SDK and Roslyn/CIL prerequisites:

```sh
dotnet --info
dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.SelfTests.csproj -c Release
dotnet build Compilers/HybridCPU_Compiler/HardwareBridge/HardwareBridge.Companion.csproj -c Release
```

The zero-external-test-package runner tests deterministic bytes, defensive copy, duplicate/traversal/empty identities, reference roles, budgets, cancellation, malformed/stale/tampered sidecar, exact signed bit patterns, overflow, upper 32 bits, unknown/untrusted names, throwing delegate and a real **compile-only** Companion/CIL AddWord fixture with PE/PDB/closure rejection. It never executes the newly emitted assembly. The runner reads the explicitly known test-runtime reference via `typeof(object).Assembly.Location`; production bridge code never opens a file.

`BridgeSelfTests.cs` is fully `#if HARDWAREBRIDGE_TESTS`-guarded. Only this test csproj defines that symbol, and only the opt-in Companion csproj defines `HARDWAREBRIDGE_COMPANION`. `Directory.Build.props` redirects generated metadata to the parent project's normally excluded `obj/` and `bin/` paths, so the main project's recursive compilation does not ingest nested generated `.cs` files.

If the SDK/runtime/reference closure is unavailable, record tests as **NOT RUN**, not PASS.

## HB-INT-04 raw trace (non-executable fixture)

The same standalone runner now executes `GoldenRawTraceTests.Run()` before the original scalar/Companion tests. It checks raw source/assembly digest binding, original-bit signed words, before/after continuity, step/clock/context/lane and ordered memory/retire events, explicit nondeterministic choices, canonical deterministic JSON, cancellation, mutation, duplicate/unknown JSON fields and fail-closed CPU timing/rank4 requests. Fixture assembly bytes are deliberately **not** a compiled PE: these tests check the DTO/codec only, not Golden execution, verified equivalence or hardware acceptance. If .NET 11 is absent report `NotRun`; do not call synthetic tests independent Golden receipts.

### Isolated HB-INT-04 codec gate

```sh
dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/GoldenRawTrace.ContractTests.csproj -c Release
```

This .NET 11-only project compiles the actual production snapshot, fixed AddWord word DTO and raw trace codec directly, with **no** ProjectReference to the software compiler, Roslyn, CircuitWeave or native tools. It checks the new contract independently while the original Companion/CIL integration suite is still exercised separately. The baseline compiler Core may fail with unrelated `HybridCPU.Compiler.Core.IR.Artifacts` missing types (`CS0234/CS0246`): this is an explicit **Failed/Blocked integration gate**, not grounds for marking it green or removing the original tests. The CI workflow keeps that failure visible in a separate job.
