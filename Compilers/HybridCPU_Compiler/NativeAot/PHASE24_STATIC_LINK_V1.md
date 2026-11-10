# Phase 24B — deterministic static-link contract

## Qualified scope

`hybridcpu.static-link/v1` consumes only canonical checksum-valid HCO v1 objects and produces a deterministic statically laid-out byte image plus a complete link map. This layer resolves symbols and linker-owned relocations. It does not produce an executable container, select an entrypoint, initialize runtime state, execute code, or authorize publication.

## Production options

- image base: `0x00010000`;
- page alignment: 4,096 bytes;
- maximum inputs: 256;
- maximum image size: 256 MiB;
- input identity: explicit UTF-8 module identity, unique, no NUL, at most 128 bytes;
- exact bindings: HCO object contract, target-platform digest and managed-ABI-family digest;
- dynamic libraries, weak symbols and COMDAT coalescing: unsupported;
- host object inputs and host linker fallback: forbidden.

Options are one immutable production record with a digest. Any modified record is version skew and is rejected before input inspection.

## Ordering and layout

Inputs are sorted by module identity. Section contributions are ordered by `(section kind, module identity, section name)`. A change of section kind begins a new page; each contribution additionally respects its HCO alignment. Zero-fill sections occupy deterministic zeroed image space. Timestamps, randomized bases and host paths are absent.

Local definitions are scoped by `(module identity, symbol name)`. Global definitions are unique across the link. Duplicate globals fail; undefined globals fail; weak/COMDAT fallback does not exist. Link-map ordering is address, then symbol/module identity.

## Relocation ownership

HCO records `Absolute64` and `PcRelative32` as linker obligations. The static linker evaluates `S + A` and `S + A - P` with checked arithmetic, validates width, and writes little-endian values at the exact HCO-owned patch locations. Overflow produces `HCLINK1005` and no image. Compiler-internal bundle-relative relocation and TLS remain unavailable to this layer.

## Provenance and authority

The link-map digest includes the production options digest, ordered `(module identity, object SHA-256)` inputs, section addresses, resolved definitions and every applied relocation/value. The image SHA-256 covers the full linked byte image including deterministic alignment gaps and zero-fill storage.

Successful symbol resolution is linker-owned evidence only. Runtime legality, startup state, execution, publication, commit and retire authority remain false. Phase 24C owns entrypoint/startup/helper bootstrap; Phase 24D owns the restricted runnable-image gate.
