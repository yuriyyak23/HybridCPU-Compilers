# ChatGPT compiler 2 — historical audit summary

This is a retained, non-authoritative summary of the second external audit. It is not an
execution plan and must not be used to infer current feature support. The maintained register is
`ChatGPT-compiler-2/README.md`; active work is in `ChatGPT-compiler-2/Open-Tasks.md`.

The audit recommendations retained as current work are:

- keep `PE/CIL -> RestrictedCilImporter -> Core -> HCO -> static linker -> HCEXE` as the
  HybridCPU backend authority; NativeAOT is bounded semantic ingress and LLVM is a sibling;
- preserve three independent admission layers: source tooling, CIL import, and closed-world
  graph/link qualification;
- complete one bounded aggregate/receiver-loan path before general byref or aggregate support;
- derive profile/CoreLib requirements from the reachable graph rather than declarations;
- keep static contract metadata deterministic and non-authoritative; keep performance tooling
  and solver/oracle work outside production Core.

No audit text qualifies a workstream, a publish, an ISE run, or Doom guest execution.
