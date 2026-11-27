# HB-INT-03: opt-in executed Csc → PE/PDB method linkage (v1)

> **Baseline 2026-11-25.** The executed link binds independently pinned captured arguments/input bytes, emitted PE/PDB and supported method/map facts. The observation logger, launch/response witness and byte snapshot are separate inputs. A standalone response replay matching hashes still leaves original task consumption unestablished. See [current status](current/STATUS.md).

## Observed Csc output body import

`BridgeObservedCscBodyImporterV1` imports exact MethodDef roots from a
caller-pinned managed PE using the existing restricted CIL body-world importer.
It copies and checks the PE and portable PDB bytes against separate SHA-256
pins, imports the selected primary-module closure, and produces managed source
maps against the matching portable PDB. Its
`hybridcpu.hardwarebridge.observed-csc-body-facts/v1` result identifies the
caller-supplied capture digest separately from the PE/PDB and graph digests.

The API takes existing output bytes and the expected capture digest. Execution
provenance, full input closure, source admission, and independent Core
qualification must be established by their respective owners. The existing
Companion-source facts and executed-Csc link wire formats keep their semantics.
The capture digest is a caller-supplied reference. The importer receives no
capture file and does not rehash it. PDB document names and checksums in a
source map are metadata assertions. The importer does not open those paths
or prove that their source bytes exist or have the reported checksum.

The local AddWord-only probe separately hashed the replay receipt and read
the raw pinned Golden source bytes, then compared the independent source hash
with all 15 visible map rows. It rejected a duplicate root, an inconsistent
MethodDef name, and changed PE or PDB bytes. The resulting graph has two
methods and one local edge, with digest
`18c131a87a7f0fe6c2ac717c4d77216035a9f7743d28bc63563af13f185100c2`.

On SDK `11.0.100-rc.2.26504.105`, the raw Release PE/PDB from Golden
`6946ccab9d888f7dae0a4d8e18480f436138acd9` admitted AddWord, SubWord and
MultiplyWord as three roots with three local edges to SignExtendWord. The
graph digest was
`d16b22d7952d835f9d27a11f2ebb7b7595d14778bdbff3f68db60cab51400f61`.
This local run used the isolated software compiler build with the ignored
`CompilerEmissionPackage.cs` overlaid. Clean compiler publication remains a
prerequisite for a reproducible checkout build.

**Owner:** HardwareBridge software facts only. **Not** a CIL compiler, RTL emitter, proof authority, or new default writer.

## Distinct identity domains

The existing `BridgeSourceSnapshotV1.SnapshotDigest` is separate from
`BridgeCompilerFactsV1.SourceInputDigest` (a Companion input assertion).
`BridgeExecutedCscLinkV1` introduces a separate
`hybridcpu.hardwarebridge.executed-csc-link/v1` envelope. Its invocation
digest binds the SDK pin, raw compiler image, ordered argv JSON and actual
captured source/reference bytes; its output-pair digest then binds the invocation
to **separate raw PE and portable PDB hashes**. Each byte pin includes role,
asserted origin, intended consumer and exact byte size. The serialized Companion
facts digest and method source-map assertion digest are also separate.

The writer/reader inspect actual supplied PE metadata, assembly name, MethodDef
token/type/name and IL body bytes, PE CodeView GUID vs portable PDB ID,
document SHA-256 and exact visible portable PDB sequence point, line and
column. These are independent **byte/symbol checks**, not independent evidence
that Csc executed. Stable method identity, canonical signature, Compiler
SourceInputDigest and IR row index remain **Companion assertions**. An attacker
can forge a self-consistent event: a trusted R1-01 task-time observer, isolated
output creation, and independent Core consumer are still required to attest
compiler execution and Golden origin.

## Refusal boundary

Only `Candidate` + successful primary-module non-generic methods may be linked.
`Rejected` is still the other allowed software-facts status, but has no
successful PE/PDB linkage. Distinct typed refusals cover failed Csc, missing
PE/PDB, wrong argv/output paths, source or reference changes, wrong assembly,
mismatched PE/PDB or CodeView ID, changed/duplicate MethodDef tokens, generic
roots, stale/wrong source maps, invalid/duplicate JSON, and pin/SDK drift.
Owned-body foreign assemblies, unrecognized generics, hidden or non-exact PDB
mapping are deliberately unqualified. **No Verified, HardwareQualified, RTL
emission or hardware acceptance status is produced.**

## Byte compatibility and migration

| Contract | State |
| --- | --- |
| `hybridcpu.hardwarebridge.sidecar/v1` | Unchanged existing writer/reader/bytes |
| `hybridcpu.hardwarebridge.compiler-facts/v1` | Unchanged DTO/bytes |
| `hybridcpu.hardwarebridge.provenance-index/v2` | Unchanged existing writer/reader/bytes |
| `hybridcpu.hardwarebridge.executed-csc-link/v1` | New optional canonical writer/strict reader; NOT default |

If the existing sidecar must grow incompatibly, define a separate
`sidecar/v2` with strict writer/reader, explicit v1/v2 golden byte fixtures
and a separate paired Core reader PR before any default switch. This PR
does not extend an existing schema in place.

## Actual isolated fixture and evidence

The new `Tests/CscLinkFixture` host executes the pinned SDK's real
`Roslyn/bincore/csc.dll` via `dotnet`, using one ordered argv array.
Source and reference files, compiler image, real PE/PDB, MethodDef IL and PDB
sequence points are re-read; mutations reject stale input bytes, output,
argv, method token, source maps, and re-emitted replacement PE/PDB.
The contract-test `BridgeCompilerFactsV1` objects are **test-only assertions**
derived from actual emitted metadata. They do not represent an actual
Golden Companion compilation. Their synthetic Companion digest is not
claimed to be an observed Golden input digest.

Reproduce on a host with .NET SDK `11.0.100-rc.2.26504.105`:

```bash
sdk='11.0.100-rc.2.26504.105'
export HB_CSC_PATH="$DOTNET_ROOT/sdk/$sdk/Roslyn/bincore/csc.dll"
export HB_SDK_VERSION="$sdk"
project="$PWD/Compilers/HybridCPU_Compiler/HardwareBridge/Tests/CscLinkFixture/HardwareBridge.CscLink.FixtureTests.csproj"
work="$(mktemp -d)"
printf '%s\n' '{"sdk":{"version":"11.0.100-rc.2.26504.105","rollForward":"disable","allowPrerelease":true}}' > "$work/global.json"
cd "$work"
dotnet restore "$project" -p:ArtifactsPath="$work"
dotnet build "$project" -c Release --no-restore -p:ArtifactsPath="$work"
dotnet "$work/bin/HardwareBridge.CscLink.FixtureTests/HardwareBridge.CscLink.FixtureTests.dll"
```

The opt-in `.github/workflows/hb-int-03-executed-csc-link.yml`
records SDK, checkout SHA, actual Csc exit and stdout/stderr hashes,
restore/build/fixture exits, raw logfile hashes and real output hashes.
Restore/build are redirected to runner TEMP; changed tracked files fail CI.

## Remaining gates (owners)

- **HB-INT-02 / R1-01 compiler task-time owner:** capture real Golden
  executed Csc event, generated input bytes, references, argv, process and PE/PDB
  output provenance, source before/after hashes and filesystem no-write,
  symlink/reparse/traversal refusal. **Blocked** until actual observation.
- **HB-INT-06 / parent software compiler owner:** repair real opt-in
  Companion build and emit real Companion method facts; latest master companion
  selftests fail. **Failed/Blocked** separately from this fixture.
- **CW-INT-03/04 Core owner:** independently rehash actual PE/PDB/source
  and Csc event, exact symbol admission and paired Core reader for the
  optional transport. **NotRun** in this Bridge-only PR.
- **GitHub Actions supply-chain owner:** provide an explicit read-only private
  Core token for cross-repository importer workflow. **Blocked** while absent.
- **Core verification/EDA owners:** Golden full Processor, RTL, formal and
  physical signoff. **Blocked/NotRun**; no test-only fixture can waive them.


## R1-03 hardening (bounded v1-compatible checks, 2026-11-21)

This change preserves the **exact v1 schema, property ordering, digest preimages,
sidecar/v1, compiler-facts/v1, and snapshot/v1**. No v2 wire contract or Core
approval is silently introduced. Existing v1 material whose claim is not
independently supported by its supplied bytes can now be **refused**, rather
than silently reinterpreted. This is a stricter *reader*, not a v1 migration.

The PE **MVID must be nonzero** and is read from the actual PE module metadata.
The PE must have exactly one **portable** CodeView entry with Age=1. The
complete **20-byte** portable PDB ID (GUID + four-byte CodeView stamp) must
match, not merely its first 16 bytes. All PDB documents must have unique
physical source names and SHA-256 checksums matching exactly captured source
bytes; undocumented #line aliases, foreign documents, and duplicate names
fail closed. The source and reference inventories cannot alias physical input
paths, references must be accounted for exactly once in the ordered argv, and
Csc response-file indirection is refused until R1-01 can provide expanded
task-time inputs.

For the **narrow v1 method admission subset**, the observed PE ModuleName and
AssemblyIdentity must match the asserted Companion module, the declaring type
and method must be non-generic, and the MethodDef signature must decode into an
exact, bounded primitive signature matching Companion CanonicalSignature. Any
generic, nested, byref, array, class/valuetype, custom-modifier or otherwise
complex signature is **UnsupportedFacts**, pending a distinct reviewed
contract and independent Core symbol qualification. This is intentional
fail-closed behavior, not an assertion that real Golden signatures are simple.
Only exact owned **MethodDef** local calls are cross-linked: IL opcodes and
operand lengths are decoded from the **actual PE IL**, with call offsets,
callee MethodDef tokens and Companion direct-callee identities checked against
the bounded edges. Unknown method facts, missing local edges, duplicated edges,
unsupported IL and stale method graphs are refused. Foreign MemberRef/MethodSpec
calls are **refused**, rather than silently omitted from the owned-body closure.

The updated isolated toy compiles **two separate C# source files in a single
primary PE module** (`Fixture.Add` calling `Helper.Sum`), checks both physical
sources, both PDB documents, both signatures and exact IL call offset/token,
and exercises mismatched signatures, foreign method facts, stale graphs,
duplicate source maps, mixed PE/PDB from a second actual Csc invocation,
malformed/noncanonical JSON and changed argv/SDK. This is **not** a two-assembly
Golden or multi-module acceptance test. A separate positive multi-module link
must remain **NotRun** unless a complete supported observed receipt is supplied.
All Csc subprocesses in the fixture have a 90-second bounded wait; no
`Assembly.Load` is used for untrusted PE.

### Ownership and integration contract

R1-01 must provide an independent **completed task-time Csc execution witness**:
approved exact SDK/actual compiler executable SHA, ordered raw/expanded argv,
exit, stdout/stderr byte hashes, timestamps, isolated source/reference path
inventory and output PE/PDB bytes with on-disk hash attestations. A copied
`BridgeCscExecutedEventV1` alone is **not** an OS execution witness. R0-01
must provide real Companion `BridgeCompilerFactsV1` whose method/edge facts,
owned-body source input identity and PE/PDB digests are tied to its own actual
successful Companion run. R1-02 must supply its separately pinned source
capture. The invocation SHA, Companion input digest, SourceSnapshotDigest,
output pair SHA and Companion facts SHA remain **distinct domains**. No
synthetic test digest may be reused as Golden provenance. Core must perform
independent admission and method resolution; Bridge never outputs Verified,
AcceptedRTL or an authorized launch.

### Status and executable check matrix

| Check | Expected status without R0-01/R1-01 Golden receipts |
| --- | --- |
| Actual pinned toy Csc, PE/PDB, MVID, full CodeView/PDB ID | Tested in R1-03 CI only |
| Two-source single-module primitive MethodDef + owned IL edge | Tested in R1-03 CI only |
| Strict nested JSON/duplicate/unknown SDK and mutation refusals | Tested in R1-03 CI only |
| Full Golden software execution and real Companion method graph | **Blocked** |
| Positive two-assembly or multi-module Golden | **NotRun** |
| Core independent import, Bindings and RTL verification | **NotRun** |
| Golden checkout reads/writes/restore/build by this workflow | **NotRun (no checkout)** |

No success of a toy or mutation fixture changes these statuses. Native hashes,
exact SDK, Csc argv SHA, exit, stdout/stderr SHA and durations are printed by
the fixture CI; the GitHub Actions workflow does not upload private sources or
raw Golden artifacts.

### Verified first CI execution

Draft PR [#32](https://github.com/yuriyyak23/HybridCPU-Compilers/pull/32)
started the approved toy CI on
[run 44836355887](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44836355887).
Its first observed run used SDK `11.0.100-rc.2.26504.105`, with
`dotnet restore` exit=0, `dotnet build --no-restore` exit=0 and two actual
isolated `dotnet csc.dll` processes exit=0. The first actual compiler image
SHA-256 was `35E65EDC893C6902260FBFEBFF39EB3C2D6B34F3E80D364B2CF9B2F180FA588A`.
The first PE and PDB SHA-256 were
`6F6D957586AF73C0AC4A5E071213058E4B0E00825A00A471330BF9AB2389D730`
and `1D1499A16D707393A5FC6B3B994A408A3A8D84DDC292E557023B395E6EF6B540`.
This first run captured only the two-source toy: **no Golden or Core checkout**.
Subsequent CI runs must be evaluated by their own head SHA and job results,
not by the earlier run's success.

## R1-03 residual package — opaque IL method dispatch and production evidence preflight (2026-11-21)

Base re-audit: Bridge `master@d243dd7ecce243cc08a13303cf01247ba7a65024`;
private Core `master@b5c44685197a40df99a4d48891120b12475c6ca5`.
PR #32 was already merged; its [CI run 44836606012](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44836606012)
passed the **actual two-source toy Csc fixture only**. No Golden execution approval
or external software facts were inferred from that success. The current
R1-01 Linux witness `hybridcpu.hardwarebridge.csc-os-execution-receipt/v1`
reconstructs Csc cwd, execve ancestry and argv from a separate trace; its
own header expressly restricts the collector to a disposable two-project
fixture and does not authenticate Golden. R1-02's external task crosswalk
remains an *assertion* without separately approved task and raw observation
bytes. R0-01's recorded production Companion build fails due to missing
Core-owned production source types; no successful Companion graph/roots are
available for Golden. These dependencies cannot be resolved from R1-03
ownership.

### Bounded additional IL refusal

The existing v1 checks `call`, `callvirt`, `newobj` MethodDef tokens
against the claimed graph. A body could nevertheless contain `jmp`,
`calli`, `ldftn`, `ldvirtftn` or `ldtoken` of a method handle, which the
previous direct-edge enumeration did not claim or resolve. The narrow
v1 verifier now rejects those **opaque executable method-transfer paths**
as `StaleMethodGraph`. `ldtoken` for TypeDef alone is not a method
edge and remains admissible. It does not guess a target from an untrusted
Companion DTO and does not rewrite v1 wire bytes, schemas or property order.

`CscLinkFixtureProgram.cs` adds typed negative probes for all four
indirect control/handle forms, three method-handle token tables and a
truncated token, plus direct MethodDef and TypeDef-token regression
checks. These parser-level mutation tests run in the existing pinned
two-source Csc toy CI; **they are not production method evidence**.
The existing independent recompiled replacement PE/PDB checks remain
in place. Any real Golden method that needs these constructs requires a
separately reviewed qualification strategy with the actual compiler
and Companion receipts, not a best-effort v1 acceptance.

### Exact inter-owner crosswalk — required before positive Golden

| Stage | Link and independent origin | State at this head |
| --- | --- | --- |
| Effective Csc options | R1-01 actual task event + independently replayed ordered OS argv, compiler image hash, cwd, exit, stdout/stderr hashes | **Blocked** for Golden |
| Source/reference/generator bytes | R1-02 immutable captured inputs + role, exact path, SHA-256, before/after Golden no-write; source digest is not the invocation digest | **Blocked** |
| Executed outputs | Exact PE/PDB bytes, SHA-256 and output-pair digest bound to the Csc invocation, not merely supplied as a self-consistent event DTO | **Blocked** |
| PE/Portable PDB | Actual PE module MVID, one CodeView GUID+stamp/age=1, matching 20-byte portable PDB ID | Implemented in v1; toy-tested |
| Method identity | Actual MethodDef token/type/name/primitive signature/IL byte digest and bounded direct intra-module call-edge tokens | Implemented in v1; toy-tested; opaque IL refused |
| Debug ownership | PDB documents with SHA-256, visible sequence points at the exact IL offsets, captured owned source bytes | Implemented in v1; hidden-only mapping refused |
| Companion roots/graph | **R0-01** successful real opt-in Companion producer binding distinct source input digest, method identities, roots, edges and source maps | **Blocked** |
| Independent method qualification | **CW-INT-04** Core-owned Roslyn/source rederivation; **CW-INT-03** independent reader, separate Core PR | **NotRun** |
| RTL/formal/physical | Core-only acceptance and downstream qualification | **NotRun** |

`Tests/CscLinkFixture/R1_03_ProductionEvidenceContract.json`
is a *machine-readable missing-prerequisite manifest*, **not a receipt**.
It intentionally has `Disposition=Blocked`, `PositiveGolden=NotRun`,
`PairedCore=NotRun`, and separate SHA identity domains. A separate
GitHub Actions step strictly checks that the required external owners
and the pinned historic SDK are present in that manifest, and fails if
a synthetic fixture is promoted to a production status. The step
does **not** check trust in an arbitrarily supplied OS witness; R1-01 must
rederive the actual process from owned raw traces, and Core must
independently requalify its source/method identity before acceptance.

### Negative checks and scope

| Negative/mutation | Exact expected refusal/status |
| --- | --- |
| `jmp`, `calli`, `ldftn`, `ldvirtftn` | `StaleMethodGraph` |
| `ldtoken` MethodDef / MemberRef / MethodSpec | `StaleMethodGraph` |
| Truncated `ldtoken` operand | `StaleMethodGraph` |
| Direct MethodDef `call` and type-only `ldtoken` | Parser regression must remain admitted |
| Swapped/changed PE/PDB and recompiled source replacement | `WrongPePdb`, existing real toy mutations |
| Wrong PDB ID/stamp, missing/forged/duplicate document | `WrongPePdb` or `WrongSourceMap` (strict v1) |
| Hidden-only PDB map; wrong/out-of-body source point | `WrongSourceMap` (strict v1) |
| Stale token, signature, mis-bound root | `WrongMethodToken` or `DuplicateMethod` |
| Missing Companion graph/edges; foreign or generic calls | `UnsupportedFacts` or `StaleMethodGraph` |
| Missing trusted R1-01 OS witness or R0-01 Companion facts | **Blocked** — v1 event alone is not execution proof |
| Unknown SDK / changed reviewed toolchain | `InvalidPin` at strict reader; Golden **Blocked** |
| Missing reviewed Golden source approval | **NotRun** — no checkout, restore, build, invocation |
| Candidate/toy result promoted to Verified/AcceptedRTL | CI preflight `R1_03_CONTRACT_PROMOTION` |

**No Golden checkout or original source write** is performed by this
R1-03 CI. Local `dotnet restore/build` is unavailable in the authoring
environment, so the new code's actual execution is judged only from the
new PR CI job at its own head SHA, not from PR #32's historical green.
All production inputs and private diagnostic bytes remain outside
public artifacts. Software output can only be *Candidate/Observed*
until a separate Core-owned acceptance.

## Golden evidence

The .NET writer reproduced the archived segmented Windows capture and Core independently staged the bytes as CandidateOnly. See the [current status](current/STATUS.md) and [historical execution receipts](https://github.com/yuriyyak23/HybridCPU-Compilers/blob/8749de2c1c5cefbae82c22ab7d418f57d640bd71/Compilers/HybridCPU_Compiler/HardwareBridge/Docs/HB_INT_03_Executed_Csc_Link.md).
