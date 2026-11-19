# HB-INT-01 — Detached HWIR regression compatibility

**Scope:** regression-only configuration of `HardwareBridge.Hwir.csproj`. The production software-facts producer remains `HardwareBridge.Companion.csproj` and has no Core `ProjectReference`. No exporter schema, Golden source or parent compiler is changed.

## Explicit cross-repository build (not production dependency)

Use two independently chosen checkout directories. Record and review the actual 40-hex commit IDs before executing the compatibility fixture:

```bash
CORE_ROOT=/absolute/path/to/any-core-checkout
BRIDGE_ROOT=/absolute/path/to/any-hybridcpu-compilers-checkout
CORE_SHA=$(git -C "$CORE_ROOT" rev-parse HEAD)
BRIDGE_SHA=$(git -C "$BRIDGE_ROOT" rev-parse HEAD)
test "$CORE_SHA" = "<reviewed-core-commit>"
test "$BRIDGE_SHA" = "<reviewed-bridge-commit>"
dotnet build "$BRIDGE_ROOT/Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/HardwareBridge.Hwir.csproj" \
  -p:CircuitWeaveFrontendRoslynProject="$CORE_ROOT/src/CircuitWeave.Frontend.Roslyn/CircuitWeave.Frontend.Roslyn.csproj"
```

The caller must ensure these paths and commits are trustworthy. An absent or missing explicit project path fails with `HBINT0101` / `HBINT0102`. The fixed `CircuitWeave-main` dependency has been removed; the Bridge HWIR project is **not** a prerequisite of `HardwareBridge.Companion.csproj`.

The optional P14 Core compatibility launcher is `eng/cw-int-02-bridge-compat.sh <bridge-checkout> <40-hex-bridge-revision>` in the Core repository. This verifies the actual checkout HEAD against the pin, and passes the absolute Core project as an MSBuild property. Its old BridgeHwir source-specific cases remain candidate-only; it is NOT a new Core IR owner or independent RTL verification gate.

## Compatibility and incomplete gates

- Wire source is unchanged: `hybridcpu.hardwarebridge.sidecar/v1`.
- The Core-owned `circuitweave.bridge-import/v1` descriptor binds existing sidecar bytes, revision label and explicit caller-supplied SHA-256 pins. This descriptor is not Bridge-written or proof of source/PE/PDB closure.
- Full source/Csc identity verification, native CIRCT/RTL qualification, target observation, Processor top and physical signoff remain Blocked/NotRun until their independent follow-on packages deliver evidence.
- No platform-native or .NET tests are claimed here until actual CI logs exist.
