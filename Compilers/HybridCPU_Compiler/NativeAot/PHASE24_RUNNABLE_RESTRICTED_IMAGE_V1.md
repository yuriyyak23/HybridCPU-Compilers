# Phase 24D runnable restricted image

`compile-image` is the closed Phase 24 composition path:

```text
restricted CIL import -> Canonical IR -> schedule/bundle -> Phase 20 allocation
-> HCO v1 -> deterministic static link -> HCEXE001 startup package
```

It reuses the existing compiler pipeline and records the exact register-allocation
contract/options/witness, object, link-map and startup digests. Method-entry scalar
values are fixed to native ABI argument registers and the terminal return carrier uses
`JALR x0,x1,0`; the result remains in `x10`.

The runnable qualification executes a package produced from real C# through the
existing HybridCPU decoder, execute, memory/write-back and retire test seams. The
loader-side test applies the versioned startup descriptor; it does not introduce a
compiler interpreter or a second runtime pipeline.

The qualified program class remains the Phase 21 primitive/value subset. Managed
references, runtime helpers, executable static constructors, TLS, GC/safepoint
actions, EH/unwind, interop, dynamic libraries and host objects fail before image
qualification. A successful compiler manifest still grants no runtime, execution,
publication, commit or retire authority.
