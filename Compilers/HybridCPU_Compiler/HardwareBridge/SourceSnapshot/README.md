# SourceSnapshot v1 — diagnostic inputs only

`BridgeSourceSnapshotV1.Capture` receives **explicit** virtual source texts, ordered in-memory reference images, exact root selectors and disjoint body/metadata-only index lists. No path probing, type-name matching, reflection, assembly loading or filesystem discovery occurs. It bounds counts, per-item and total bytes; rejects duplicate case-aliased/traversing paths, identical reference images, overlapping roles and duplicate roots; and checks cancellation. Snapshot DTOs retain hashes and lengths, not mutable source buffers.

`SnapshotDigest` is computed over **this bridge's** stable ordered JSON preimage (UTF-8 + SHA-256). It is **not** `ManagedSourceCompanionV1.SourceInputDigest`. Calling `HasConsistentDigest` checks only self-consistency, not authenticity. The actual Companion input digest appears in compiler facts only after an existing Companion invocation.

The typed `CompanionBridgeProjectionV1.CompileBodyWorld` performs an initial validation, deep-copies caller references and selectors, confirms both source snapshots match, and invokes existing `ManagedSourceCompanionV1.CompileBodyWorld` exactly once on the owned request. Its source language uses the existing `CompanionSourceLanguageV1` C# 13 / gated C# 15 selection, not a new language policy. Caller must not mutate inputs concurrently during ownership transfer.

## Byte and selector treatment

`Capture` hashes the supplied .NET source strings through `Encoding.UTF8` without adding a BOM or normalizing line endings. This identifies text converted to UTF-8. A capture from original files must separately check raw encoding and byte identity. Virtual paths are relative, slash separated, at most 512 characters, and reject control characters, backslashes, colons, empty segments, dot segments and segments with leading/trailing spaces. Uniqueness is ordinal-ignore-case. No content-based filesystem identity is obtained.

Budgets are 1–128 documents (1 MiB text each), 0–128 nonempty reference images (32 MiB each), 1–32 roots, and 64 MiB combined. Reference rows preserve ordinal and exact byte hash. A reference outside both disjoint ordered lists is `unselected`; the input still reaches Roslyn as a compilation reference, without CIL body ownership. Root selector order is explicit, the token may be null, and each ordered generic-argument list allows at most eight entries. Capture rejects duplicate exact selectors. CIL resolves overloads and imports actual identities under its own rules. CircuitWeave may additionally reject an ambiguous or untokened generic root.

The [v1 wire page](../Docs/sidecar-v1.md) defines the snapshot preimage. See [CompilerFacts](../CompilerFacts/README.md) for digest domains and [current compatibility](../Docs/current/COMPATIBILITY.md) for consumer differences.
