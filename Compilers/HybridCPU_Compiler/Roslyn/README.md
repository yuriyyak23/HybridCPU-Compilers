# Managed source companion

Default host: package Roslyn4.14.0, explicit CSharp13. Existing analyzer/generator packages
and CSharp13 source lane remain unchanged. Owned requests retain v2 identity/defaults.

The same companion assembly now has a command-line entry (OutputType Exe; public library
APIs remain usable by project references). Run:

```text
dotnet HybridCPU.Compiler.Roslyn.dll compile-source-image --source Program.cs --reference CORELIB.dll --assembly-name App --type App --method Read --out App.hcexe
```

Repeat --source/--reference; --body-reference supplies reachable method bodies and
--metadata-reference supplies declarations only. UTF-8 source inputs use absolute source
paths in PDB/input identity; cross-root equality is not claimed. CLI defaults to CSharp13;
explicit --language csharp15 requires an opted-in matching SDK host. --language csharp13
is also explicit, and other language values reject. Package host refuses CSharp15 with
HCSOURCE1001/exit2 and no output. Existing versioned language/host input identity applies.
It calls owned CompileBodyWorld then existing object linker/image
builder. Source/NoHeap rejection exits3, graph rejection4, link/image rejection5,
usage/input IO errors2, caller cancellation130. Diagnostics precede emit/import/output.
All inputs are read before output write; duplicate/overlapping input/output paths reject.
64 source/reference files maximum; source4 MiB/file and16 MiB closure, reference32 MiB/file
and64 MiB closure; existing importer/graph/metadata budgets still apply.

One synchronous output write occurs only after successful linking and final token check.
Compiler rejection preserves an existing output or creates none; atomic/crash-recovery
output and interruption during synchronous write are not claimed. No publish, execution
or NativeAOT upstream identity is inferred. Qualification:no.

Optional --emit-pe PATH, --emit-pdb PATH and --emit-map PATH export the exact owned
source artifacts for debugging after successful graph/link admission. Map JSON is a
method-identity-sorted dictionary of existing ManagedSourceMapV1 records; dependency
methods have no primary-PDB mapping. Inputs and every output path must be distinct.
Exports off preserve the image-only default. Source/graph rejection and pre-write
cancellation write no outputs; synchronous multiple file writes are not atomic as a group.

Repeat --method-argument int32|int64 to supply exact primitive arguments for a generic
method root (maximum8). These map to System.Int32/Int64 plus existing CIL carriers and
are passed to RestrictedCilMethodSelectorV1; importer retains arity/constraint/body
admission. No type inference or arbitrary named types are introduced. Repeat --type-argument
int32|int64 for declaring-type context, maximum8 independently of method arguments.
Use the exact metadata type name including arity (for example CliGenericType`1).
VAR and MVAR contexts remain separate; importer rejects open/wrong-arity/constrained
forms outside its existing contract. Source identity still describes source/emit; graph/import identities bind
the selected exact context. A generic source root without arguments fails HCCIL1012.

`CompileBodyWorld(request, importer, roots, dependencyReferenceIndices)` is the explicit
owned-source graph route; use a ScalarControlFlowV2 importer. It shares source snapshots,
diagnostics/NoHeap admission and deterministic emit with `Compile`, then calls existing
ImportBodyWorld in StandaloneRestrictedModule mode. The legacy single-method overloads
and their helper allowlist remain unchanged. The caller can pass successful Compilation
to existing ScalarControlFlowV2ObjectLinkerV1 for object/link/image stages.

Indices select body-bearing images from the exact owned Roslyn ReferenceImages closure.
Unselected references supply no graph bodies; no filesystem/module discovery or automatic
framework promotion occurs. Invalid/duplicate indices throw before emit. The explicit
overload taking metadataReferenceIndices selects declaration-only references disjoint
from dependencyReferenceIndices. Existing metadata budgets/closure identities apply;
declarations grant no method bodies. Adapter-presented mode/recursion options remain separate.

Existing CIL BodyWorld resolves external enum TypeRef carriers from its call-owned
body/metadata closure. Eight integral carriers preserve primitive lowering/code bytes;
definition image digest participates in method options identity. Standalone ImportImage
without closure remains fail closed; invalid/unresolved enum declarations stay rejected.
SourceInputDigest binds source/emit inputs; graph/import identities bind selected roots,
exact generic context, effective importer options and body-reference closure. They are
different scopes, not a persistent cache key.

ManagedSourceBodyWorldResultV1.Source contains diagnostics and emitted PE/PDB/artifact/input
identity (its single-method Imported/SourceMap fields stay null). Compilation is null on
source rejection; otherwise it carries the existing importer success/failure. SourceMaps
is read-only and keyed by exact method identity, populated only for successful graph
methods belonging to the primary emitted PE. Dependency tokens never use the primary PDB.
Unsupported reachable bodies/budgets return no successful graph/maps; cancellation throws.
NoHeap is source admission, not interprocedural certification of reference implementations.

Experimental SDK host: build this project with `UseSdkRoslynForCompanion=true` using the
repository-pinned SDK. Compiler references come from that SDK's Roslyn/bincore; assembly
metadata `CompanionSdkRoslynHost` is generated from NETCoreSdkVersion. Consumers must bind
to the same compiler assemblies; mixing their explicit4.14 package with an SDK companion
fails compilation. This is not a new shipping NuGet pin or portable host packaging contract.
CompilerProfileSmoke now supports the same explicit build opt-in. Shared SdkRoslynHost.targets
uses GetAssemblyIdentity on actual SDK DLLs for exact references; no handwritten compiler version.
An isolated matching consumer is also retained in p4-csharp15-sdk-lane-20261013.

Set `CompanionSourceRequestV1.Language = CompanionSourceLanguageV1.CSharp15` only for this
host. Default/package hosts reject it. v3-csharp15 source identity and host-input/v1 bind
language/options, source/reference hashes, actual Roslyn assembly version and build SDK
metadata. CSharp13 remains default; mixed/implicit parse languages are not admitted.

Bounded consumer acceptance: labeled break and nested-loop labeled continue produce deterministic PE/PDB, independent
emitter equality and success through existing CIL importer. Source-error/cancellation controls
return no source artifacts; unsupported emitted CIL remains rejected by importer. Existing
NoHeap admission remains required. No general CSharp15, union/async/unsafe/ABI/importer,
trimming/NativeAOT-host or runtime/publication qualification follows. Qualification:no.

Labeled continue is covered in existing CompilerProfileSmoke by a NoHeap outer-for
consumer and independent CSharp13 goto-to-outer-increment emitter: exact method IL/local
signature, CFG edges and opcode projection agree. Different source/PDB provenance is not
required to have identical full IR fingerprints. Exact repeat PE/PDB/map/input/artifact,
changed-source divergence, CS9394/CS0103/HC0004, unsupported floating-point CIL
(HCCIL1001), old-lane rejection and cancellation/reuse controls are retained.

NoHeap admission policy v8 also rejects nonexhaustive switch expressions with HC0004
before emit: their implicit failure path can box the input and constructs an exception.
Roslyn semantic exhaustiveness is required; named enum members or guarded discard arms
alone do not suffice. Exhaustive bool switches need no discard; unprofiled behavior stays.
The policy version intentionally changes owned source identity, not positive PE/PDB.
Existing CompilerProfileSmoke covers enum switch through BodyWorld and the shared
object/link/restricted-image path with independent CSharp13 if/else emitted-CIL host
semantic controls. This does not prove guest execution.

NoHeap admission policy v10 retains the v8 rejection of exact System.Array.Empty<T>() with HC0004 before emit,
including alias/global/suppressed calls: a cached array can allocate on first use.
Existing Kernel.NoHeap profile matrix also rejects __hybridcpu_managed_array_empty,
while Managed/unprofiled calls retain their existing admission. Source identity and
Kernel.NoHeap matrix digest intentionally change; no array intrinsic/runtime change.

NoHeap admission policy v10 retains collection-expression rejection: reference targets report HC0004;
value/unknown targets report HC0006 pending a separate stack-only lowering contract.
This applies to both default13 and opted-in15 consumers. Unprofiled methods remain unchanged.
Method-group delegate conversions report HC0004, including cached static, instance, generic
and cast forms. Explicit delegate construction and anonymous functions retain their existing
syntax diagnostics; direct method invocation remains allowed by this source check.

Policy v10 admits explicit and target-typed source construction of a top-level,
non-generic, non-record, non-ref-like struct with one Int32 instance field
and one Int32 constructor parameter. Its source constructor must contain only
that field assignment from the parameter; initializer, static constructor, explicit
layout and object initializer are excluded. Other value constructions report HC0006;
reference construction and boxing still report HC0004. This is source admission:
the existing CIL importer and profile closure must independently accept receiver,
constructor, storage and effects. The bounded consumer links with explicitly selected
metadata-only corelib for the existing exception descriptor dependency.

The field may be explicit (v9) or the readonly backing field of one source get-only
auto-property (v10). The latter must have one bodyless getter, no setter/init accessor
or initializer; constructor assignment must resolve to that exact property.
Existing CIL receiver/body/profile checks remain mandatory. Versioned source input
identity changes; identical accepted source can retain identical emitted PE/PDB.

The existing CIL scalar carrier also admits a dynamic Int32 factory returned into
a local consumed only by exact pure field getters. Stores must come immediately
from calls or initialized local copies of the exact nominal value type. Copy-related
locals qualify together; unsupported source/destination address uses invalidate
the dependent chain. Existing CFG dominance/must
initialization proves assignment before every read, including reassignment and
conditional store joins; no branch may target a store or removed getter call.
Caller encoded code matches an independent scalar factory; constructor helper
retains its existing scalar copy and native return contract. Missing initialization,
wrong nominal stores, EH, cctor, nonpure getter and other address uses remain fail closed.
CompilerProfileSmoke runs the value consumer as part of full smoke; optional
`--value-construction` selects only this bounded group for diagnosis.

By-value helper parameters also support exact pure I4 getter reads through a bounded
ldarga.s/call pair. The original decoder/graph retains both instructions for budgets
and closure; scalar ingress projects to ldarg. Only immutable parameters qualify;
mutable, ref/in, EH, targeted-call and nonpure getter uses remain rejected. This
does not enable a general managed-byref parameter ABI.

An explicitly selected body-reference library can define the exact I4 value and
factory/getter. Synchronous BodyWorld collects owned scalar/getter definition proofs
and supplies referenced proofs to child importers; full definition PE digests enter
versioned options. Unselected/metadata-only references gain no body authority;
dependency methods never use the primary Portable PDB. This remains bounded to the
existing exact scalar shape and pure getters, not general external aggregate support.

Owned BodyWorld admission v11 permits direct external Int32 construction only when
the existing CIL metadata binding qualifier proves the selected body-reference
constructor is a pure field copy. Exact Roslyn assembly symbols/types bind admission;
body-reference selection participates in source-input identity. Omitted/metadata-only,
effectful or cctor-bearing constructors reject before emit. Standalone analyzer and
Compilation-only API receive no metadata waiver; downstream CIL/profile/link checks
remain independent and mandatory.

Policy v12 additionally admits source-owned pure Int64 field-copy constructors
under the same shape restrictions, with exact field/parameter type equality.
Direct construction/property uses existing receiver lowering. Mixed-width copies
remain rejected. Source-owned
Int64 factory return/local/pure getter uses an exact 8-byte scalar carrier, nominal
stores, CFG initialization and the existing shared backend. Its return must retain
the exact nominal identity; primitive and other-value returns reject HCCIL0019.
CompilerProfileSmoke asserts independent scalar CSharp13 caller bytes, full-register
copy/return ABI, host boundaries and repeat PE/PDB/maps/image. Immutable source-owned
Int64 parameter getters also admit only an immediate exact pure ldarga.s/call pair,
with existing immutable/untargeted/non-EH checks. Nominal Int64 call arguments must
retain their exact value identity; primitive/other-value arguments reject HCCIL0017.
External factory/local/getter scalar definitions also admit exact signed Int64
layout8/8 through selected body modules. Call-owned declaration carries the proven
carrier, full definition PE digest and exact getter identities; external options
schema v2 binds those facts (including Int32 consumers). Omitted/metadata-only roles,
nonpure/cctor/generic shapes and primitive nominal returns remain rejected. Primary
PDB maps cover only primary methods. External immutable Int64 parameter getters
also use the exact carrier through budgeted raw-decoder definition resolution;
nominal identity, definition digest, pure getter IL and immutable/untargeted/non-EH
restrictions remain mandatory. Owned BodyWorld policy v13 also admits external
Int64 pure-copy constructors from selected body modules. Qualification facts bind
the exact assembly/type and primitive parameter carrier, so a qualified overload
does not admit another overload on the same type. Receiver storage must match8/8;
source/body/profile/link proofs remain independent. Scalar memory IR and post-bundle
allocator stack operations now use physical LSU lanes4–5 through the shared backend;
encoded width/operands/lanes and final agreement are checked. This is not runtime
execution acceptance. Exact Int64 constructor-initialized locals now support pure
getter reads and immutable local copy chains/conditional joins through the existing
scalar projection. Both newobj/store and in-place ctor with an immediate exact I8
argument load/constant are proven before erasure; nominal identity, whole-component
qualification, definite initialization, untargeted calls and non-EH/noescape restrictions
remain required. Straight-line arithmetic/bit/shift/conversion arguments now reuse
existing V2 typed analysis from an empty argument stack, excluding calls/memory/byrefs
and incoming branch targets. Original erased receiver depth still counts toward
MaxStack and stack budgets. Forward conditional/nested/short-circuit regions also
use existing typed CFG analysis: no external entry, outgoing exit or backedge, and
one exact constructor sink. Primary static primitive leaf helper calls also qualify
only after exact MethodDef body decoding/typed analysis: no cctor, generic/byref,
EH, memory, transitive calls or loops. Normal graph/profile/ABI and call budgets
remain mandatory. Selected-body external MemberRef helpers use budgeted exact
definition resolution and the same pure-body proof with exact graph target identity.
Metadata-only references grant no body authority. Transitive pure scalar calls
reuse exact graph dependencies and per-definition typed body proof, with method
identity memoization, active cycle guard, maximum depth64 and charged metadata
resolution. Effects/EH/memory/byrefs/loops remain excluded. Proven-safe signed/unsigned
I4/I8 division/remainder arguments reuse existing zero/overflow proofs and scalar
lowering without runtime/fault helpers. Unsigned I4 consumers also use V2 conv.u4 low32 truncation and conv.u8
source-width-aware zero extension; frozen V1 remains closed. Exact checked Int64
division/remainder also qualify: receiver address erased before argument evaluation, scalar
store only after successful helper return, no byref across the safepoint/fault.
Existing checked-div-i8 runtime ABI, native exception transfer and exact exception
image-type gates remain mandatory; eh-unwind remains nonpublish. Checked Int64
remainder uses n-trunc(n/d)*d after the existing checked quotient helper, preserving
both operands as scalar SSA across its ABI. No new runtime operation. Checked
unsigned Int64 division/remainder also qualify: 0/signedBits(d) through the existing
checked-I8 helper faults only for zero, then its zero result participates in an ADD
feeding full-width DIVU/REMU. This data dependency preserves probe-before-operation
ordering and all unsigned bits. Exact existing signed I4 div/rem and unsigned I4
div helpers also qualify with native exception transfer, signed/unsigned I8 result
extension and unchanged provider ABI. Dynamic unsigned I4 remainder uses the existing
checked-U4 0/d probe, an ADDW data dependency and REMUW, followed by source-aware I8
extension. Proven-safe signed/unsigned I4/I8 division/remainder inside pure helper
bodies also qualify through existing V2 analysis and recursively proven managed
dependencies. Non-managed runtime/fault helpers inside a pure body remain excluded.
Primitive I4/I8 local assignment expressions also qualify through existing typed
stores/SSA, including chained/compound/conditional assignments and checked RHS.
Targets must be declared primitive locals without aggregate identity; original
stack and CFG/lifetime guards remain. Primitive I4/I8 parameter updates in arguments
and pure helper bodies also qualify through existing mutable-argument SSA; aggregate,
byref and typed receiver mutations remain excluded. Exact int[] parameter/local
element arguments also qualify using validated signature declaration facts and
existing checked ldelem.i4 helper/null/bounds/type/GC/lifetime gates. This does not
admit other references or array writes. Exact int[] Length arguments also qualify
through existing checked length/null/type/GC/lifetime and native-width conversion,
including local/repeated/conditional reads and element composition. Exact uint[]
element/Length arguments also qualify with unsigned declaration/load pairing and
existing checked helper/zero extension. Each isolated region uses one exact word
element carrier; mixed references remain excluded. Exact long[] element/Length
arguments qualify via V2 ldelem.i8 and checked array-load-i8 runtime/helper/adapter
(operation39, managed ABI minor62/runtime-pack contract v1.24). Exact ulong[]
element/Length arguments qualify with a distinct UInt64 declaration and existing
I8 bits/unchecked cast; mixed carriers and checked overflow casts remain excluded.
Exact string Length constructor arguments qualify via a validated string declaration,
exact CoreLib getter/signature and existing checked helper/GC/null/native-EH path.
Parameter/local/repeated/conditional/null-skipping reads preserve UTF-16 length.
Mixed reference kinds, arbitrary getters and ldlen over string remain excluded.
Exact string character arguments qualify through the closed Length/Chars accessor
set and existing checked UInt16/null/bounds/UTF16 helper/native-EH path. Shared helper
resolution rejects foreign String getter scopes/signatures with HCCIL1478, including
raw value-only imports. Exact byte/sbyte/short/ushort/char/bool array element/Length
arguments qualify with matching sign/width declarations and checked helper paths;
Boolean reads feed scalar conditional CFG. Signed I1 loads use the existing checked
U1 helper plus exact sign extension, preserving the producer used by locals/phis.
Mixed array/String and signed/unsigned expressions qualify through per-value
declaration facts in the existing isolated V2 proof. Each consumer checks its own
operand; same-kind joins preserve facts, heterogeneous joins become unknown and
cannot authorize a reference consumer. Normal/raw imports keep enforcement off.
Exact reference local/parameter assignments inside arguments qualify when the
operand fact matches the declared carrier. Existing SSA preserves chained and
conditional assignments and observable later reads; wrong carrier, aggregate,
absent slots and unknown producers remain closed in the isolated proof.
Explicit null, nullable reference assignments, coalescing and coalescing assignments
qualify with exact carrier/null joins in that proof. All-null joins retain only Null;
unknown or heterogeneous carriers cannot authorize reference operations. Runtime
null/bounds faults still use the existing checked helpers. Exact CoreLib Nullable<int>
locals from conditional access, null/default, copies and reassignment qualify through
opaque private packed-I8 V2 state. Exact newobj/in-place initialization, HasValue,
GetValueOrDefault() and GetValueOrDefault(single effect-free I4 producer) qualify;
coalescing can feed retained Int64 values. Stores preserve the nominal marker;
integer escape/injection, boxing/EH and arbitrary Nullable<T> remain closed.
Exact I4/I8 Value accessors use a checked runtime helper: absent values produce
InvalidOperationException through native EH transfer; full payload and canonical
presence remain separate helper arguments. ABI63/runtime-pack1.25 adds operation40.
The image requires the exact exception descriptor; metadata-only input grants no
method-body authority. Exception message/HResult/stack-trace compatibility is not
qualified by this scoped type/result/fault consumer.
Exact Nullable<long> locals retain full I8 bits and a separate I4 HasValue component
in existing V2 SSA, including component phis, copy/reassignment/default/accessors.
In-place forward-CFG constructor arguments reuse the bounded scalar proof; static
declarations/member bytes and noescape restrictions remain. No packed-I8 widening.
No nullable parameter/return ABI is introduced. General aggregate
ABI, arbitrary address uses and guest execution are not qualified.

Constructed generic type calls can resolve exact definitions from explicitly
selected body-reference libraries using assembly scope, nested type name and
generic arity. Existing type/method substitution and signature matching apply;
metadata-only or foreign same-name bodies cannot supply execution semantics.
This does not qualify arbitrary CoreLib Nullable<T> locals/accessors or general aggregate ABI.

Exact Nullable<int/long>.GetValueOrDefault computed scalar arguments reuse the
bounded typed constructor argument proof and forward CFG. Argument instructions
remain eager, including argument assignment and checked division faults even for
present receivers. Stack-consuming projection preserves exact nominal local,
signature and original call/stack budgets. A bounded preliminary pass admits
nested exact leaf Value/HasValue/GetValueOrDefault() accessors in such arguments;
Value faults remain eager for present outer receivers. Bottom-up normalization
also admits nested computed defaults, preserving existing typed scalar proof and
erased receiver stack depth. Computed Nullable<int> in-place initialization reuses
the exact I4 argument proof and existing scalar stack initialization operation,
with the same eager faults/CFG and original call/stack budgets. Exact Nullable<uint>
locals use a distinct nominal marker over existing packed I4 bits. Init/copy/join/
default/HasValue/Value retain all payload bits; emitted conv.u8 supplies explicit
unsigned widening. Exact Nullable<ulong> locals retain distinct nominal identity
and full payload/presence through existing component SSA, default/accessor and
checked Value lowering. Exact Nullable<ushort> locals use a distinct bounded-U2
marker; constructor/default argument lowering truncates to 16 bits independently
of source conv.u2. Accessor result extends correctly into I4/I8. Exact Nullable<short>
locals retain separate Int16 nominal identity and signed getter/default/Value
extension after 16-bit truncation. Exact Nullable<byte> locals use UInt8 nominal
and 8-bit truncation/zero extension; the private width fact supports only admitted
8/16-bit forms. Exact Nullable<sbyte> locals have distinct Int8 identity and reuse
8-bit truncation with signed getter/default/Value extension. Exact Nullable<char>
locals use distinct Char metadata/local identity over the existing U2 carrier and
16-bit truncation/zero extension; Char/UInt16 member mixing rejects. Exact Nullable<bool>
locals retain a separate Boolean nominal over the existing width8 carrier. Raw
constructor/default arguments preserve their CLI low byte; source logical expressions
keep their emitted normalization. Boolean/byte member mixing rejects. Enum nullable
locals with a source-owned exact Int32 enum definition are admitted separately:
module/TypeDef/assembly nominal identity survives packed I4 local projection and
existing proof/lowering. Source-owned signed Int64 enum nullable locals retain a
distinct full-payload/presence identity through the existing wide SSA/accessor/default
path. Source-owned UInt64 enum nullable locals preserve a distinct nominal/unsigned
fact and all payload bits through the existing full-U8 path; signed consumer
conversion remains explicit in source. External definitions require the selected
declaration facts described below. Source-owned UInt32 enum nullable locals preserve a separate nominal
identity over the existing packed-U4 path, with UInt32 proof return and source
zero extension into Int64. Source-owned UInt16 enum nullable locals reuse width16
truncation/zero extension with their exact nominal marker. Primitive constructor
fallback excludes enum receivers. Source-owned Int16 enum nullable locals preserve
distinct signed identity and reuse I2 truncation/sign extension for payload/default/
checked Value. Source-owned UInt8 enum nullable locals preserve a distinct unsigned
identity with existing width8 truncation/zero extension. Source-owned Int8 enum
nullable locals retain distinct signed identity and I1 truncation/sign extension.
External enum nullable locals with all eight integer underlyings are admitted from explicitly selected body or
metadata declaration modules through the same exact definition/nominal checks and
existing proof/lowering (full I8/U8 payload/presence for wide enums). UInt64 source
conversion to signed consumers remains explicit; UInt32 uses existing packed-U4
projection and explicit zero extension into Int64. UInt16 reuses exact width16
constructor/default truncation and zero extension through existing narrow proof/lowering;
primitive member fallback still excludes enums. Int16 retains its separate signed
identity and uses existing I2 truncation/sign extension for initializer/default/
accessor/checked Value. UInt8 retains its distinct unsigned identity and width8
truncation/zero extension through the existing U1 proof/lowering. Int8 retains its
distinct signed identity and I1 truncation/sign extension for initializer/default/
accessor/checked Value. Scalar I4/I8 equality branches admit
signed/unsigned CLI carriers, including uint.MaxValue/ulong.MaxValue constants; mixed widths and
reference mismatches still reject. Standalone Compile gains no reference-body or declaration
discovery. Malformed/non-integer enum declarations remain closed.
The primary BodyWorld route additionally admits a private static single Nullable<int>
parameter/GetValueOrDefault helper, returned as I4 or signed I8 through exact packed
ingress and pure-getter projection. It is not a root/public/external/instance/generic/
byref/EH ABI. Caller packets retain exact nominal identity; primitive I8/other nullable
arguments reject. NoHeap source/image and scalar/encoded-word controls cover this
bounded route. Exact private `long Consume(int? value)=>value.HasValue?1L:0L` also
uses packed ingress, a presence-bit projection and the emitted comparison/widening.
Exact I4 constant `GetValueOrDefault` arguments reuse the local default lowering
with a private argument-source marker and original stack/call accounting.
An exact second immutable I4 parameter may supply the default through ldarg.1;
the first carrier remains nominal packed I8, with ordinary I4 second-argument ABI.
Fault-free primitive arithmetic/bitwise/forward conditional default computations
reuse the existing typed argument proof and eager stack-default lowering, with
original receiver/operand stack accounting. Exact private primary I4→I4 helpers
may supply defaults after graph discovery and existing budgeted purity proof;
discovery alone is not admission. Memory/stores/nonpure or recursive helpers/fault
helpers/backedges remain closed. Parameter/external-helper-default and broader nullable
signatures remain gates.
