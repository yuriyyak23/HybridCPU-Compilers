# HB-INT-03: opt-in executed Csc → PE/PDB method linkage (v1)

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
sdk_dir="$(dotnet --list-sdks | awk -v v="$sdk" '$1==v {gsub(/[][]/,"",$2); print $2}')"
export HB_CSC_PATH="$sdk_dir/$sdk/Roslyn/bincore/csc.dll"
export HB_SDK_VERSION="$sdk"
project=Compilers/HybridCPU_Compiler/HardwareBridge/Tests/CscLinkFixture/HardwareBridge.CscLink.FixtureTests.csproj
work="$(mktemp -d)"
dotnet restore "$project" -p:BaseIntermediateOutputPath="$work/obj/" -p:BaseOutputPath="$work/bin/"
dotnet build "$project" -c Release --no-restore -p:BaseIntermediateOutputPath="$work/obj/" -p:BaseOutputPath="$work/bin/"
dotnet "$work/bin/Release/net11.0/HardwareBridge.CscLink.FixtureTests.dll"
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
