# HybridCPU compiler modernization register

Status: 2026-09-03. This directory is the operational documentation for the DoomSharp managed
AOT refactor. Source, contracts, and reproducible evidence are authoritative; audits are only
backlog input.

## Authority

```text
PE/CIL -> RestrictedCilImporter -> canonical IR/CFG/value flow -> scheduling/allocation
       -> native ABI/frame lowering -> HCO -> static linker -> restricted HCEXE
```

NativeAOT/ILCompiler can supply roots, reachable bodies, type information, and closed-world
metadata. It is not HybridCPU instruction-selection, allocation, ABI, object-format, or link
authority. LLVM remains a sibling ingress. The scheduler proves structural admissibility only;
the runtime owns live execution, isolation, memory, ECALL, GC, and fault authority.

## Current facts

- `ThrowBoundarySmoke` now requires compiler-derived `eh-unwind` evidence from native EH method
  and runtime-HCO evidence. This corrected a host-smoke expectation only.
- `eh-unwind` is still outside `PublishQualifiedWorkstreams`; a request for it remains a
  fail-closed `HCPUB1004` rejection.
- A Doom `.hcexe` exists in historical loader evidence, but it is explicitly unqualified. It is
  neither a successful Doom guest run nor ISE qualification.
- Native `finally` continuation metadata and dispatch exist, but full EH evidence is incomplete.
  The remaining observed managed-EH test results are recorded in `Validation-2026-09-02.md`.
- General aggregates, reference-bearing inline copies, and unproven byrefs/receiver loans remain
  rejected.

## Evidence rule

Feature discovery, component tests, host/CoreCLR execution, image production, loader-backed CPU
execution, and publish qualification are distinct claims. Do not substitute one for another.
Promotion requires exact lowering/link/runtime evidence and two clean deterministic publishes.

See `Open-Tasks.md` for strict ordering, `Profile-Tooling.md` for non-authoritative tooling, and
`Next-Session-Doom-Refactor-Prompt.md` for the hand-off prompt.
