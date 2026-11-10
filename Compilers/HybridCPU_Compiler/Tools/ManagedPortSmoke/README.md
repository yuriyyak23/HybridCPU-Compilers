# Doom managed-port compiler/runtime smoke

Signed-byte V2 loads reach images through the existing checked U1 helper plus
exact I1 sign extension. The full suite compares serialized straight/conditional
code with independent ldelem.u1/conv.i1 paths, including the I4 phi producer.
No runtime ABI/service addition is implied.

The full suite checks checked Int64 array loads through the existing runtime array
owner and CPU-callable helper operation39: full-width payload bytes, adjacent
elements, null/bounds/wrong-width refusal and serialized service/ABI/repeat words.
Managed ABI minor62/runtime-pack contract v1.24; this is no guest qualification.

`--module-acquisition` checks late malformed dependency/metadata modules, diagnostic
repeat/reuse and observed disposal of a completed reader on later acquisition failure.
It also checks caller-token cancellation on module2 before open, collection cancellation
cleanup and successful reuse; constructors/hash are synchronous bounded operations.

EH-plan discovery and exact rederivation borrow the BodyWorld call-owned reader without
caching plans. `--eh-ingress-budget` also checks active graph parity, pre-cancel and
successful reuse; public EH ownership/malformed/budget diagnostics remain covered.

BodyWorld per-method import borrows its call-owned module PEReader/metadata;
public ImportImage ownership is unchanged. Existing closure/generic/malformed/
cancellation/reuse controls apply; this is no persistent cache or concurrency feature.

`--typespec-count` checks constructed generic declaring-type PE count2/127 admission,
closed positive/exact context identities, deterministic rejection and importer reuse.

`--nested-generic-count` checks actual Identity<Box<int>> MethodSpec, nested2/127
structural rejection before storage, deterministic diagnostics/closed identities/reuse.

GenericIngress also checks outer MethodSpec counts2/127 against remaining bytes before
storage, preserving HCSCF-BODYWORLD1002 and successful closed-context reuse.

`--local-count` checks real local-signature count2/127 structural rejection before
storage, HCCIL0029/repeat/reuse/positive and PE-budget precedence.

`--fnptr-header` checks a real managed function-pointer parameter signature:
It also mutates method and nested parameter counts2/127 and requires deterministic
InvalidInput/HCCIL0006 before oversized parameter storage.
reserved80 variants reject HCCIL0006, existing convention/this forms remain unsupported,
ordinary import/repeat/reuse and PE-budget precedence retained. No pointer execution claim.

`--method-header` checks real MethodDef reserved80 variants fail closed with HCCIL0006,
ordinary successful import, deterministic repeat/reuse and PE-budget precedence.
Nested FNPTR signatures remain a separate coverage gate.

`--calli-header` checks actual PE StandAloneSig reserved-bit admission before binding,
ordinary missing-binding control, three reserved variants, deterministic rejection/reuse
and PE-budget precedence. This is calli-only diagnostic regression.

`--postbundle-cancellation` checks shared lowering None/active bytes and annotations,
pre/mid-bundle/callback cancellation without completion, and reusable inputs.
NativeAot and CIL linker propagate their caller token into this lowerer. Allocated
completion also retains legacy/None/active bytes and pre-cancel/no result.
Program-level annotation emission now also observes caller cancellation between bundles.
The same command checks legacy/None/active sidebands, pre/mid cancellation and reuse,
including shared completion forwarding into the annotation pass after successful lowering.
Single-bundle metadata construction stays bounded and synchronous.

The same command checks BuildAgreement legacy/None/active classification/fact/diagnostic
equality, pre/mid aggregation cancellation, builder/input reuse and token forwarding
through shared completion before AgreementSummary. AnalyzeBundle and ValidationOnly
compiler preflight semantics stay unchanged; no runtime authority is acquired.

The existing `--branch-entry` command now also checks relocation caller-token
None/active encoded equality, pre/copy/entry-map/placement-map/patch cancellation,
unchanged input bundles and reuse. Existing41 entry/interior/short/long/negative
semantics controls remain. Single-operation LINQ searches and bundle checker bodies
are still synchronous; no general backend latency claim.

`--serializer-cancellation` checks legacy/None/active and empty image size/bytes,
pre/count/mid/final-read cancellation without a returned image, token forwarding and reuse.
Shared completion and CIL method serialization receive the caller token. Existing
size arithmetic, write failure handling, budgets and single-bundle serialization stay unchanged.

`--nativeaot-compile-cancellation` checks caller-owned Compile token/parse precedence,
pre-cancel no pair, three None/active artifact+manifest pairs, pre-write existing-pair
preservation and late serialization cancellation completing the synchronous Write sequence.
Non-publish Main owns Console signal lifetime; Windows isolated-process Ctrl-C130/no pair is
validated in p5-nativeaot-cli-signal-20261014. Other platforms/late producer signals, PCL
auxiliary rollback and atomic I/O remain unproven.

`--pcl-admission` checks NativeAot sideband path admission before file mutation:
producer/host/manifest aliases preserve files, valid derived/non-derived clearing stays,
default-off context stays null (9 controls). This is filesystem admission regression,
not producer execution or publication evidence.

The same bounded command also checks shared NativeAotSeamOutputV1 payload/manifest round trip
and pair rejection cleanup (11 controls total). Real CLI before/after output equivalence is
retained in p1-nativeaot-output-owner-20261014; no atomic write/CLI cancellation claim.

`--managed-link-cancellation` checks actual two-method PE graph Link:
legacy/None/active full result projections (objects/relocations/link/image/provenance),
pre-cancel, deterministic second-method cancellation/no partial return, invalid-entry/null
precedence and instance reuse. Component API is not CLI cancellation/publication evidence.

`--allocator-cancellation` checks Allocate token ingress: None witness/physical-image
equality, pre-cancel, deterministic selected-plan digest cancellation, exception escape,
null/budget diagnostic precedence, instance reuse and shared token-aware allocation callback.
Proof rebuild forwards the token through both scheduler/bundler passes; a deterministic
topology-rank read cancellation checks exception escape/no result and allocator reuse.
General dependency/resource/loop analysis internals remain synchronous and separately scoped.

Allocation physical topology/order/frontier loops now check caller cancellation. Frontier
first-mask-read cancellation must stop before the next ordinal and return no partial edges;
existing200 frontier reachability/equivalence cases and cyclic/malformed diagnostics remain.
The general dependency analyzer and sorting/materialization internals stay synchronous.

`--allocation-orchestration` checks CompileWithAllocation caller cancellation before scheduling,
after scheduling and after real allocation callback. None witness/image equality, absent allocator,
unwitnessed/budget-rejected layout preservation and post-cancel reuse are retained.
Allocator internals and NativeAot CLI do not acquire cancellation from this contract alone.

## 2026-10-13 bundler cancellation

`--bundler-cancellation` checks global/local bundle materialization: legacy/None bytes and
annotations, pre-cancel, deterministic mid-cycle reads, shared callback cancellation and
instance reuse. Raw canonical caller tokens now reach materialization through the shared
ProgramBundler/IrProgramCompiler. Placement/search internals remain synchronous; no
general backend latency/runtime emission claim.

## 2026-10-13 canonical cancellation

`--canonical-cancellation` checks token-aware canonical local/fallback compilation:50 controls
for legacy/None/active artifacts and observer ordering, pre-cancel, each progress boundary,
no partial artifact return, reuse and frontend diagnostic precedence. Full suite62 groups
passes in p5-canonical-cancellation-20261013. Backend operations between checkpoints remain
synchronous; no runtime emission transaction or general latency/concurrency claim.

## 2026-10-13 opt-in source-map sidecar

`--source-map` checks symbol/token/IL/IR correlation, matched Portable PDB document
mapping, missing-PDB unknown locations, deterministic digest and corrupt/stale PDB/PE
negatives. Sidecar is diagnostic-only; importer/image/cache authority unchanged.
Full current Release suite61 PASS groups: `m4-source-map-20261013`. Qualification: no.

## 2026-10-13 managed input closure

`--closure-identity` verifies source-label independence, same-method changed-PE
invalidation, reordered metadata-only reference equality and reference-removal
invalidation. Graph contract/provenance bind role-specific sorted assembly/SHA inputs;
generic context remains in stable method identities. Existing CIL HCO/image/witness
bytes unchanged, provenance changes intentionally. Full current Release suite59 PASS
groups: `m4-managed-closure-20261013`. Qualification: no.

## 2026-10-13 ImportImage cancellation

`--ingress-cancellation` verifies preserved legacy/None bytes/provenance, caller-token
pre-cancellation, deterministic mid-call input-read cancellation and positive sequential
reuse. Existing API remains; the new overload observes safe ingress/stage boundaries
and throws OperationCanceledException without returning a successful partial result.
Inputs/bindings must remain stable during the call. Full current Release suite54 PASS
groups: `m3-image-cancellation-20261013`. Body-world/EH/file cancellation and shared
concurrent ownership are not established by this slice. Qualification: no.

`m3-graph-cancellation-20261013` extends the same harness and retained body-world API
with caller-token worklist/method/final-result checkpoints. None graph digest/order,
pre-cancel, deterministic mid-input cancellation and positive reuse pass; full suite55
PASS groups. EH/file cancellation and shared concurrent ownership remain separate.

`m3-eh-file-cancellation-20261013` adds retained-API caller-token EH/file overloads
and same-token graph-to-EH propagation. None bytes/provenance/plan digest, pre-cancel,
mid-input EH cancel and sequential reuse pass; full Release suite56 PASS groups.
Calls remain synchronous: cancellation is observed at boundaries, not an OS-read
interrupt or arbitrary latency guarantee. Caller owns stable input/binding payloads;
concurrent shared importer safety is not promoted. Constructor membership snapshot
consistency for managedCallTargets remains the next ownership gate. Qualification: no.

`m3-binding-ownership-20261013` normalizes constructor membership snapshot for managed
call targets. Actual managed-call binding/provenance/diagnostics survive clearing the
caller dictionary; full suite57 PASS groups. Nested row payloads remain caller-owned
stable data and shared instances require caller serialization. This does not establish
deep immutability or unsynchronized concurrent safety. Qualification: no.

## 2026-10-13 malformed ingress

`--eh-ingress-budget` covers production-positive EH admission and deterministic
HCCIL2805 rejection when the exact IL body exceeds MaximumMethodBodyBytes. Before
the guard, budget1 incorrectly returned Success. Current full suite:52 PASS groups
in `m3-eh-budget-20261013`; no guest EH execution qualification follows.

`m3-eh-clauses-20261013` extends that same regression with6 malformed clause
range/catch-token controls, byte-budget precedence and sequential reuse. Narrow,
independent-process repeat and full Release suite52 pass; existing rejection codes
HCCIL0802/1804/0803 are retained. No importer change was required for these mutations.

`--malformed-ingress` runs18 fixed PE/metadata/method/token mutations over the existing deterministic
dependency fixture, logs input SHA/status/codes and checks repeated rejection/no IR plus
positive sequential importer reuse and exact PE/body budgets (22 checks). Invalid call,
calli signature and ldftn tokens reject without IR. Independent process output matched.
Full current Release suite:51 PASS groups in `m3-method-ingress-20261013`.
This bounded corpus does not establish exhaustive fuzz coverage or concurrent-session safety.
Qualification: no.

## 2026-10-12 evidence scope

Current plan: `Documentation/Compilers/DoomSharp-HybridCPU-Compiler-Refactor-Plan-2026-09-04/Modernize`.
Exact ILCompiler HCPUB1208 admission was closed by the immutable 2026-10-06
`ilcompiler-exact-20261006-000310` evidence; strict corruption negatives remain.
Older blocked/publication assertions below are historical checkpoints.
The M0 inventory itself did not execute smoke/build. Subsequent current-source
`eh-type-admission-20261012` validation passed full Release smoke (50 PASS groups),
`--throw-boundary` and `--static-projection`. Native EH without the exact admitted
System.Exception descriptor now rejects as HCSCF-LINK4061 before object emission,
after existing helper/budget/graph diagnostics. The throw positive supplies exact
metadata-only CoreLib; missing-metadata negative creates no objects/image.
Qualification: no.

## 2026-10-05 CFG entry relocation regression

The projects use the repository's .NET 11 RC1 target. `--branch-entry` imports the
minimal reference-field diamond through the real scheduler and Qualification allocator,
then decodes serialized short/long branch words and the required ADDI x10,x18,0.
Semantic block entries use the first final block segment bundle; interior targets retain
their instruction placement. Missing/ambiguous targets are rejected. This compiler-word
evidence grants no guest execution or qualification authority.

Evidence: `TempEnv/doom-continuation-20260905/branch-entry-20261005-225449`.
Before: encoded bundle 20 skips copy in 19. After: bundle 19, 41 checks passed.
The subsequent `fixture-drift-20261005-235658` iteration fixes ClosedInterfaceSmoke's
duplicate control with the existing small dependency module. Independent module-count
and byte-budget controls retain HCSCF-BUDGET2010 precedence. Narrow closed-interface,
branch-entry and full Release smoke exit 0 under .NET 11 RC1.
`--branch-entry-next-gate` remains a diagnostic for duplicate-CoreLib byte-budget
precedence. Exact ILCompiler pack admission remains blocked by HCPUB1208 SHA skew.
Qualification: no.

## 2026-09-01 current backend checkpoint

Successive clean cycles closed four reached blockers: rejected loops skip distance
DAG construction; symbolic control-flow labels use byte-address encoding; exact
helper ABI argument/result values expose x10 aliases before scheduling; and only
guest-service symbols with linker-owned runtime HCOs join the direct-callee set.
`AllocationCapacitySmoke`, `ArgumentStoreSmoke`, and `VirtualClockSmoke` retain the
positive and fail-closed regressions. Full ManagedPortSmoke, Doom CoreCLR 26/26,
requested Release builds, compiler/runtime/kernel builds, pack verification and
closure reporting pass in `TempEnv/doom-guest-callee-20260901`.

Normal publish remains HCPUB1004 `eh-unwind`. Diagnostic publication now stops at
HCSCF-LINK4003 signed-16-bit control-flow relocation range. A body-world scan binds
the same blocker to `DoomSharp.Core.GameLogic.Trigger.TeleportEvent`, IR 310 / 231
values, CIL `IL_002a` -> `IL_0196`, final IR 71 -> 391. This is a real long-branch
lowering requirement, not the previously fixed target-index corruption. No `.hcexe`
or ISE execution exists.

## 2026-08-31 exact scheduler successor counts

`SchedulerSuccessorSmoke` runs in the full suite or alone with
`dotnet ManagedPortSmoke.dll --scheduler-successors`. It compares exact counts
against independent DFS on chains, shared descendants, disconnected/reordered and
seeded DAGs; checks repeatable schedules; rejects cycles, invalid outgoing edges and
duplicate node identities. A 16384-node chain asserts 49149 edge visits / 4194048
word unions, and a 1024-node complete DAG asserts 16368 unions, not a timeout.
The reverse-topological bitset implementation preserves the existing priority
comparison and instruction-index tie-break. No ABI/ISA/managed semantics changed.

Fresh targeted and full smoke exit 0 (43 PASS lines), Doom CoreCLR 26/26 and requested
Release builds pass. Evidence: `TempEnv/doom-scheduler-20260831`, including scripts,
closure report, baseline EventPipe traces and a 217-file verified immutable pack.
Baseline was deliberately stopped in the freshly profiled recursive DFS hot path;
its exit is not a semantic diagnostic. New-pack normal publish retains HCPUB1004
eh-unwind. Diagnostic completed at HCSCF-LINK4003 / allocation BudgetExhausted
(backend 15, publish-graph 31, publish 1). Exact body-world attribution identifies
MapObjectInfo.AddPredefinedTypes in DoomSharp.Core (ordinal 36): 15038 instructions,
9557 values versus allocator limits 4096/8192. Whole-method gate; cctor ingress
IL_000f. The probe's 1913 admitted methods are not completed HCOs. Allocation limits
were not increased; the exact next blocker remains allocation input capacity.
No Doom .hcexe, executable hash pair or ISE execution qualification is claimed.

The separate GUI continuation adds `DoomSharp.HybridCpu.HostTests` (14 passing
image/WAD/admission checks) and an offscreen WPF startup check. These do not run
ManagedPortSmoke again, qualify ISE execution or restart the Doom AOT backend.
The host's `HCDOOMGUI1100` loader/ECALL gate remains closed.

### Current: 2026-08-31 build-only recovery

Compiler/runtime/kernel and Doom Core/Windows/guest Release builds succeeded under
`TempEnv/doom-20260831-rebuild`; its immutable `pack193` has 217 verified file hashes.
ILCompiler/references were restored from the verified saved pack, not rebuilt.
At the user's request, no new smoke/test execution, diagnostic preflight or publish
was launched during recovery. Earlier smoke passed 44 reported groups; CoreCLR
passed 26/26. No new backend blocker or `.hcexe` is established by the rebuild.

### Historical: 1.15.188 — exact framebuffer present and console title

Exact metadata-owned `byte[]` interface matching, initialized-size framebuffer presentation and
bounded Console/CT16 are compiler/kernel linked. Diagnostic publication advances to nullable
managed `IHybridCpuGuestServices.PullInput()` in `HybridCpuGraphics.StartTic:IL_0006`. The trusted
loader input registry and ISE ECALL execution are not qualified; no `.hcexe` was emitted.

### Historical: 1.15.185 — trusted boot blob and non-trap guest process exit

File/BLOB resolves only through a trusted registry of exact byte[] extents rooted for process life;
it never accepts a managed reference from the generic host provider and does not copy registered
payloads. Exact guest ProcessExit uses the existing loader sentinel through a trap-free ABI wrapper.
Diagnostic publication advances through both members to image-owned `IGraphics.ScreenReady(byte[])`;
the narrow interface matcher now needs exact SZ-array signature support. No `.hcexe` was emitted.

### Current: 1.15.183 — exact Doom deadline park/wake

Clock/WAIT converts Doom tics with exact ceiling rounding to the mandatory boot timebase, registers
a kernel deadline and returns an explicit Parked ECALL disposition. Tests prove 44 tics at 1000 Hz
wake exactly at source tick 1258 and never at 1257; reached deadlines resume immediately.
Diagnostic publication advances to `IHybridCpuGuestServices.GetBootBlob(int)` in
`EntryPoint.Run:IL_0029`. Immutable rooted byte[] boot-blob materialization remains required; no
`.hcexe` was emitted.

### Current: 1.15.182 — bounded UTF-16 console service

Console/CW16 uses the exact immutable String 16/20 layout, checks null/negative/1M bounds and
transports only an even mapped read-only UTF-16 payload through a synchronous no-reentry/deferred-GC
ECALL. Invalid buffers never reach the provider. Diagnostic publication advances to
`IHybridCpuGuestServices.WaitUntilDoomTic(int)` in `DeterministicDoomClock.WaitTic:IL_000e`.
Correct deadline parking/resume remains required; no `.hcexe` was emitted.

### Current: 1.15.181 — exact framebuffer initialization service

Graphics/FBIN packs exact Int32 width/height into one u64 ECALL argument. RuntimeKernel validates
one no-buffer request and positive dimensions up to 16384 before provider dispatch. The native HCO
thunk and exact Doom declaration binding advance diagnostic publication to
`IHybridCpuGuestServices.ConsoleWrite(string)` in `HybridCpuConsole.Write:IL_0007`. String
rooting/pinning and bounded UTF-16 buffer transport remain required; no `.hcexe` was emitted.

### Current: 1.15.180 — exact native Doom clock thunk

Kernel boot now requires an explicit virtual-clock frequency. Clock/DOOM performs exact bounded
35 Hz projection, and a native HCO thunk emits one fixed ECALL, returns Int32 on success and routes
service failure to process exit 255. Only the full Doom guest declaration identity is bound.
Diagnostic publication advances to `IHybridCpuGuestServices.InitializeFramebuffer` in
`HybridCpuGraphics.Initialize:IL_0010`. Loader/ISE callback execution remains unqualified; no
`.hcexe` was emitted.

### Current: 1.15.179 — trusted external-service ECALL envelope

The platform contract now defines a fixed user-mode ECALL register envelope for native external
services. RuntimeKernel validates ECALL identity, privilege, live context, enum widths and bounded
arguments, then owns transition-digest signing before entering the existing service boundary.
Clock/MONO success and malformed/foreign/machine-privilege failures are regression tested. Loader/
ISE callback wiring and the guest clock thunk remain required, so Doom still fails closed at the
exact `IHybridCpuGuestServices.GetMonotonicDoomTics` LINK4011 member; no `.hcexe` was emitted.

### Current: 1.15.86 — checked UInt32 division helper

Dynamic UInt32 `div.un` lowers to `__hybridcpu_managed_divide_u4_checked`, a safepoint/unwind-capable
managed runtime helper. A zero divisor raises `System.DivideByZeroException`; no ISA zero-divisor
value can escape. Proven nonzero divisors still use native DIVUW/DIVU. Other dynamic widths and
remainder/signed overflow shapes remain fail-closed. Compiler and runtime smoke cover normal, zero,
native-exclusion and CoreCLR-parity paths.

Doom diagnostic publication advances to `HCCIL1401` for `newarr State` in `State..cctor:IL_0005`.
Standard publication remains closed on `HCPUB1004: eh-unwind`; no `.hcexe` was emitted.

### Current: 1.15.85 — exact single-field scalar signature/field projection

The already-proven four-byte, single-field, reference-free value shape now projects through exact
managed method parameters/returns while retaining its scoped nominal identity. Reading its sole
instance field is an SSA copy: it does not introduce object null-checks, payload memory access or a
general managed-byref lifetime. Cross-module graph call contracts use the same projected carrier.
Aggregate, multi-field and GC-bearing signatures remain closed. `StaticProjectionSmoke` covers the
positive graph/call/field path and the existing aggregate suites retain the negatives.

Doom diagnostic publication advances from `HCCIL1202` at `Angle.op_Division:IL_0001` to
`HCCIL1830` at `IL_0007`, where dynamic unsigned division needs exact managed divide-by-zero
semantics. Standard publication remains closed on `HCPUB1004: eh-unwind`; no `.hcexe` was emitted.

### Current: 1.15.84 — exact static single-field i4 value projection

Static fields whose exact metadata type is a sequential, non-GC value type with one primitive
four-byte instance field now use the existing i4/u4 static load/store helpers. The projection
does not alter the owner TypeDescriptor, physical `BlittableValue` storage, static symbol, cctor
reachability or GC-root metadata. Nested/reference-bearing and instance aggregate fields remain
fail-closed; this is not a general aggregate/byref ABI.

`StaticProjectionSmoke` covers projected cctor stores, getter loads and retained type-init effects.
`NestedGcLayoutSmoke` still requires `HCCIL1209` for a reference-bearing aggregate copy. The full
smoke suite passes. Doom diagnostic publication advances from `HCCIL1209` on `Angle::Angle0` to
`HCCIL1202` at `Angle.op_Division`, `IL_0001`. Standard publication remains closed on
`HCPUB1004: eh-unwind`; no Doom `.hcexe` was produced and no workstream was added.

Run from the HybridCPU repository root (pinned SDK):

```powershell
dotnet run --project Compilers/HybridCPU_Compiler/Tools/ManagedPortSmoke/ManagedPortSmoke.csproj -c Release
```

This executable exercises CIL import and runtime components, **not guest execution**:

- user-defined struct SZARRAY arguments remain object-reference carriers;
- exact closed `System.Array.Empty<T>()` intrinsic with required SZARRAY binding;
- arbitrary MethodSpecs remain fail-closed;
- `ldtoken` FieldRVA, exact PE-byte validation, graph-owned extraction and `InitializeArray` helper resolution;
- immutable runtime FieldRVA registration, size/handle/null validation without partial writes;
- one empty-array singleton per closed type and runtime-owned GC roots.

## Publication checkpoint — 2026-08-30

### Current: 1.15.46 — signed arithmetic, byte conversion and exception constructor closure

Pack `1.15.46-refplan7-phase15`, managed ABI remains `v1.20`. Pack contract digest:
`0fd212149e656cfb941c6d5e90649c7dd52a3202614883e7452c5f168dd377fb`.
Installed immutably at `Diagnostics/HybridCpuAotAddDemo/.hybridcpu-pack-refplan7-phase15-46/`.

ScalarControlFlowV2 now admits signed `div` only with exact nonzero and minimum/-1
overflow exclusion, lowers I4/I8 to DIVW/DIV, admits `conv.u1` as `ANDI 0xff`, and
admits wrapping integer `neg` as `SUB zero,value`. Dynamic/zero/overflow, wrong-type and
underflow cases remain fail-closed. `System.FormatException(string)` is bound to the
existing exception-message runtime helper. ManagedPortSmoke covers CoreCLR boundary
parity plus importer/backend/link behavior.

The Doom diagnostic probe passes those blockers and now stops at the first contract that
cannot be supplied by compiler mapping alone: `stelem.i2` in
`DoomSharp.Core.Data.ByteReader.ReadName`, followed by `System.String(char[])`.
Managed ABI v1.20 has no checked 16-bit array-store or UTF-16-buffer string-construction
helper. No `.hcexe` is emitted. Pack 42 is an intentionally retained rejected immutable
candidate (stale manifest digest); packs 43-46 validate normally.

### Previous: 1.15.38 — bounded switch and complete relational branch family

Pack `1.15.38-refplan7-phase15`, managed ABI remains `v1.20` (`147ba173f099054076045539fab730e0aeb6c42818a675b5babe324fc6edcea4`).
Pack contract digest: `1ff8b6943448ce892e3b4e268c23fe69040d1d26c4607c8ffce356791d94fd8c`.
Installed immutably at `Diagnostics/HybridCpuAotAddDemo/.hybridcpu-pack-refplan7-phase15-38/`.

ScalarControlFlowV2 now decodes a bounded ECMA-335 `switch` table, validates every
target as an instruction boundary, includes all targets plus fallthrough in CFG/SSA,
and lowers each ordered case to an exact BEQ. Target count is capped by the existing
basic-block budget. Branches into local projections remain visible, so projection cannot
erase an address whose consumer is a switch target. Frozen legacy V1 remains unchanged.

The missing unsigned relational forms `bge.un`, `bgt.un`, `ble.un`, and `blt.un`, short
and long, now decode and lower to BGEU/BLTU with exact operand reversal for greater/less-
or-equal forms. New raw-CIL tests cover switch CFG/backend link, wrong selector and stack
underflow, frozen-V1 isolation, and `ble.un.s` BGEU lowering. Full ManagedPortSmoke,
ReleaseQualification, seven Doom tests, Core/guest/WPF builds pass.

The immutable Doom `Patch` payload is now a reference type, avoiding the unsupported
GC-containing `Nullable<Patch>[]` array shape while preserving shallow immutable copy
semantics. Actual Doom metadata smoke verifies `_background` as one object-reference GC
slot. Startup version formatting is specialized to constant protocol byte 109 and uses
the already-qualified three-string concat helper; no byref-local or new native ABI helper
is claimed.

Standard publish still correctly rejects only `eh-unwind` with HCPUB1004. Diagnostic
publication omitting only that requirement passes the former Array.Empty<Nullable<Patch>>,
switch, `ldloca.s` and `ble.un.s` points, then fails closed at:

```text
HCSCF-BODYWORLD1002: System.Environment.get_NewLine():System.Object is not in the v1 helper allowlist.
DoomSharp.Core.DoomGame.Run : IL_010f
Core image digest: bf5f73f852a6d1769399bd61583b0e67ff9924e205691fc26adf8565b612e0b0
```

The next contract must define a deterministic guest newline value as a rooted immutable
UTF-16 string (or an equivalent platform-adapter value); an unrooted compiler literal is
not sufficient. No real `.hcexe` was emitted. Candidates 34-36 remain immutable rejected
packaging experiments (noncanonical owner list or stale pack-contract digest); pack 37
was valid and exposed the unsigned branch blocker.

### Current: 1.15.31 — exact method signatures, aggregate field analysis, native shl

Pack `1.15.31-refplan7-phase15`, managed ABI still `v1.20`.
Contract digest: `8bde17634be8168a3490462b7dd83f110089d64b9b1501c765c43d6baa7cd51a`.
Installed separately at `Diagnostics/HybridCpuAotAddDemo/.hybridcpu-pack-refplan7-phase15-31/`;
the guest default is unchanged, so pass an absolute `HybridCpuSdkPackRoot` ending in `\`.

Call-graph definitions now use the existing exact aggregate signature analysis instead
of rejecting Point parameters before graph traversal. Nested value/receiver identities
retain their enclosing type path. MemberRef overload selection uses a new bounded,
length-framed metadata-signature key **before** ABI carrier selection: nested types,
assembly scopes, enum versus primitive, generic parameters, array shapes and modifiers
are not erased. Single-name lookup is no longer evidence that a signature matches.
This is resolution, not permission to execute pointers, byrefs, generics or CoreLib bodies.
Signature byte budget is 256; recursive TypeSpecs are separately bounded to 16.
Forwarding/equivalence beyond the encoded exact identities remains fail-closed.

Reference-free instance aggregate fields now participate in exact ldfld/stfld dataflow:
wrong struct identity rejects HCCIL1204; valid copies reach the existing HCCIL1810
pre-IR gate. Nested GC aggregate fields still reject HCCIL1209. **Native by-value
aggregate call/copy ABI is not implemented by this change**; no struct becomes an Int64
or object-reference alias. The next ABI work still needs owned storage and copy/lifetime
semantics. Graph traversal progressing past Animation..ctor is not proof it compiled.

Native CIL `shl` is implemented in ScalarControlFlowV2: I4 uses SLLW (truncate/sign-extend),
I8 and native integers use SLL. Existing ISA semantics mask counts by 31/63. Constants
use the shared register materializer. Invalid value/count kinds reject HCCIL1850 and
stack underflow HCCIL1851; frozen legacy V1 is unchanged. CFG proof generation is 7.
No ISA, ISE, runtime production or Doom source changes were made in this iteration.

New tests:

- `AggregateCallSmoke.cs`: cross-assembly First+Point versus Second+Point overloads,
  forged single-name/wrong-signature MemberRef, enum versus int overload selection,
  constructor/field dataflow, incorrect struct stores, and CoreCLR value-copy behavior.
- `ShiftSmoke.cs`: raw CIL without C# count masking, decoded actual HCO SLLW/SLL words
  compared with CoreCLR over I4/I8/native boundary values and negative/oversized counts;
  malformed type/stack negatives, backend/link success and repeated code-byte equality.
- `NativeConstantSmoke.cs`: constant shift by 16 and shared native-word evaluator.

These remain host-side compiler/backend/runtime component tests, not ISE Doom execution.
Candidate 1.15.29 exposed the enum TypeRef-versus-local-enum matching bug during full
publication; exact metadata keys fixed it in 1.15.30. That pack reached Fixed.FromInt's
shl blocker; 1.15.31 includes the verified native shift lowering. All installed candidates
were kept immutable rather than overwritten.

Verified: full ManagedPortSmoke including actual Doom Core, ReleaseQualification build,
Core/WPF/guest builds, all six Doom tests. Standard `dotnet publish -r hybridcpu` still
rejects `HCPUB1004: unsupported managed publish workstream(s): eh-unwind`.
Diagnostic publication omitting **only** that requirement reaches:

```text
HCPUB2002 -> HCCIL1001: CIL opcode 0xfe15 is outside the closed v1 matrix.
DoomSharp.Core.Graphics.RenderEngine..ctor : IL_034a
initobj token 0x02000028 = DoomSharp.Core.Graphics.ClipWallSegment
Source digest: f5a320c5b7faad465a2d516a19461419f3f94717fda9cbbbe73a1e17f4fe6bb3
```

Next work: typed initobj destination/storage lifetime and zero initialization, alongside
the still-pending aggregate call ABI. No real Doom `.hcexe` or two-image SHA-256 result.
Conservative preflight `artifacts/aot-closure-phase15-31.txt` remains 1723 owned methods,
78 external/CoreLib members, 201 types, not a complete compiled CoreLib closure.

Changed sources: `Cil/RestrictedCilImporterV1.cs`, `.AggregatesV1.cs`, `.CallGraphV1.cs`,
`.ControlFlowV2.cs`, `.ReceiverAbiV1.cs`, new `.ExactSignaturesV1.cs`; NativeAot pack
contract/manifest; `Tools/ManagedPortSmoke/Program.cs`, `InlineStorageSmoke.cs`,
`NativeConstantSmoke.cs`, new `AggregateCallSmoke.cs`, `ShiftSmoke.cs`, this README.

### Previous: 1.15.28 — nested GC storage and closed nullable layouts

Pack `1.15.28-refplan7-phase15`, ABI remains `v1.20`.
Contract digest: `422279905a4ad11a7914b72ef45bca1c550c615930c02f71e4df707737e8e2f8`.
Installed separately at `Diagnostics/HybridCpuAotAddDemo/.hybridcpu-pack-refplan7-phase15-28/`.
The guest default pack is unchanged: pass `-p:HybridCpuSdkPackRoot=<absolute-pack-path>\`.
Candidate 1.15.27 was retained unchanged after a regression was caught: interface
definitions have nil BaseType and must still have object-reference storage. The fix
and a dedicated interface-storage regression are included in 1.15.28.

`ManagedStorageResolver` computes target sequential storage recursively, including
exact generic parameter substitution, supported constraints, nullable presence/padding,
and nested GC slots. Resolution uses assembly-scoped identities and metadata-only
CoreLib definitions/forwarders, without admitting their method bodies. Packed/explicit,
byref-like, open, unsupported constraint and recursively sized value layouts remain
closed. SZARRAY fields are references, not recursively inline element payloads.
Signature, nesting, layout and field-count budgets are bounded.

The existing physical-field descriptor contract represents GC-containing aggregates
as reference-free byte spans and exact eight-byte ObjectReference slots. The original
logical field stays `UnsupportedManaged`; synthetic physical identities never become
source-level field bindings. There is no blittable alias or aggregate-copy bypass.
`ldfld`/`stfld` on the logical aggregate still reject `HCCIL1209` pending copy/barrier
and lifetime lowering. No Platform/runtime production, ISA or ISE change was needed.
Compiler metadata encoding now also rejects invalid storage kinds and wrong-width
or wrong-alignment reference slots.

`NestedGcLayoutSmoke.cs` checks nested nullable/struct layouts and static maps,
reference-order determinism, invalid metadata, missing definitions, packed/constraint
negatives, cctor reachability, and CoreCLR nullable-copy reference behavior. It rebuilds
the compiler physical descriptor through the existing runtime type builder and checks
identical descriptor digest, then exercises the real runtime GC: nested instance/static
references survive, pointer-looking primitive bytes do not retain objects, and clearing
the reference slots permits reclamation. Nullable slots are scanned even when hasValue
is false. These are host-side component tests, not guest execution.

With the optional Doom Core argument, the smoke verifies the actual
`IntermissionController._background : Nullable<Patch>` descriptor/cctor and its array
reference slots at target object offsets 120 and 128; it also guards the DoomGame
descriptor against interface-storage regressions.

Verified: complete ManagedPortSmoke suite with actual Doom Core, ReleaseQualification,
Core/WPF/guest builds, and all six Doom tests. Ordinary publication still fails closed
with `HCPUB1004: unsupported managed publish workstream(s): eh-unwind`.
Diagnostic publication omitting **only** that requirement now passes the former
IntermissionController layout/cctor blocker and reaches:

```text
HCPUB2002 -> HCSCF-CALL1001: Managed references, floating point, aggregates and extended signature types are not qualified.
DoomSharp.Core.UI.IntermissionController+Animation..ctor(
    AnimationType type, int period, int frameCount, Point location, int data1)
Importer legacy owner: Animation
Method identity digest: 2e2d152df241107cdd63c762a5c14f9a298bba67250f87cce414e99f7db6b5e2
```

The concrete missing contract is by-value aggregate argument `Point location`
(`IntermissionController+Point`, readonly struct with int X/Y), not floating-point
support. A receiver loan alone does not provide caller-owned aggregate argument
storage/copy semantics. Next work is exact aggregate call ABI, preserving the existing
pre-IR/receiver image gates and nested type identity, not treating Point as Int64.
No Doom `.hcexe` exists and two-image SHA-256 determinism cannot yet be checked.
Conservative preflight `artifacts/aot-closure-phase15-28.txt`: 1723 owned methods,
78 external/CoreLib members, 201 types; this is not an exact compiled CoreLib closure.

Changed sources: new `Cil/RestrictedCilImporterV1.ManagedStorageV1.cs`,
`Cil/RestrictedCilImporterV1.MetadataBindingsV1.cs`, `.ClosedInterfacesV1.cs`,
`Core/Target/Managed/HybridCpuManagedTypeMetadataV1.cs`, NativeAot pack contract/manifest;
new `Tools/ManagedPortSmoke/NestedGcLayoutSmoke.cs`, `InlineStorageSmoke.cs`,
`ReceiverAbiSmoke.cs` (remove obsolete strict-layout diagnostic), `Program.cs`, this README.

### Previous: 1.15.26 — typed receiver loans and lossless native constants

Pack `1.15.26-refplan7-phase15`, ABI remains `v1.20`.
Contract digest: `679c2b6ebd96861becfb7dcbf37396212c7dd2f8ae2a514621d1f2c57e966f21`.

Value-type instance definitions now use an exact assembly-scoped `T&` signature and
`ManagedByRef` IR/value classes. A bounded nonescaping, no-safepoint receiver loan is
admitted for reference-free, non-generic sequential payloads. Primitive field accesses
validate their complete PE layout and retain payload-relative offsets (no object header
or object null-check helper). Loads/stores cover 1/2/4/8 bytes with exact signedness and
I4 stack normalization. Readonly stores are allowed only in the declaring constructor.
CFG joins retain exact receiver identity. Calls, loops/safepoints, EH, GC-containing/packed
payloads, foreign fields and object-reference substitutes remain closed.

`ManagedReceiverAbiPlanV1` states callee preconditions, not caller proof. General byref
calls reject `HCCIL1843`; image linking rejects loan-bearing IR with `HCSCF-LINK4012`
until caller-owned storage, non-null bounds and lifetime are proven. Removing the plan
does not remove that IR gate. General aggregate temporaries/returns and GC byref maps
remain incomplete. This is not full struct-call support.

During native-word verification another correctness bug was found: a wide `Constant`
operand on `ADDI` was truncated to the encoder's signed 16-bit immediate, and constants
on register-only operations did not materialize a source register. New V2 normalization
materializes exact 64-bit values before RA using bounded ADDI/SLLI/ORI sequences, uses x0
for register zero, and converts out-of-range immediate operations to register forms.
Expansion is bounded; CFG target mapping occurs afterwards. I8 constant-to-UInt64 return
compatibility and same-width signed/unsigned wrap arithmetic now follow CIL stack bits.
This fixes actual encoded constants, including the prior wide DIVU/DIVUW divisor cases;
the older IR-only checks did not establish this property. General CIL `shl` remains
outside the decoder matrix. No ISA or ISE change.

New `ReceiverAbiSmoke.cs` checks exact payload offsets/widths against CoreCLR, untouched
neighbor/padding bytes, managed-byref IR identity, readonly/type/GC/call/loop negatives,
scheduler/register allocator/encoder/HCO success and deterministic code bytes. An optional
Core assembly argument additionally imports the actual `DoomSharp.Core.Fixed..ctor`
with metadata/cctor bindings and verifies its emitted four-byte payload store:

```powershell
dotnet run --project Compilers/HybridCPU_Compiler/Tools/ManagedPortSmoke/ManagedPortSmoke.csproj -c Release -- DoomSharpHybridCPU/src/DoomSharp.Core/bin/Release/net8.0/DoomSharp.Core.dll
```

`NativeConstantSmoke.cs` evaluates decoded HCO `.text` instruction words with pre-bundle
register snapshots against CoreCLR: int.MinValue/MaxValue, full I8 literals, wide addition,
masks and unsigned 32/64-bit division. These are compiler/backend and ISA-word semantic
tests, not execution in ISE or a native Doom run.

Verified: all compiler smoke cases, ReleaseQualification build, Core/WPF/guest builds,
and all six Doom smoke cases pass. Standard publish still rejects `HCPUB1004: eh-unwind`.
Diagnostic publication omitting only that requirement passes the old Fixed receiver gate
and reaches this next graph blocker:

```text
HCPUB2002 -> HCCIL1016: Declaring types with a static constructor are not qualified.
DoomSharp.Core.UI.IntermissionController..ctorinstance(System.Object):System.Void
First missing field layout: IntermissionController._background
Field type: System.Nullable<DoomSharp.Core.Graphics.Patch>
```

The metadata field resolver confirms `_background` is the first failed layout. `Patch`
contains `uint[] ColumnOffsets` and `Column?[] Columns`: the next work is constructed
value-type storage plus nested GC reference maps, not blittable aliasing. No Doom
`.hcexe` was emitted; image SHA-256 determinism is still unverified. Conservative
preflight (`artifacts/aot-closure-phase15-26.txt`) remains 1723 owned methods, 78 external
members and 201 types, not a complete compiled CoreLib closure.

Changed sources: new `RestrictedCilImporterV1.ReceiverAbiV1.cs` and `.ConstantsV2.cs`,
CIL import contracts/signatures/value kinds, aggregate argument metadata, V2 transfer,
lowering/evidence, inline storage access, graph validation and linker gates; SDK version/
manifest; new receiver/native-constant smoke files, updated closed-interface/unsigned-
division tests and smoke entry. Doom/ISE/ISA production sources unchanged.

### Previous: 1.15.25 — closed-interface declaration metadata and separate reference inputs

Pack `1.15.25-refplan7-phase15`, ABI remains `v1.20`.
Contract digest: `baab619a83140222e389d9a6b76308d851d2c8784dd4b27bc4b73e5e9d35cf7d`.

The adapter now passes digest-bound CoreLib/System.Runtime/mscorlib metadata from the
validated SDK pack into a separate `MetadataOnlyModules` input. This has its own 32 MiB /
64-module limit and never contributes executable bodies or presented-method authority.
Graph evidence includes sorted input digests and required closed-interface plan digests.

The metadata builder resolves bounded generic interface TypeSpecs, assembly forwarders,
and exact VAR substitutions. Constructed interface and named argument identities are
assembly scoped. It retains the owner's interface IDs and cctor binding instead of
dropping the interface. Ambiguous legacy unscoped owner keys are rejected. Open definitions,
constraints, inherited generic interfaces, DIM bodies, generic methods and byref-like
arguments remain unqualified. CoreLib's `AllowByRefLike` parameter flag permits ordinary
arguments but does not enable ref-struct arguments or calls. Primitive identities are
canonical across primitive encodings and resolved CoreLib TypeRefs.

`ManagedClosedInterfacePlanV1` is declaration evidence, not executable dispatch metadata.
Only plans needed by reachable owners/field accesses are retained; unused declarations
do not block a scalar graph. Image linking rejects retained plans with `HCSCF-LINK4011`
until exact implementation slots and type registrations exist. Value-type instance methods
now explicitly reject `HCCIL1840` rather than using an ObjectReference receiver ABI.

New `ClosedInterfaceSmoke.cs` verifies real CoreLib forwarding/VAR substitution, retained
cctor, exact owner layout and interface identity, cross-assembly same-name separation,
deterministic reference ordering, missing forwarder target, duplicate/budget guards,
DIM/open/constraint/ref-struct negatives, receiver and linker gates, and CoreCLR behavior.
It proves that a metadata-only callee rejects `HCSCF-BODYWORLD1002` and that the same
callee succeeds only after explicit body-world admission. `MetadataInputSmoke.cs` checks
adapter digest, duplicate/count/JSON rejection and provenance, restoring process environment.

Verified: compiler smoke (all cases), ReleaseQualification build, Core/WPF/guest builds,
and all six Doom smoke cases pass. Standard publish remains correctly rejected with
`HCPUB1004: unsupported managed publish workstream(s): eh-unwind`. Diagnostic publication
with only that requirement omitted now passes the old `HCCIL1016` and stops at:

```text
HCPUB2002 -> HCCIL1840:
Value-type instance methods require an exact managed-byref receiver ABI;
ObjectReference this is not valid.
DoomSharp.Core.Fixed..ctorinstance(System.Object,System.Int32):System.Void
```

The next compiler work is the exact value-type receiver/address/lifetime and call ABI,
not a cast of struct payloads to object references. Full generic interface dispatch and
EH image publication are still incomplete. No Doom `.hcexe` or image SHA-256 determinism
claim. Preflight remains a conservative scan, not a compiled CoreLib closure.

Changed sources: `RestrictedCilImporterV1.ClosedInterfacesV1.cs`, metadata bindings,
body-world/graph contracts and evidence, method-envelope receiver guard, linker gate,
NativeAot adapter metadata input, SDK version/manifest, the two new smoke files,
`ManagedPortSmoke.Dependency/MetadataOnlyCallee.cs`, smoke entry/project and existing
inline-storage test invocation. No Doom/ISE/ISA production source changes in this step.

### Previous: 1.15.24 — proven-nonzero unsigned division

Pack `1.15.24-refplan7-phase15`, ABI remains `v1.20`.
Contract digest: `443f4c8bcf8ec6193122b39c21a80c46586ec7bbc571998479c6bf4c6912d93a`.

V2 imports `div.un` for matching I4 or I8 stack families only when the divisor is
an exact nonzero constant. Lowering uses existing `DIVUW` for I4 and `DIVU` for I8,
with explicit constant materialization. A zero or unproven divisor rejects with
`HCCIL1830`: the ISA zero-divisor result is not managed `DivideByZeroException`
semantics. Mixed operand widths reject with `HCCIL1831`. ISA and ISE are unchanged.
CFG evidence generation 4 includes this proof requirement; general division and
`conv.i8` remain unqualified. No helper or runtime ABI was added.

`UnsignedDivisionSmoke.cs` checks emitted width/opcodes, actual graph/backend/link
success, an evaluator of the emitted arithmetic fragment against CoreCLR at unsigned
boundaries (including poisoned high I4 bits), and dynamic/literal-zero rejection.
These are component/IR checks, not native guest execution.

Verified: all compiler smoke cases, ReleaseQualification, Core/WPF and guest builds,
and all six Doom smoke cases pass. Standard publish still fails closed with
`HCPUB1004: unsupported managed publish workstream(s): eh-unwind`.
Diagnostic publication, omitting only this requirement for investigation, reaches:

```text
HCPUB2002 -> HCCIL1016: Declaring types with a static constructor are not qualified.
DoomSharp.Core.Fixed..ctorinstance(System.Object,System.Int32):System.Void
```

The next work is full type/cctor binding for `Fixed : IComparable<Fixed>` and its
closed-generic interface metadata, followed by the value-type receiver ABI; do not
bypass the cctor check or treat the displayed Object receiver as a qualified struct
ABI. Conservative Doom preflight reports 1723 owned methods, 78 external/CoreLib
members and 201 types (`artifacts/aot-closure-phase15-24.txt`); this scan is not an
exact compiled CoreLib closure. No Doom `.hcexe` or image determinism claim.

Changed sources: new `RestrictedCilImporterV1.UnsignedDivisionV1.cs`, V2
transfer/lowering/opcode evidence, new `UnsignedDivisionSmoke.cs`, smoke entry,
SDK version/manifest and this report. No Doom/ISE/ISA source changes in this step.

### Previous: 1.15.23 — nonescaping static scalar getter projection

Pack `1.15.23-refplan7-phase15`, ABI remains `v1.20`.
Contract digest: `306cc790a2380b16dcf87b6ccc143458edf1bcb25e918b2835d594ac88b34ca1`.

Added a bounded compiler optimization for adjacent `ldsflda; call`: the static field must
be readonly, its owner and value/getter type must be the same local non-generic value type,
the payload must be one four-byte field, and the complete nonsynchronized/nonvirtual getter
must be exactly `ldarg.0; ldfld; ret`. No caller/getter EH or branch into the consumed call
is allowed. This produces a projected static I4 read, not a materialized managed byref.
The exact runtime field layout is still validated. UInt32 return accepts the same I4 bits.

Reachability explicitly retains the projected owner's cctor after removing the getter call.
Lowering retains `ensure_type_initialized` followed by `static_load_i4`. General `ldsflda`
remains closed with `HCCIL1820`; mutable fields and getters with computation/effects are
not projected. CFG evidence generation 3 records the new projection/cctor contract.

`StaticProjectionSmoke.cs` tests actual graph/helper output, retained cctor reachability,
signed/unsigned consumer returns, mutable/computing/side-effect negatives, and CoreCLR
initialization behavior. These remain component tests, not native execution.

The Doom full-body decoder now reaches:

```text
HCCIL1001: CIL opcode 0x005c is outside the closed v1 matrix.
DoomSharp.Core.GameLogic.Player..cctor: IL_0018
div.un — Angle.Angle90.Value / 18
```

The whole Doom method has not reached lowering; unsigned division is the next decode/lowering
work. General byref/value-type receiver ABI, static runtime image registration, helper linkage
and EH remain incomplete. Compiler smoke, ReleaseQualification, Core/WPF and guest builds,
and six Doom smoke tests pass. Standard publish rejects `HCPUB1004: eh-unwind`.
No `.hcexe` or image SHA-256 determinism claim.

Changed sources: new `RestrictedCilImporterV1.StaticProjectionV1.cs`, decoder/field lookup,
call-graph initializer reachability, V2 projection/guard/evidence, `StaticProjectionSmoke.cs`,
smoke entry, SDK version/manifest and this report. Doom/ISE/ISA sources unchanged.

### Previous: 1.15.22 — checked byte-array operations

Pack `1.15.22-refplan7-phase15`, ABI `v1.20`.
Contract digest: `5337851484ef8fff20fba3babc6aca75890dcfe60282e5d9776776645bb78cfd`.

Added V2 decoding, type checks and helper lowering for `stelem.i1`, `ldelem.i1` and
`ldelem.u1`; the legacy scalar matrix is unchanged. The three corresponding runtime-helper
ABI entries use int32 stack operands/results and a checked one-byte primitive SZARRAY.
Stores truncate to the low byte, loads sign-extend or zero-extend respectively. Runtime
operations read only the length and selected element, with no whole-array snapshot or
guest allocation. Null, width, malformed extent and index validation precede writes.

`ByteArraySmoke.cs` verifies emitted helper symbols and tests integer boundaries, adjacent
bytes, signed/unsigned loads, atomic rejected writes, null/empty/width checks. A CoreCLR
DynamicMethod executes raw `stelem.i1`/`ldelem.u1` on bool[] and confirms that noncanonical
boolean bytes must not be normalized to 1. All compiler/runtime smoke tests pass.

Diagnostic Doom graph publication moves past `PlayerReborn` and now reports:

```text
HCCIL1001: CIL opcode 0x007f is outside the closed v1 matrix.
DoomSharp.Core.GameLogic.Player..cctor: IL_000C
ldsflda DoomSharp.Core.Angle.Angle90
```

The operand is resolved from the current Core PE. Next work requires typed static-field
managed-byref provenance/lifetime and value-type receiver semantics, not an integer address.
Compiler ReleaseQualification, Core/WPF and guest builds and six Doom smoke tests pass.
Full publication remains closed on `HCPUB1004: eh-unwind`; final runtime helper linkage,
bootstrap metadata registration and aggregate ABI/lifetime are still incomplete.
No Doom `.hcexe` exists and image SHA-256 determinism is unverified.

Changed files: V2 importer, managed ABI contracts and release ABI identity, new
`ManagedByteArrayRuntimeV1.cs`, new `ByteArraySmoke.cs`, smoke entry, SDK version/manifest
and this report. No Doom/ISE/ISA source changes.

### Previous: 1.15.21 — Array.Empty with an exact local value element

Pack `1.15.21-refplan7-phase15`, ABI remains `v1.19`.
Contract digest: `dd15f13f7c70123a6379581219e919e06baa97d49a985c9df0fe13849f0a3feb`.

`Array.Empty<T>` now parses a local non-generic, non-byref-like value element independently
of the general scalar-generic MethodSpec parser. Its result remains an array reference;
T never becomes a scalar parameter/return carrier. Element identity is assembly-scoped.
The existing inline-storage resolver derives payload size/alignment from the actual PE.
An explicit array binding must supply a matching value descriptor, exact token/identity,
TypeId, reference-free payload and matching array stride. Missing or inconsistent bindings
fail `HCCIL1462`. External/constructed/GC-bearing or unsupported-layout value elements remain
closed rather than receiving a guessed descriptor.

`EmptyValueArraySmoke.cs` verifies direct import and call-graph success with exact bindings,
missing descriptors, token/TypeId mismatch, internally consistent but PE-inconsistent
width/alignment, CoreCLR empty singleton behavior, and continued rejection of unrelated
struct-generic calls. The runtime Empty/GC tests from prior steps remain green. These are
compiler/runtime component checks, not native guest execution.

Diagnostic Doom graph traversal passes `Array.Empty<Vertex>()` and now stops at:

```text
HCCIL1001: CIL opcode 0x009c is outside the closed v1 matrix.
DoomSharp.Core.GameLogic.GameController.PlayerReborn: IL_00AB
stelem.i1 — WeaponOwned bool[] initialization
```

This advances graph discovery, not final lowering of every previously visited method.
Automatic final image registration of array descriptors and aggregate ABI/lifetime lowering
are still unfinished. Compiler smoke, ReleaseQualification, Core/WPF and guest builds,
and six Doom smoke tests pass. Standard publish still rejects `HCPUB1004: eh-unwind`.
No `.hcexe` was produced; image SHA-256 determinism cannot yet be checked.

Changed files: array intrinsic resolver, reusable inline-storage resolver, array binding
contract/ResolvedHelper metadata, V2 intrinsic binding lookup, `EmptyValueArraySmoke.cs`,
smoke entry, SDK version/manifest and this report. No Doom/ISE/ISA source modifications.

### Previous: 1.15.20 — exact aggregate dataflow, not aggregate ABI

Pack `1.15.20-refplan7-phase15`, ABI remains `v1.19`.
Contract digest: `4a0514e87f8e64391facb20dc14b1316bef50406b9fad62dbb0b0f7e5f13d1b7`.

Direct V2 method import now retains a distinct Aggregate marker plus scoped type identity
in method/local signatures and abstract stack values. Local assignment, CFG phi joins,
return and typed `stelem` compare exact identities, not byte widths. `dup` retains identity.
The scalar legacy parser and general managed-call/generic signatures remain closed.
Unknown aggregate producers and user-supplied scalar-style aggregate bindings are rejected.

All analyzed aggregate paths stop at `HCCIL1810` **before IR and GC-map construction**:
there is no value-buffer lifetime, call ABI or typed element-copy lowering yet. This is
not executable support for `stelem Fixed`. The CFG evidence generation is now 2 and
explicitly names the analysis-only/pre-IR boundary. Also fixed `pop`, previously decoded
but missing transfer and lowering; scalar pop now imports successfully.

`AggregateFlowSmoke.cs` generates real CIL PEs with valid and deliberately invalid
same-sized value types. Tests cover argument/local/dup/phi/return/stelem propagation,
wrong-type stores/joins/returns, arithmetic rejection, pop underflow, unused aggregate ABI
arguments and preservation of scoped method provenance. All compiler smoke tests pass.

Full-body decoding now passes the former opcode `0xA4` barrier. Diagnostic Doom graph
publication next stops earlier in IL order during call-graph traversal:

```text
HCSCF-BODYWORLD1002: MethodSpec contains an open, aggregate, byref or unsupported exact argument
DoomSharp.Core.GameLogic.GameController..ctor: IL_010D
System.Array.Empty<DoomSharp.Core.Graphics.Vertex>()
```

The member was independently resolved from the current Core PE. The intrinsic returns
an array reference and needs an exact element/type-descriptor binding; it must not treat
the struct as a scalar generic carrier. The aggregate ABI/lifetime gate remains additional
unfinished work, as do runtime bootstrap/helper linkage and EH.

Compiler ReleaseQualification, Core/WPF and guest builds, compiler smoke and all six Doom
smoke tests pass. Standard `dotnet publish -r hybridcpu` rejects `HCPUB1004: eh-unwind`.
No `.hcexe` was produced and image hash determinism remains unverified.

Changed sources: new `RestrictedCilImporterV1.AggregatesV1.cs`, importer signatures/decoder,
V2 dataflow/pop/pre-IR guard, CIL type marker, `AggregateFlowSmoke.cs` and smoke entry,
SDK version/manifest and this report. No Doom, ISE or ISA source edits.

### Previous runtime prerequisite: exact value SZARRAY copies

SDK remains `1.15.19-refplan7-phase15`; this step adds runtime component operations,
not CIL qualification, an ABI helper, or image publication.

`ManagedTypeSystemV1` now requires a real element TypeId for `BlittableValue` arrays
and validates its reference-free value payload, size and alignment against the complete
runtime type set. Anonymous, missing, reference-containing and shape-mismatched elements
fail closed. Reference and primitive array contracts remain distinct.

`ManagedValueArrayRuntimeV1.LoadValue/StoreValue` copy one exact value into/from a
caller-owned buffer. They preserve value semantics, reject same-sized unrelated types,
validate null/bounds/allocation extent and buffer width before mutation, and do not box
or allocate guest objects. The heap's bounded `TryReadObjectBytes` avoids whole-array
snapshots for these accesses. Zero-length allocation respects the fixed descriptor prefix.

`ValueArraySmoke.cs` tests 4-byte Fixed-like values against CoreCLR copies, 12-byte
non-scalar payloads, adjacent-element preservation, no aliasing, malformed lengths,
overflow, atomic failure, exact identities, missing/GC-bearing types, GC reachability
without interpreting opaque bits as pointers, and deterministic component bytes across
two fresh runtimes. These hashes are **not image or Doom frame hashes**.

Compiler smoke, compiler ReleaseQualification/adapter builds, WPF/Core and guest managed
builds (via Doom tests), and six Doom smoke tests pass. Repeated diagnostic publication
still reports `HCCIL1001` at `GameController..ctor: IL_0222`, `stelem Fixed`.
The next compiler step must preserve exact aggregate identity and value lifetime through
signatures, evaluation stack and merges, then connect typed element-copy lowering.
`RestrictedCilTypeV1`/`V2Value` currently only model scalar carriers; converting Fixed to
Int32 would erase required identity. No guard or `PublishQualifiedWorkstreams` was relaxed.
No `.hcexe` was produced.

Changed files in this prerequisite: runtime type-system builder, core-shape allocation,
heap bounded read, new `ManagedValueArrayRuntimeV1.cs`, new `ValueArraySmoke.cs`, smoke
entry and this report. No ISE/ISA or Doom source edits.

### Previous: 1.15.19

Pack `1.15.19-refplan7-phase15`, managed ABI remains `v1.19`.
Contract digest: `d5be3901a5e51be4f8a4dfbd67a4bc1eac4a27394dd3bbf9f15f3c07bb4c33fc`.

Added independent, bounded metadata storage calculation for sequential inline value types
without references, including nested structs, enums, empty payload sizing and self-typed
static fields. Methods, generic interfaces and static initialization do not determine the
payload size. Metadata uses the existing `BlittableValue` storage contract; it does not
pretend these aggregates are scalar values. Aggregate field access fails `HCCIL1209` until
aggregate/byref lowering exists. Reference-containing, explicit and packed inline payloads
remain closed; nested GC maps are not silently omitted.

`InlineStorageSmoke.cs` checks payload size against CoreCLR `Marshal.SizeOf`, explicit
HybridCPU offsets/alignment, following reference visibility, cctor reachability, self-static
storage and negative layout/scalarization cases. All compiler smoke tests pass.

Diagnostic Doom publication advances beyond the `GameController` layout/cctor guard to:

```text
HCCIL1001: CIL opcode 0x00a4 is outside the closed v1 matrix.
DoomSharp.Core.GameLogic.GameController..ctor: IL_0222
stelem DoomSharp.Core.Fixed (metadata token 0x02000009)
```

The operand was independently resolved from the current Doom Core PE. The next work is
value-type stack/byref and SZARRAY element copy semantics, not integer substitution for
`Fixed`. Runtime image metadata registration, helper linkage and EH remain incomplete.
Compiler ReleaseQualification, Core/WPF, guest managed builds and six Doom smoke tests pass.
Standard publish still rejects `HCPUB1004: eh-unwind`. No Doom `.hcexe` or image determinism
claim. ISE/ISA and Doom sources were not changed in this iteration.

Changed sources: `RestrictedCilImporterV1.InlineStorageV1.cs`, metadata binding builder,
field-access guard, `InlineStorageSmoke.cs`/smoke entry, SDK version/manifest and this report.

### Previous: 1.15.18

Pack `1.15.18-refplan7-phase15`, managed ABI remains `v1.19`.
Contract digest: `f6c8ab09985fff6649fd92489941653493c67330bc57c1756afab05b9692b700`.

The graph now carries an exact `System.Exception.get_Message` slot plan, including
owned overrides and inherited implementations across dependency modules. Tests cover
hidden newslot isolation, cross-module body reachability, deterministic plan digest,
slot-ID/runtime-vtable parity, and rejection of single-method unproven devirtualization.
This is not runtime dispatch publication: the object linker rejects unmaterialized
plans with `HCSCF-LINK4010` until final runtime addresses and bootstrap registrations exist.

Diagnostic Doom publication (only the EH requirement omitted) advances to:

```text
HCCIL1016: Declaring types with a static constructor are not qualified.
DoomSharp.Core.GameLogic.GameController..ctor
```

Metadata initializer bindings currently require a complete type descriptor. The metadata
layout builder only accepts scalar/reference field storage; `GameController` contains
inline `DoomSharp.Core.Fixed` fields such as `_blockMapOriginX` and static `StopSpeed`.
Inline value-type layout, including alignment and nested GC references, must be qualified
before this guard can be removed. No cctor guard was bypassed.

Compiler smoke, ReleaseQualification build, Core/WPF build, guest managed build and all
six Doom smoke tests pass. Conservative preflight remains 1723 owned methods, 78 external
members and 201 types, not a proof of full CoreLib closure. Standard publication remains
closed on `HCPUB1004: eh-unwind`. No Doom `.hcexe` was produced; image SHA-256 comparison
cannot yet be performed. `PublishQualifiedWorkstreams` was not expanded.

Changed source groups: CIL contracts/importer/call graph, new
`RestrictedCilImporterV1.ExceptionDispatchV1.cs`, object-linker fail-closed gate, compiler-owned
smoke tests and dependency fixture, SDK version/pack manifest. ISE/ISA sources unchanged.

### Previous: 1.15.17

Pack `1.15.17-refplan7-phase15`, managed ABI `v1.19`:
`e4c0707f66fbaadd944de7b21b2e62b5256e2cf9c227f48f48b753c0dc69c15c` (contract digest).

Added exact CoreLib `ArgumentNullException(string paramName)` helper/newobj lowering and
`ManagedArgumentNullExceptionRuntimeV1`. Tests compare invariant CoreCLR Message, ParamName,
HRESULT, raw UTF-16 and inherited GC references. Other constructors, localized resources and
automatic image-level exception dispatch are not qualified by this addition. Invalid receiver
and non-string parameter inputs fail without mutating the receiver. Empty runtime string
allocation now respects the descriptor's aligned fixed prefix.

A separate correctness fix rejects unproven devirtualization of `callvirt Exception.Message`
to the base-field helper. The diagnostic Doom publication now stops at:

```text
HCCIL1501: System.Exception.get_Message requires an exact virtual/interface dispatch binding
DoomSharp.HybridCpu.Guest.EntryPoint.Run: IL_006E
```

The next step is exact virtual-slot/override reachability and runtime dispatch for this call;
reading only `Exception._message` would silently drop `ArgumentException.ParamName` formatting.
The constructor helper is tested independently because this newly exposed dispatch failure
occurs earlier in the Doom graph traversal. `PublishQualifiedWorkstreams` was not expanded.

Compiler/runtime smoke, compiler ReleaseQualification build, WPF/Core build, guest managed
build and six Doom smoke tests pass. Standard publish still fails `HCPUB1004: eh-unwind`.
No Doom `.hcexe` exists and image SHA-256 determinism remains unverified.

Changed sources in this iteration: CIL importer, call-graph diagnostics, constructor lowering,
helper and ABI contracts, SDK identity/manifest, release ABI identity, runtime string allocation,
new argument-null runtime implementation and `ArgumentNullSmoke.cs`.

### Previous: 1.15.15

Immutable SDK pack: `1.15.15-refplan7-phase15`, managed ABI `v1.18`.
Pack contract SHA-256: `487efb9c878b74c54dac707c65ea8973d2e80f0fd38bd45070b2d1367216d340`.

Standard Doom `dotnet publish -r hybridcpu` correctly fails `HCPUB1004: eh-unwind`.
Diagnostic graph publication, with only `eh-unwind` removed from the requested workstreams,
now reaches:

```text
HCSCF-BODYWORLD1002
System.ArgumentNullException..ctor(System.Object,System.Object):System.Void
DoomSharp.Core.DoomGame.SetClock: IL_000A
```

The two carriers are the exception receiver and `string paramName`. This must not be
aliased to `Exception(string message)`: parameter-name storage, default message, exception
type and HRESULT semantics need their own qualified contract.

No Doom `.hcexe` was produced. Image SHA-256 determinism is consequently unverified.
Compiler ReleaseQualification, WPF/Core and guest managed builds and all six Doom smoke
tests passed. The conservative preflight report has 1723 owned methods, 78 external/CoreLib
members and 201 referenced types; it is not proof that the external CoreLib closure compiles.

Remaining image-level work includes exact allocation/SZARRAY/string bindings, FieldRVA
registration in the final image bootstrap, sticky cctor invocation, runtime-helper linkage,
and EH lowering. Passing these component/import tests does not qualify those image contracts.
# 2026-09-01 Doom AOT scalability and exact-ingress continuation

`SchedulerSuccessorSmoke` and `AllocationCapacitySmoke` cover exact bounded descendant
counting, weighted control/serialization frontier equivalence, 9004-instruction allocation,
streamed proof digests, synchronized 8192 HCMG/runtime safepoints and fail-closed bounds.
`ArgumentStoreSmoke` additionally proves that CIL backedges skip the synthetic argument prolog,
the exact fixed-register ingress lifetime ends at its sole copy-use, and GC-map finalization
does not require an unrelated loop-optimization MII proof. Full ManagedPortSmoke and Doom
CoreCLR 26/26 pass in `TempEnv/doom-rootmap-loop-20260901`.

The current diagnostic terminal blocker is HCSCF-LINK4003 in
`DoomSharp.Core.GameLogic.GameController..ctorinstance(System.Object):System.Void`
(IR 579/value 496): `il_0018:null-check-arg-abi:value` cannot satisfy the native call/return
location contract. Normal publication remains HCPUB1004 `eh-unwind`; no Doom `.hcexe` exists.
# 2026-09-01 diagnostic checkpoint

Regression coverage now includes exact closed-CFG frame classification (ordinary return,
zero-exit infinite CFG, and fail-closed terminal-without-transfer), deterministic undefined
HCO symbols for relocated guest-service calls, EH-thunk exclusion, and detailed HCOBJ0012
relocation diagnostics. Full ManagedPortSmoke and the complete Doom preflight pass. The
diagnostic publish progressed to `ManagedDispatchTypeObjectV1.Emit` and now stops at
`Dispatch type 7 lacks its exact descriptor` (adapter exit `-532462766`, publish exit 1).
Normal publication remains gated by `HCPUB1004` for `eh-unwind`; no Doom `.hcexe` exists.
# 2026-09-01 dispatch ownership checkpoint

ManagedPortSmoke now proves that exact `runtime-service` dispatch candidates do not enter
image type/table closure, while mixed service/image candidates fail closed. The full Doom
preflight passes and diagnostic publication advances to `HCSCF-LINK4005 / HCLINK1003`:
undefined `__hybridcpu_managed_argument_exception_get_message`. This symbol currently has
an ABI/dispatch declaration but no native object implementation; exact ArgumentException
message and ParamName behavior remains the next blocker. No Doom `.hcexe` exists.
# 2026-09-01 exception getter checkpoint

ManagedPortSmoke now covers the descriptor-bound native `System.Exception.get_Message`
HCO definition and fail-closed malformed field offsets. Full preflight passes. Diagnostic
publish now stops at exact `HCSCF-LINK4015`: native `System.ArgumentException.get_Message`
must allocate/concatenate the invariant ParamName suffix; the base field getter is not a
semantic substitute. No Doom `.hcexe` exists.

# 2026-09-01 image/bootstrap heap-consumer checkpoint

ManagedPortSmoke now requires exact `HCSCF-LINK4016` evidence when the reachable virtual
slot needs allocating `System.ArgumentException.get_Message`: there is no production
loader consumer that installs the managed heap, type system, GC safepoints and allocating
helper bindings from the image bootstrap descriptor. A native symbol alone and host-side
ManagedRuntime construction are explicitly rejected as substitutes. The full smoke run
passes. Fresh full preflight also passes Doom CoreCLR 26/26, all requested Release builds,
the verified immutable 217-file pack and closure 1937/37/202. Diagnostic publish reaches
`HCSCF-LINK4016` with outer exit 1 / adapter exit 15; no HybridCPU guest execution or Doom
`.hcexe` is claimed.

# 2026-09-01 guest-visible heap backing checkpoint

`HybridCpuManagedHeapAllocatorV1` now uses an explicit bounded memory-backing contract.
ManagedPortSmoke covers exact range identity, object-header visibility and a mismatched
backing rejection. The production ISE adapter binds the same contract to an explicitly
supplied `Processor.MainMemoryArea`; a separate production-assembly probe verifies the
guest-visible bytes and range failures. Full smoke and Doom preflight pass. Bootstrap
orchestration/helper binding remains the exact `HCSCF-LINK4016` blocker; no `.hcexe` is
claimed.

# 2026-09-01 production loader and CoreLib Message-body checkpoint

`HybridCpuIseManagedImageLoaderV1` now provides a production ISE consumer for an already
inspected managed image. It materializes bytes into one explicit `Processor.MainMemoryArea`,
boots RuntimeKernel, installs exact image type metadata, binds the guest-visible heap,
constructs string and non-moving GC runtimes, and runs the bootstrap contract. Its result
explicitly denies CPU/ECALL execution authority. The production probe under
`TempEnv/ise-managed-image-loader-probe-20260901` verifies exact image bytes, a guest-visible
allocated object header, and fail-closed image/heap overlap rejection.

The former `HCSCF-LINK4016` consumer gate is closed. The next two diagnostic runs exposed and
closed an incorrect synthetic dispatch symbol, then established the exact remaining blocker.
Fresh `TempEnv/doom-corelib-message-body-20260901` passes full ManagedPortSmoke, Doom CoreCLR
26/26, Core/Windows/guest Release, compiler/runtime/kernel builds, a verified immutable
217-file manifest pack (218 physical files including the manifest), and closure 1937 owned /
37 external / 202 referenced types. Diagnostic publish exits outer 1 / ILCompiler 1 / adapter
15 at `HCSCF-LINK4017`: virtual dispatch requires executable CoreLib
`System.ArgumentException.get_Message`, but the current CoreLib input is metadata-only and
supplies neither that managed body nor an exact allocating runtime implementation. The base
`System.Exception::_message` getter is not a semantic substitute because it omits parameter-name
formatting. Normal publish remains outer 1 / adapter 25 at `HCPUB1004: eh-unwind`. No Doom
`.hcexe` or clean-publish SHA-256 exists.

# 2026-09-01 exact ArgumentException.Message helper-contract checkpoint

A probe against the exact pack CoreLib identifies `System.ArgumentException.get_Message` as
method token `0x06000c8d`. Direct ScalarControlFlowV2 import first rejects its call to
`System.ArgumentException.SetMessageField`; importing the CoreLib body world reaches unsupported
`ldloca.s` in the formatting closure. Doom closure contains only
`ArgumentNullException(string)` and `ArgumentOutOfRangeException(string)`; their qualified
runtime constructors already install the invariant message, parameter name and HRESULT.

The exact complete Message behavior is now represented by managed ABI helper
`__hybridcpu_managed_argument_exception_get_message(object-ref):object-ref`, marked allocating
and `RequiredSafepoint`. Dispatch metadata binds the CoreLib virtual target to that helper, and
ManagedPortSmoke covers its ABI identity, GC transition, invariant UTF-16 formatting, null/empty
parameter behavior, GC retention and malformed receiver rejection. No executable thunk was
invented: fresh short-path diagnostic evidence at `TempEnv/d4018` exits outer 1 / ILCompiler 1 /
adapter 15 at `HCSCF-LINK4018`, because production ISE has no CPU-callable ECALL bridge that
transfers the object reference/result and preserves allocation safepoint roots. The host-side
ManagedRuntime method is reference evidence only. Normal publish remains outer 1 / adapter 25 at
`HCPUB1004: eh-unwind`. Full preflight in
`TempEnv/doom-argument-message-helper-contract-20260901` passed before publication; its longer
path also demonstrated Windows CreateProcess error 206, so terminal compiler evidence uses the
byte-identical verified pack copied to the short path. No Doom `.hcexe` or hashes exist.
## 2026-09-01 ArgumentException message ECALL checkpoint

The exact allocating `ArgumentException.Message` helper contract now has a
CPU-callable thunk plus an explicit production ISE ECALL retirement bridge.
`ArgumentNullSmoke` checks the supported RequiredSafepoint ABI and emitted HCO
definition. Full ManagedPortSmoke reports 46 PASS groups in the clean
`TempEnv/d4020` cycle; Doom CoreCLR is 26/26 and all requested Release builds,
the 217-file immutable pack, and closure report pass.

The first diagnostic after this change reached duplicate process-exit module
ownership (`HCSCF-LINK4005 / HCLINK0002`); ownership is now deduplicated and the
full preflight was repeated. The exact next diagnostic is
`HCSCF-LINK4005 / HCLINK1003: Undefined global symbol
'__hybridcpu_managed_ensure_type_initialized'` (adapter 15, ILCompiler 1,
publish 1). ILCompiler did not report a method, assembly, or IL offset. Normal
publish remains HCPUB1004 `eh-unwind`. No Doom `.hcexe` or ISE guest execution
is claimed.
## 2026-09-01 static initialization/runtime ECALL sequence

The reached ensure-type-initialized, static-store-i4, and static-load-i4 linker
gaps are closed with CPU-callable thunks and exact production ManagedRuntime
ECALL bindings. StaticProjectionSmoke verifies the supported ABI and emitted HCO
definitions. Separate clean `d4021`, `d4022`, and `d4023` cycles each pass
full smoke, Doom CoreCLR 26/26, requested Release builds, ISE, immutable pack and
closure checks.

The next exact diagnostic is HCSCF-LINK4005 / HCLINK1003 for
`__hybridcpu_managed_null_check` (adapter 15, ILCompiler 1, publish 1). The
existing trap-mapping reference runtime is not wired into production ISE
retirement, so neither a zero-address trap nor process exit proves managed
NullReferenceException dispatch. Normal publish remains HCPUB1004 `eh-unwind`.
No Doom `.hcexe` or guest execution is claimed.

## 2026-09-01 exact null-check capability gate

`StaticProjectionSmoke` now proves that retained native null checks fail closed at
`HCSCF-LINK4019` and that the diagnostic names the actual owning method. CIL emission offsets
are preserved in IR source metadata, so the clean `d4028` pack reports the first Doom site
exactly: `DoomSharp.Core.GameLogic.Player.set_DidSecret`, assembly
`DoomSharp.Core,1.0.0.0,neutral,none`, `IL_0002` (adapter 15, ILCompiler 1, publish 1).
The gate forbids a no-op or process-exit replacement until production ISE retirement connects
precise fault authorization, exact exception allocation, GC roots, handled/unhandled native EH
transfer, and managed process exit. Full smoke, Doom CoreCLR 26/26, all requested Release
builds, closure, and the 217-file immutable pack passed. Normal publication remains
`HCPUB1004: eh-unwind`; no `.hcexe` exists.

## 2026-09-01 explicit null and i4 array-store helpers

The null-check helper is now an allocating `RequiredSafepoint`/`MayThrow` CPU thunk: non-null
returns unchanged, null allocates the registered `System.NullReferenceException` and tail-enters
native throw dispatch. The reached i4 array-store helper uses the production guest-backed array
runtime and maps null/bounds outcomes to exact managed exception objects before the same native
EH transfer. ABI/HCO regressions and full clean `d4035`/`d4039` preflights pass. The next exact
diagnostic is `HCSCF-LINK4005 / HCLINK1003: Undefined global symbol
'__hybridcpu_managed_array_store_ref'` (adapter 15, ILCompiler 1, publish 1). Normal publish is
still gated by `HCPUB1004: eh-unwind`; no Doom `.hcexe` is claimed.

## 2026-09-01 reference-store, InitializeArray and newarr checkpoint

The reached `__hybridcpu_managed_array_store_ref`, `__hybridcpu_managed_initialize_array`, and
`__hybridcpu_managed_newarr` linker blockers are closed in order. Reference stores use exact
runtime assignability and allocate guest `NullReferenceException`, `IndexOutOfRangeException`,
or `ArrayTypeMismatchException` before native EH transfer. FieldRVA blobs now travel as bounded,
digest-covered immutable bootstrap rows into the production ISE loader; InitializeArray preserves
its atomic size/type validation. Newarr returns the guest-backed allocation and maps negative/size
overflow and heap exhaustion to exact guest `OverflowException` and `OutOfMemoryException`.
Malformed handles/provider failures remain fail closed.

Clean preflights `d4046` and `d4050` passed full ManagedPortSmoke, Doom CoreCLR 26/26, all requested
Release builds, closure (1937 owned / 37 external / 202 types), and verified 217-file packs.
Diagnostic `d4051` exits publish 1 / ILCompiler 1 / adapter 15 at the next exact compiler/linker
blocker:

```text
HCSCF-LINK4005: Static linker rejected the managed object world
(HCLINK1003: Undefined global symbol '__hybridcpu_managed_static_store_ref'.)
```

The aggregate diagnostic contains no method, assembly, or IL offset. Normal publish `d4052-normal`
exits 1 / adapter 25 at `HCPUB1004: unsupported managed publish workstream(s): eh-unwind`.
No Doom `.hcexe`, determinism hashes, or HybridCPU guest execution exists.

## 2026-09-01 static references and object allocation checkpoint

Three subsequently reached undefined helper blockers are closed in strict order:
`__hybridcpu_managed_static_store_ref`, `__hybridcpu_managed_static_load_ref`, and
`__hybridcpu_managed_alloc`. Static reference access is restricted to exact descriptor-owned
8-byte reference slots; malformed kind/offset requests fail atomically. The non-moving GC scans
those descriptor-declared static roots, with smoke evidence for retention and reclamation.
Object allocation now uses the guest-backed heap at a RequiredSafepoint; invalid type handles fail
closed and exhaustion allocates exact image-owned `System.OutOfMemoryException` before native EH
transfer. Managed ABI is minor 36.

Clean preflights `d4054`, `d4057`, and `d4061` each passed full ManagedPortSmoke, Doom CoreCLR
26/26, requested Release builds, closure (1937 owned / 37 external / 202 types), and a verified
217-file immutable pack. Diagnostic `d4062` exits publish 1 / ILCompiler 1 / adapter 15 at:

```text
HCSCF-LINK4005: Static linker rejected the managed object world
(HCLINK1003: Undefined global symbol '__hybridcpu_managed_array_load_ref'.)
```

The aggregate diagnostic supplies no method, assembly, or IL offset. Normal publish
`d4063-normal` exits 1 / adapter 25 at `HCPUB1004: unsupported managed publish workstream(s):
eh-unwind`. No Doom `.hcexe`, determinism hashes, or HybridCPU guest execution exists.

## 2026-09-01 array-load continuation

Managed ABI minor 38 closes the reached `array_load_ref`, `divide_u4_checked`, and `array_load_i4` helpers with deterministic ECALL thunks, exact argument envelopes, exact managed exception allocation, required safepoints and native handled/unhandled EH transfer. The `array_load_i4` regression also verifies that its ABI row is allocating/throwing and that the emitted HCO defines the exact helper symbol.

Clean preflight `TempEnv/d4072` passed targeted allocation-capacity, the full ManagedPortSmoke, Doom CoreCLR 26/26, all requested Release builds, immutable 217-file pack verification, and closure 1937 owned / 37 external / 202 types. Diagnostic `TempEnv/d4073` exits outer 1 / ILCompiler 1 / adapter 15 (MSBuild wrapper 31) at `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_array_empty'`; its aggregate has no method, assembly or IL offset. Normal publish `TempEnv/d4074-normal` remains gated at adapter 25 by `HCPUB1004: unsupported managed publish workstream(s): eh-unwind`. No Doom executable or guest execution evidence exists.

`Array.Empty<T>` is closed in ABI minor 39 with an exact allocating/throwing ECALL, per-exact-SZARRAY cached identity, zero length, GC root retention, invalid-type rejection and exact OOM/native-EH transfer. `TempEnv/d4076` passes the full preflight and verified 217-file pack/1937-37-202 closure. `TempEnv/d4077` advances to `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_array_length'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4078-normal` remains `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.

Array length is closed in ABI minor 40 with exact layout reads, null-status regression, NRE allocation, required safepoint/native EH, and exact HCO definition. `TempEnv/d4080` passes the full preflight with verified 217-file pack and 1937/37/202 closure. `TempEnv/d4081` advances to `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_array_store_i1'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4082-normal` remains `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.

Byte-array store is closed in ABI minor 41 with exhaustive low-byte truncation/CoreCLR parity, atomic negative behavior, exact NRE/bounds allocation, required safepoint/native EH and exact HCO definition. `TempEnv/d4084` passes the full preflight with verified 217-file pack and 1937/37/202 closure. `TempEnv/d4085` advances to `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_string_char'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4086-normal` remains `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.

String character access is closed in ABI minor 42 with raw UTF-16, signed-index, exact null/bounds status, exact managed exception/native-EH transfer, ABI-effects and HCO-definition regressions. `TempEnv/d4088` failed the new negative-index assertion and is not evidence; the runtime's ambiguous `-1` length sentinel was replaced by an explicit length/character selector. Clean `TempEnv/d4089` then passes full ManagedPortSmoke, Doom CoreCLR 26/26, all builds, verified immutable 217-file pack, and closure 1937/37/202. Diagnostic `TempEnv/d4090` advances to `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_string_length'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4094-normal` with SDK identity `10.0.204` remains adapter 25 / `HCPUB1004: eh-unwind`; `TempEnv/d4092-normal` proves the unpinned installed SDK fails closed at `HCPUB1007`. No Doom `.hcexe` exists.

String length is closed in ABI minor 43 with exact UTF-16 count/null-status and allocating/throwing ABI/HCO regressions. `TempEnv/d4096` is excluded because its legacy-shell orchestration stopped before builds/pack. Clean `TempEnv/d4097` reruns everything under `pwsh` and passes full smoke, CoreCLR 26/26, all builds, immutable 217-file pack verification with zero mismatches, and closure 1937/37/202. Diagnostic `TempEnv/d4098` advances to `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_exception_ctor_message'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4099-normal` remains adapter 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.

`Exception(string)` now binds a deterministic native HCO that writes the exact aligned `_message` reference slot, with `HCSCF-LINK4033` for missing layout metadata. Clean `TempEnv/d4101` passes full smoke, CoreCLR 26/26, all builds, immutable 217-file pack verification, and closure 1937/37/202. Diagnostic `TempEnv/d4102` advances to `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_string_concat2'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4103-normal` remains adapter 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.

String.Concat(string,string) is closed in ABI minor 44 with embedded-NUL/non-ASCII and null-operand positives, invalid-type negative, checked allocation, exact OOM/native-EH transfer, ABI effects and defining-HCO regressions. Clean `TempEnv/d4105` passes full smoke, CoreCLR 26/26, all builds, immutable 217-file pack verification and closure 1937/37/202. Diagnostic `TempEnv/d4106` advances to `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_array_load_u1'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4107-normal` remains adapter 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.

Unsigned byte-array load is closed in ABI minor 45 with exhaustive zero-extension/CoreCLR raw byte/bool parity, exact null/bounds and wrong-width negatives, NRE/IndexOutOfRange native-EH transfer, ABI effects and defining-HCO checks. Clean `TempEnv/d4109` passes full smoke, CoreCLR 26/26, all builds, immutable 217-file pack verification and closure 1937/37/202. Diagnostic `TempEnv/d4110` advances to `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_divide_i4_checked'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4111-normal` remains adapter 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.

Checked signed Int32 division is closed in ABI minor 46 with existing CoreCLR/runtime quotient, divide-by-zero and MinValue/-1 overflow regressions plus exact ABI/HCO coverage. Clean `TempEnv/d4113` passes full smoke, CoreCLR 26/26, all builds, immutable 217-file pack verification and closure 1937/37/202. Diagnostic `TempEnv/d4114` advances to `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_divide_i8_checked'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4115-normal` remains adapter 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.

Checked signed Int64 division is closed in ABI minor 47 with existing CoreCLR/runtime quotient, divide-by-zero and MinValue/-1 overflow regressions plus exact ABI/HCO coverage. Clean `TempEnv/d4117` passes full smoke, allocation regression, CoreCLR 26/26, all builds, immutable 217-file pack verification and closure 1937/37/202. Diagnostic `TempEnv/d4118` advances to `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_math_abs_i8'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4119-normal` remains adapter 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.

Checked `Math.Abs(Int64)` is closed in ABI minor 48 with existing CoreCLR/runtime boundary and CoreLib-identity regressions plus exact `MaySafepoint`/throw ABI and defining-HCO coverage. Clean `TempEnv/d4121` passes full smoke, allocation regression, CoreCLR 26/26, all builds, immutable 217-file pack verification and closure 1937/37/202. Diagnostic `TempEnv/d4122` advances to `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_array_load_i2'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4123-normal` remains adapter 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.

Signed Int16 array load is closed in ABI minor 49 with existing CoreCLR/runtime sign-extension, null/bounds/wrong-width coverage plus corrected required-safepoint/throw ABI and defining-HCO regression. Clean `TempEnv/d4125` passes full smoke, allocation regression, CoreCLR 26/26, all builds, immutable 217-file pack verification and closure 1937/37/202. Diagnostic `TempEnv/d4126` advances to `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_array_load_u2'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4127-normal` remains adapter 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.

Unsigned Int16/Char array load is closed in ABI minor 50 with existing CoreCLR/runtime zero-extension, null/bounds/wrong-width coverage plus corrected required-safepoint/throw ABI and defining-HCO regression. Clean `TempEnv/d4129` passes full smoke, allocation regression, CoreCLR 26/26, all builds, immutable 217-file pack verification and closure 1937/37/202. Diagnostic `TempEnv/d4130` advances to `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_array_copy_all'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4131-normal` remains adapter 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.

Three-argument `Array.Copy` is closed in ABI minor 51 with existing overlap/CoreCLR parity, primitive identity, atomic reference validation and negative runtime coverage plus corrected required-safepoint/throw ABI and defining-HCO regression. Clean `TempEnv/d4133` passes full smoke, allocation regression, CoreCLR 26/26, all builds, immutable 217-file pack verification and closure 1937/37/202. Diagnostic `TempEnv/d4134` advances to `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_isinst'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4135-normal` remains adapter 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.

Managed `isinst` is closed in ABI minor 52 with exact importer type-handle resolution, null/inheritance/non-assignable runtime coverage, fail-closed invalid handle/receiver cases, ECALL operation 29 and defining-HCO/ABI regression. Clean `TempEnv/d4137` passes full smoke, allocation regression, CoreCLR 26/26, all builds, immutable 217-file pack verification and closure 1937/37/202. Diagnostic `TempEnv/d4138` advances to linker-layer `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_remainder_i4_checked'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4139-normal` remains adapter 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.

Checked signed Int32 remainder is closed in ABI minor 53 with existing CIL/CoreCLR/runtime boundary and zero/overflow coverage plus ECALL operation 30, exact allocating/throwing ABI, linker gate `HCSCF-LINK4042` and defining-HCO regression. Clean `TempEnv/d4141` passes full smoke, allocation regression, CoreCLR 26/26, all builds, immutable 217-file pack verification and closure 1937/37/202. Diagnostic `TempEnv/d4142` advances to linker-layer `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_string_concat3'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4143-normal` remains adapter 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.

Three-string `String.Concat` is closed in ABI minor 54 with shared exact UTF-16/null-as-empty semantics, invalid operand rejection, checked OOM allocation/native-EH transfer, ECALL operation 31, linker gate `HCSCF-LINK4043` and CIL/ABI/defining-HCO regressions. Clean `TempEnv/d4145` passes full smoke, allocation regression, CoreCLR 26/26, all builds, immutable 217-file pack verification and closure 1937/37/202. Diagnostic `TempEnv/d4146` advances to linker-layer `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_array_store_i2'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4147-normal` remains adapter 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.

Checked 16-bit array store is closed in ABI minor 55 with existing exhaustive CoreCLR/runtime truncation, atomic null/bounds/wrong-width coverage plus corrected required-safepoint/throw ABI, ECALL operation 32, linker gate `HCSCF-LINK4044` and defining-HCO regression. Clean `TempEnv/d4149` passes full smoke, allocation regression, CoreCLR 26/26, all builds, immutable 217-file pack verification and closure 1937/37/202. Diagnostic `TempEnv/d4150` advances to linker-layer `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_string_from_utf16_array'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4151-normal` remains adapter 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.

Exact CoreLib `String(char[])` is closed in ABI minor 56 with existing raw UTF-16/runtime/CoreCLR and metadata-only lowering coverage plus null/OOM allocation/native-EH transfer, invalid shape rejection, ECALL operation 33, linker gate `HCSCF-LINK4045` and exact ABI/defining-HCO regressions. Clean `TempEnv/d4153` passes full smoke, allocation regression, CoreCLR 26/26, all builds, immutable 217-file pack verification and closure 1937/37/202. Diagnostic `TempEnv/d4154` advances to linker-layer `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_array_copy'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4155-normal` remains adapter 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.
Five-argument `Array.Copy` is closed in ABI minor 57 with an exact 40-byte guest stack argument block carried by the existing read-only external-service buffer envelope, production `MainMemoryArea` reads, canonical Int32 validation, the existing overlap-aware/prevalidated atomic runtime, ECALL operation 34 and linker gate `HCSCF-LINK4046`. Clean `TempEnv/d4157` passes full smoke, allocation regression, CoreCLR 26/26, all builds, immutable 217-file pack verification and closure 1937/37/202. Diagnostic `TempEnv/d4158` advances to linker-layer `HCSCF-LINK4005` / `HCLINK1003: Undefined global symbol '__hybridcpu_managed_argument_null_ctor_param_name'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `TempEnv/d4159-normal` remains wrapper 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.
Exact `ArgumentNullException(string)` construction is closed in ABI minor 58 with the existing CoreCLR-parity runtime, ECALL operation 35, required safepoint, image-owned OOM managed transfer, invalid receiver/parameter provider failure and linker gate `HCSCF-LINK4047`. String stays on shape-aware metadata rather than dispatch-type encoding. Clean `TempEnv/d4163` passes full smoke, allocation regression, CoreCLR 26/26, all builds, immutable 217-file pack verification and closure 1937/37/202. Diagnostic `TempEnv/d4164` advances to `HCSCF-LINK4005/HCLINK1003: Undefined global symbol '__hybridcpu_managed_argument_out_of_range_ctor_param_name'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `d4165-normal` remains wrapper 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.
Exact `ArgumentOutOfRangeException(string)` construction is closed in ABI minor 59 with the existing CoreCLR-parity runtime, ECALL operation 36, required safepoint, image-owned OOM transfer, malformed-operand provider failure and linker gate `HCSCF-LINK4048`. Clean `TempEnv/d4167` passes full smoke, allocation regression, CoreCLR 26/26, all builds, immutable 217-file pack and closure 1937/37/202. Diagnostic `d4168` advances to `HCSCF-LINK4005/HCLINK1003: Undefined global symbol '__hybridcpu_managed_string_not_equals'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `d4169-normal` remains wrapper 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.
Exact string inequality is closed in ABI minor 60 with the existing ordinal raw-UTF16/null-aware runtime, canonical Boolean result, invalid-object provider failure, ECALL operation 37 and linker gate `HCSCF-LINK4049`. Clean `TempEnv/d4170` passes full smoke, allocation regression, CoreCLR 26/26, all builds, immutable 217-file pack and closure 1937/37/202. Diagnostic `d4171` advances to `HCSCF-LINK4005/HCLINK1003: Undefined global symbol '__hybridcpu_managed_array_clear'` (outer/ILCompiler 1, adapter 15, wrapper 31; no method/assembly/IL offset). Normal `d4172-normal` remains wrapper 25 / `HCPUB1004: eh-unwind`; no Doom `.hcexe` exists.
