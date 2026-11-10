# Phase 24C restricted startup contract

`hybridcpu.restricted-startup/v1` packages a successful `hybridcpu.static-link/v1`
artifact into deterministic `HCEXE001` bytes. The package carries the linked image,
entry address, stack bounds, native register roles and exact target/native/managed ABI
and link-map digests. It contains no timestamp, build ID, host object or dynamic-library
fallback.

The qualified entry is one global/default, whole-bundle code symbol. Initial state is
`IP = entry`, ABI-aligned `x2 = stack top`, `x8 = 0`, `x4 = 0`, and `x1 = return
sentinel`; arguments use `x10..x17` and the restricted result is observed in `x10`.
The loader/runtime owns applying this state and executing it.

The static-initialization subset is zero-filled BSS only. Executable static
constructors are rejected. Every reserved Phase 22 managed helper is still
unsupported, so its presence rejects the image. TLS, GC services, safepoint actions,
EH/unwind, interop, code-manager registration and host services remain unavailable.

Successful packaging proves only compiler-owned startup metadata consistency. It does
not grant runtime, execution, publication, commit or retire authority. Gate 24D must
separately load and run the package through an existing qualified CPU/runtime path.
