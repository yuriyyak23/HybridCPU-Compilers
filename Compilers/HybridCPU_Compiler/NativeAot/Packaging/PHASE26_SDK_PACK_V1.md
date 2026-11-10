# HybridCPU custom SDK/runtime pack v1

## Exact ILCompiler admission checkpoint, 2026-10-06

`Install-HybridCpuSdkPack.ps1` pins the refreshed exact Release binaries from the
unchanged `94ea82652cdd4e0f8046b5bd5becbd11461482ca` archive (SHA256
`694b9134d338db6b0d31b1a73d9c89e5419432f2f0622b610ecdf5455e851b96`), the maintained
Phase 23/26 patch digests and upstream SDK `10.0.105`. HybridCPU compiler projects
continue to use .NET 11 RC1; this does not migrate the upstream source baseline.

Pin rollover evidence is in
`TempEnv/doom-continuation-20260905/ilcompiler-exact-20261006-000310`.
The previous external binaries had stale synchronous adapter-pipe reads and old
image validation. Exact-source build and forced rebuild pass with stable hashes.
The refreshed installer admits the immutable candidate; each of the three pinned
binary corruptions still rejects with HCPUB1208 before pack creation. All 204
manifest file sizes/hashes verify, and code/image graph-seam controls pass using
the installed pack with wrong-patch/output-kind negatives.

This explicitly replaces binary identities, not source/patch/SDK authority. There
is no unpinned override. The old frozen packs/images retain their identities.
This checkpoint proves pack admission and fixture graph traversal only.
Qualification: no.

This pack defines the private `hybridcpu` RID for the controlled bare-metal simulator/runtime environment. Version `1.1.0-refplan7-phase01` evolves the Phase 26 pack identity for managed ABI/bootstrap contract V1.1, supports `win-x64` as a cross-compilation host with publish SDK `10.0.204`, and emits only `HCEXE001` target images. The maintained ILCompiler itself is hosted by its pinned VMR SDK `10.0.105`. It never substitutes a host apphost, JIT, another NativeAOT architecture or LLVM. Managed publish workstreams remain default-disabled unless independently qualified.

The maintained target-enablement patch set is ordered and immutable: Phase 23 patch `e03a6167...df32` establishes the graph-selected single-method seam; Phase 26 patch `28567486...67ad` adds the closed `image` output mode and copies the adapter sidecar. The combined patch-set digest is `08a6ab21...e7de`. Default `code` mode remains unchanged, so the Phase 23 raw-code evidence stays reproducible.

The consuming project imports `HybridCPU.Sdk.props` before the SDK targets and `HybridCPU.Sdk.targets` at the end, opts `PublishAot` into `TreatAsLocalProperty`, and supplies one restricted entry method plus an explicit dotnet host. The installed pack owns the adapter/Core/CIL compiler pack, exact patched ILCompiler directory, runtime reference set, runtime feature descriptor, SDK props/targets and both patches. The checked-in runtime graph is self-contained and restore can be offline once the ordinary SDK reference assets are present. A missing compiler, manifest, entry point, host qualification, RID match, contract digest, ILCompiler, runtime reference set or supported-workstream match fails before image emission with `HCPUB1xxx`.

The runtime-pack descriptor records the complete Phase 25 component feature set. `PublishQualifiedWorkstreams` is the smaller, explicit set wired through NativeAOT body presentation, the managed graph importer, object linking, and restricted-image construction. It currently includes exact generics, object references, SZ arrays, UTF-16 literals, static initialization, GC maps/safepoints, and virtual/interface dispatch. Component-qualified EH remains withheld until the native `finally` continuation dispatcher and loader-backed handled/unhandled execution evidence exist. Publish authority is checked after linking against compiler-derived workstreams from the imported graph and actual runtime HCO closure; `HybridCpuManagedRequirements` is only an optional superset assertion. ScalarControlFlowV2 discovers every token-bearing application body automatically, while `HybridCpuPresentedMethods` is an optional diagnostic assertion rather than a body-world authority.

`dotnet publish` invokes `publish-graph`; the pinned ILCompiler performs `SingleMethodRootProvider -> ComputeMarkedNodes -> MethodCodeNode.SetCode`, while the adapter performs the already-qualified final allocation, HCO writing, deterministic link and HCEXE startup packaging. Host ObjectWriter is still bypassed. The direct `publish-bringup` command is diagnostic-only and is never selected by SDK targets. The final `.hcexe.provenance.json` pins SDK, host and target RID, compiler/runtime pack, `dotnet/runtime` source and ordered patch set, target/managed ABI/feature contracts, ILCompiler and runtime-reference digests, linker/options, input PE digest and final image digest. It grants no runtime execution or release/publication authority.
