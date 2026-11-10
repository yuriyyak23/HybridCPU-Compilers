# HardwareBridge JSON sidecar v1

**Unqualified software observation; no RTL acceptance.** One deterministic UTF-8 JSON object created by `BridgeSidecarCodecV1.Write`:

```json
{"Schema":"hybridcpu.hardwarebridge.sidecar/v1","PayloadSha256":"<SHA256 of compact UTF-8 Payload object>","Payload":{"Schema":"hybridcpu.hardwarebridge.sidecar/v1","Source":{},"Facts":null,"GoldenOracle":null}}
```

`Source` is `hybridcpu.hardwarebridge.source-snapshot/v1`: assembly name, language host mode, declared compiler/Roslyn/admission policy, ordered `Documents` (virtual path, UTF-8 hash/length), ordered `References` (index, image hash/length, `owned-body` / `metadata-only` / `unselected`), exact roots (type/method/token/generic arguments), explicit body and metadata index arrays, and a separate `SnapshotDigest`. That digest hashes exactly the source fields **in the property order preceding SnapshotDigest**. It does not reproduce or replace Companion `SourceInputDigest`, particularly with SDK-gated C# 15 or specialized owned-constructor admission.

`Facts` is nullable `hybridcpu.hardwarebridge.compiler-facts/v1`: `Status = Candidate|Rejected`, matching SnapshotDigest, nullable true Companion SourceInputDigest, PE/PDB SHA-256, exact graph/contract digests, compiler import status, roots, normalized method identities/signatures and imported CIL edges, **primary-only** PDB source-map rows and diagnostics. A rejected example must not fabricate compiler hashes.

`GoldenOracle` is nullable `hybridcpu.hardwarebridge.golden-observation/v1`: `Status = Observed|Blocked`, reviewed source label, explicit allowlisted AddWord32 adapter ID, bounded vectors (each `Width`, `Signed`, unsigned decimal `Bits`) and exact outputs. Unsigned decimal bit patterns preserve 2's-complement storage; `Signed` only changes interpretation.

Serialization is in declared record/anonymous-property order, using default `System.Text.Json` formatting without indentation or timestamps; arrays retain explicit input order except normalized method/diagnostic rows. `PayloadSha256` is SHA-256 uppercase hexadecimal of the compact serialized `Payload` JSON bytes, **not a signature**. Readers must enforce <=16 MiB, exact schema, checksum, snapshot self-consistency, an externally expected current SnapshotDigest, status allowlists, and explicit body/metadata roles. Malformed/stale sidecars fail closed; never select or invoke a delegate by content. See [example-candidate.json](example-candidate.json), a reproducible illustrative snapshot + rejected compiler facts + test-only Golden observation.

**CircuitWeave integration:** treat the sidecar as a diagnostic source, not as a `BindingLock` or proof. A separate CircuitWeave owner must bind exact manifest/source/PDB/reference closure, evaluate hardware attributes through its resolver, import candidates into its own hardware frontend, and run independent verification ladder/tool/target evidence. Neither this sidecar nor Golden vector agreement can confer `AcceptedRtl`, `Verified`, correct hardware timing, reset, clock, memory or formal status.

**Known limitations:** one-process execution of an explicitly trusted delegate cannot be sandboxed/preempted; transitive runtime helper calls, source-less dependencies, stateful/heap/native/EH cases and any ambiguous method closure remain unsupported. Missing external build prerequisites or SDK are blockers, not successful test results.
