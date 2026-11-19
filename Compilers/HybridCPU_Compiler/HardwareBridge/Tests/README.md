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
