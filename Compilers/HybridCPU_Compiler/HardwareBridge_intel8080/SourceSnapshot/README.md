# SourceSnapshot v1 — diagnostic inputs only

`BridgeSourceSnapshotV1.Capture` receives **explicit** virtual source texts, ordered in-memory reference images, exact root selectors and disjoint body/metadata-only index lists. No path probing, type-name matching, reflection, assembly loading or filesystem discovery occurs. It bounds counts, per-item and total bytes; rejects duplicate case-aliased/traversing paths, identical reference images, overlapping roles and duplicate roots; and checks cancellation. Snapshot DTOs retain hashes and lengths, not mutable source buffers.

`SnapshotDigest` is computed over **this bridge's** stable ordered JSON preimage (UTF-8 + SHA-256). It is **not** `ManagedSourceCompanionV1.SourceInputDigest`. Calling `HasConsistentDigest` checks only self-consistency, not authenticity. The actual Companion input digest appears in compiler facts only after an existing Companion invocation.

The typed `CompanionBridgeProjectionV1.CompileBodyWorld` performs an initial validation, deep-copies caller references and selectors, confirms both source snapshots match, and invokes existing `ManagedSourceCompanionV1.CompileBodyWorld` exactly once on the owned request. Its source language uses the existing `CompanionSourceLanguageV1` C# 13 / gated C# 15 selection, not a new language policy. Caller must not mutate inputs concurrently during ownership transfer.
