# HybridCPU compiler release qualification v1

`HybridCPU.Compiler.Release` is an outward-only qualification and manifest layer. It consumes the already versioned Core, Native, LLVM, CIL, NativeAOT and Oracle contracts; none of those projects reference it, and it does not implement a second compiler pipeline.

The v1 profile dispositions are deliberately independent:

- `HybridCPU.Native`: release-authorized for the standalone ASM/carrier path and its currently available Canonical IR, schedule, bundle, ASM, binary, evidence, scheduling-report and provenance artifacts. The standalone object container remains explicitly unsupported.
- `HybridCPU.LLVM`: verified default-off optional LLVM 20.1.2 ingress. It has no scheduling/backend authority and version or semantic mismatch fails before Canonical IR.
- `HybridCPU.DotNetAot.Restricted`: release-authorized, default-off, only for the exact Phase 26 value-only single-static-method custom RID pack on the qualified `win-x64` host and .NET SDK 10.0.204.
- `HybridCPU.DotNetAot.Managed`: unsupported as an end-to-end publish profile. Phase 25 GC-map/code-manager component evidence does not qualify allocation, barriers, EH, interop or TLS.
- `HybridCPU.Full`: unsupported. Individually verified default-off VT/FSP/VDSA/compiler components do not create an umbrella runtime capability.

The release manifest and feature matrix are deterministic artifacts. Compile time and peak working set are emitted separately as performance telemetry; they never enter the release digest or select production output. Runtime legality, freshness/replay, execution, publication, commit and retire remain runtime-owned.

Run the exact two-run verifier with an implementation commit and the Phase 26 evidence SHA-256:

```powershell
./Verify-Phase27Release.ps1 -CompilerCommit <sha> -Phase26EvidenceSha256 <sha256>
```

The verifier creates a fresh isolated directory under `TempEnv/doom-continuation-20260905`
(or accepts a new `-OutputRoot`), restores/builds there, resolves `TargetPath` through
MSBuild and retains logs and outputs. Environment overrides are restored on exit.
Compiler provenance reads the SDK version embedded at build time; it does not use
the separate restricted publish SDK pin. The deterministic `.compiler.json` sidecar
records this version identity and build provenance, not a full SDK binary hash.
Release baseline drift still rejects with HCRL1012; a component build is not promotion.
