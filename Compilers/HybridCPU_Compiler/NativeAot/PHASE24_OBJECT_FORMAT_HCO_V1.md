# Phase 24A — HybridCPU relocatable object contract (HCO v1)

## Gate and scope

HCO v1 is the deterministic compiler-owned relocatable container for the restricted Phase 23 NativeAOT seam. Phase 24A qualifies object construction and inspection only. It does not claim static linking, executable startup, runtime services, GC/EH correctness, execution, publication, commit, or retire authority.

The historical `HybridCpuTargetPlatformContractV1` remains unchanged with `ObjectFormat=None`. HCO v1 is a new independently versioned completion contract rather than a rewrite of Phase 08B evidence.

## Identity and byte order

- schema: `hybridcpu.object/hco-v1`, version `1.0`;
- magic: ASCII `HCOBJ001`;
- byte order: little-endian;
- timestamps and host build IDs: absent;
- integrity: SHA-256 of the complete object with the 32-byte checksum field cleared;
- binding: exact Phase 21 target-platform digest and Phase 22 managed-ABI-family digest;
- code bundle size/alignment: 32 bytes.

## Fixed layout

| Offset | Size | Field |
|---:|---:|---|
| 0 | 8 | magic |
| 8 | 2 + 2 | schema major/minor |
| 12 | 4 | header size (`176`) |
| 16 | 4 | flags (`0`) |
| 20 | 4 × 3 | section/symbol/relocation counts |
| 32 | 8 × 6 | section, symbol, relocation, string offsets; string size; payload offset |
| 80 | 32 | target-platform digest |
| 112 | 32 | managed-ABI-family digest |
| 144 | 32 | content checksum |

Section entries are 40 bytes, symbol entries are 32 bytes, and relocation entries are 32 bytes. Reserved fields and all alignment padding are zero. Strings are unique, ordinally sorted, UTF-8, NUL-terminated, and referenced by offsets. Sections are ordered by `(kind, name)`, symbols by `(name, binding, visibility)`, and relocations by `(section, offset, kind, target, addend)`.

## Closed feature matrix

| Feature | Owner | 24A status |
|---|---|---|
| code / read-only data / writable data / zero-fill sections | object writer | Supported |
| local/global symbols; hidden/default visibility | object writer | Supported |
| `Absolute64` relocation record and overflow evaluator | linker | Supported |
| `PcRelative32` relocation record and overflow evaluator | linker | Supported |
| `BundleRelativeSigned16` | compiler internal control-flow resolver | Forbidden in HCO |
| TLS section/relocation | future linker/runtime | Unsupported |
| weak/COMDAT duplicate coalescing | future linker | Unsupported |
| debug/unwind/EH sections | future object/runtime work | Unsupported |
| host ELF/COFF/PE/Mach-O input or fallback | none | Rejected |

An HCO relocation record describes a linker obligation; the writer never claims that the relocation is resolved. Multiple relocations cannot own the same patch location. Patch width, alignment, section bounds, symbol identity, and addend arithmetic are checked before emission. Unknown and overflow cases produce stable `HCOBJ*` diagnostics and no bytes.

## Production budgets and deterministic fallback

- sections: at most 4,096;
- symbols: at most 65,536;
- relocations: at most 1,000,000;
- string table: at most 4 MiB;
- complete object: at most 256 MiB;
- names: non-empty UTF-8, no NUL, at most 1,024 encoded bytes;
- section alignment: power of two, at most 4,096 bytes.

Budget, schema, target, ABI, checksum, canonical-order, padding, unsupported-feature, and malformed-range failures are deterministic. There is no wall-clock decision and no fallback to a host object format, host linker semantic, RyuJIT code, or approximate relocation.
