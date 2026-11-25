# HB-INT-02 / CW-INT-03: C# byte-parity regression (stacked on PR #14)

> **Baseline 2026-11-25.** Python↔.NET bytes, actual CW importer and CW independent Roslyn re-emission require three distinct receipts. Record exact pins and case/alias/property-order/refusal cases. Current Python synthetic results are in the evidence index. See [current status](current/STATUS.md).

**Status: test-only interoperability extension, never source verification.**
Parent [HB-INT-02 Core v2 projection PR #14](https://github.com/yuriyyak23/HybridCPU-Compilers/pull/14).
Six-package [HardwareBridge integration plan](CircuitWeave_Pipeline_Integration_Plan.md).
Baseline HybridCPU-Compilers master 77947f82cd61ed5248c0ae48d722d7fbfdf29a9a;
CircuitWeave Core master 26724fc70a1cb7c403aa50d8e0f41383202a22ac.

PR #14 owns the **only** Csc-capture→Core-v2 producer in this package:
HWIR/EvaluatedProjectGraph/project_csc_to_core_v2.py. This QA extension
does **not** introduce another converter, v1/v2 schema, Csc executor or
transport authority.

## What the tests actually do

1. Using a fresh synthetic Golden and isolated Csc observation fixture (not
   executed compiler), run PR #14's **real production Python converter** into
   a new OS-TEMP workspace. Verify stage index/chunk hashes, the original
   fixture byte SHA before/after, and expected source/reference roles.
2. Compile the actual BridgeSourceSnapshotV1, BridgeSegmentIndexV2 and
   BridgeProvenanceCodecV2 source files into a standalone .NET 11 test host,
   with build artifacts outside Golden.
3. Load the projected index.json, capture-crosswalk.json and staged file bytes.
   Recreate the same rows with BridgeSegmentIndexV2.Write and
   ValidateAgainstActualBytes. Compare exact **raw JSONL chunk and index
   bytes**, not merely object equivalence or an exit code.
4. Fail closed for mutated actual staged source bytes, mutated raw index,
   role-promotion in the crosswalk. Record exact .NET SDK, real exit codes,
   raw test log SHA and built assembly SHA in GitHub Actions output.
5. On test teardown remove only newly created synthetic workspace; never
   modify an existing Golden checkout.

This is stronger than a Python writer comparing its own Python parser, but
it is still **same-repository serializer parity**. It does NOT show that
an isolated Csc execution ran, establish actual PE/PDB, bind independently
approved source methods, execute Golden, import through Core-owned reader,
or prove RTL equivalence.

## Reproduction (Windows-first)

Windows 2025 + PowerShell 7, Python 3.13, .NET SDK
11.0.100-rc.2.26504.105, separate OS-TEMP workspace:

~~~powershell
$project = 'Compilers/HybridCPU_Compiler/HardwareBridge/Tests/CscCoreV2Parity/HardwareBridge.CscCoreV2Parity.csproj'
$fixture = 'Compilers/HybridCPU_Compiler/HardwareBridge/Tests/CscCoreV2Parity/emit_fixture.py'
$temp = python -c 'import tempfile; print(tempfile.gettempdir())'
$out = Join-Path $temp ('hb02-parity-' + [Guid]::NewGuid().ToString('N'))
python $fixture $out
dotnet run --project $project -c Release -- $out
~~~

The workflow .github/workflows/hb-int-02-csc-dotnet-parity.yml
enforces a **separate TEMP global.json** with exact prerelease SDK and
redirected build/NuGet artifacts, and runs the three negative tests.
A CI job that is queued, skipped or fails is **NotRun/Failed**, not Passed.
The executable test and emitted SHA are recorded only on an actual run.

**Release:** keep the same existing Core v2 Schema/Roots/Chunks/ClosureSha256
wire contract; PR #14 must be reviewed/merged before this stacked parity test.
Any true Core reader handoff still needs separately pinned Core ownership and
a re-read of external expected source SHA/PE/PDB. These remain **Blocked**,
as do complete HybridCPU-v2 CPU, RTL acceptance, FPGA/ASIC and Physical signoff.


## Actual Windows QA receipt (2026-11-19)

[GitHub Actions run #44570684887](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44570684887) finished **success** on Windows 2025 with the pinned .NET 11 SDK. The real compiler/simulator/Golden execution stages were not run.

| Executed test | Actual exit | Captured stdout/stderr SHA-256 |
|---|---:|---|
| Production .NET 11 index + JSONL parity | 0 | 90B51D5F511195E91EA8F7E1B4669CBEADF6396457E177A8E37042B824EE3E67 |
| Source mutation must refuse | 1 (expected) | BA942439E6A7B1AD5E404519D4A80815A88E33CC6A078813772D86DBCB273EA5 |
| Index mutation must refuse | 1 (expected) | 4E08DC30F235D74BA2028E48E2318369D2F9BEC69F08E59E103EAD76AF225D21 |
| Metadata/source role mutation must refuse | 1 (expected) | A50AD8B6C495E977AC1CA4C2551894273D66A733BE71A7E7094B0FE122117301 |

Actual produced synthetic Core v2 closure SHA-256:
8107F7A9B676F2A39558A3FF333FABE9E547E63A45F1E57EF4104AC679A83520.
Core-shaped index SHA-256:
8F4B195CE064F06DC24C67383BD57A3497A5FBCCE959BB64360605AF18F50E7E.
Synthetic Golden source file SHA unchanged before/after:
B9AC59DEF0DC4295FBD37A0E0B9F34CED224A60DC01E1C9C7BCEF866E7ADEB7C.
Actual built executable .dll SHA-256:
4FF7B5CF48B192FB77D1B346524BCB46A76026FD15B08BF85C1EAD3F5AB2A9B0.

**Scope-limited Passed:** Python projection fixture and existing production Bridge v2 .NET byte parity only. Actual Core-owned reader applied to these emitted files, approved Golden Csc, independent source/body evidence and RTL/Physical signoff remain **Blocked/NotRun**. No Golden HybridCPU-v2 files were written.


## R2-03 — .NET/Python v2 release-wire parity extension (2026-11-21)

**Draft PR:** [#34](https://github.com/yuriyyak23/HybridCPU-Compilers/pull/34).
**Pinned pre-change Bridge master:** `9befee4f3971b4945c553cf3162ed84af94134e4`.
**Feature branch:** `feat/hb-r2-03-dotnet-python-v2-wire-parity-20261121`.
The previous 77947f8 baseline and 2026-11-19 CI receipts above are historical
and do not describe the current PR's compiled binary.

Ownership stays within `SourceSnapshot/BridgeSegmentIndexV2.cs`,
`Tests/CscCoreV2Parity/**`, this document, and
`.github/workflows/hb-int-02-csc-dotnet-parity.yml`. This PR intentionally
does not change the production C# codec, because its existing
`ValidateAgainstActualBytes` already checks whole raw index/JSONL bytes
against regenerated canonical `Write` bytes; this catches unknown fields,
duplicate JSON properties, changed property order, BOM/whitespace, wrong
descriptor SHA and versions. The R2-03 **test-host-only** manifest reader now
also rejects unknown/missing/duplicate `capture-map` and root-selector fields.
That reader does **not** become a Bridge-owned Core consumer.

### Immutable release fixture and binary test

The existing owned `emit_reviewed_stage_fixture.py` constructs deterministic
synthetic Csc-observation bytes (not an executed Csc) and calls the unchanged
production `HWIR/EvaluatedProjectGraph/stage_core_v2.py`. The synthetic
primary source fixture includes UTF-8 Unicode and **raw CRLF**; the release
stager emits numeric ASCII paths/explicit ASCII roots, preserving original
source bytes. A C#-only matrix also tests System.Text.Json's escaped Unicode
wire path; the release Python subset cannot emit arbitrary Unicode wire
identifiers and is **not** asserted to do so.

The new `r203_release_wire_driver.py` executes **both** the release Python
verification CLI and the compiled production C# `BridgeSegmentIndexV2.Write`
host, not a mock serializer, for 1/64/65/1001 primary rows (plus actual captured
metadata and one owned-body fixture at 65). It compares **raw index bytes,
all JSONL chunks, complete SHA/size/row descriptors, root selectors, file roles,
source/metadata original pins and concatenated ordered raw JSONL digest**.
A mismatch reports its first byte offset, two hexadecimal octets, and lengths.
The in-memory production C# matrix independently checks 1/63/64/65/1001/8192
rows, all six accepted wire roles, root order, duplicated/ambiguous selectors,
case-fold aliases, traversal and non-normalized names, 2 MiB source boundary,
32 MiB metadata boundary, and the existing 512 MiB workspace boundary.

The paired negative mutation matrix applies BOM, invalid UTF-8, index
whitespace, extra/duplicate index properties, incorrect version, SHA
letter-case change, corrupted JSONL, reordered JSONL, CRLF framing, duplicate
role, unknown map field, ownership change, wrong roots and stale original
source bytes. Both the Python verifier and compiled .NET host must return
**nonzero** for every changed TEMP fixture, then accept its fully restored
original bytes. These tests do **not** prove source qualification.

### CI reproduction / immutable checkout

Run the workflow
[`hb-int-02-csc-dotnet-parity.yml`](https://github.com/yuriyyak23/HybridCPU-Compilers/blob/feat/hb-r2-03-dotnet-python-v2-wire-parity-20261121/.github/workflows/hb-int-02-csc-dotnet-parity.yml)
on the Draft PR. The R2-03 job matrix uses Ubuntu 24.04 and Windows 2025,
Python 3.13 and exact SDK `11.0.100-rc.2.26504.105`.
It creates a temporary `global.json` outside the repository and pins
`BaseIntermediateOutputPath`, `MSBuildProjectExtensionsPath` and
`OutputPath` to a fresh OS-TEMP directory. The reproducible commands from
that TEMP working directory are:

```powershell
$props = @("-p:BaseIntermediateOutputPath=$obj/", "-p:MSBuildProjectExtensionsPath=$obj/", "-p:OutputPath=$bin/")
dotnet restore $project @props
dotnet build $project --configuration Release --no-restore @props
dotnet (Join-Path $bin 'HardwareBridge.CscCoreV2Parity.dll') --wire-limits
python $driver (Join-Path $bin 'HardwareBridge.CscCoreV2Parity.dll')
```

Here `$project` is the absolute
`Tests/CscCoreV2Parity/HardwareBridge.CscCoreV2Parity.csproj` path,
`$driver` is the absolute `Tests/CscCoreV2Parity/r203_release_wire_driver.py`
path, and `$obj`/`$bin` are dedicated new directories under OS TEMP.
The job hashes the executable test DLL and PDB, `dotnet` host,
pinned Roslyn `csc.dll` image, build/run logs and records actual exits.
It requires identical Git HEAD and empty porcelain before/after; no Golden
checkout is opened or built. Original source SHA inventory is checked only
for the **synthetic** fixture, not an absent approved Golden workspace.

**First executed evidence (not current head):**
[run 44836392193](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44836392193),
Ubuntu R2-03 job 136633741727 completed **success** on 2026-11-20 23:24 UTC.
Actual `dotnet restore/build/--wire-limits` and Python release↔.NET host
returned 0; 15 Python mutations returned 2 and paired .NET refusals returned 1.
Combined parity output SHA-256:
`0F44B7ECBA40E3C0C9A315D4761E9EE0739D09D476EDF79E1BC1DAEE04B9E750`.
Compiled test DLL SHA-256:
`CA7B9D6335AE7549A2A1D77AC3383DE2A6EBB7A9CF14C74FA7560C7DF0665898`.
The final PR head must be evaluated independently after all subsequent commits.

| Stage | R2-03 status |
| --- | --- |
| Production codec / Python release integration harness | Implemented |
| Ubuntu first-run synthetic binary parity (historical PR head) | TestedInCI |
| Windows / final PR head | Pending independent run |
| Exact fresh Golden Csc process, full source and PE/PDB closure | NotRun |
| Core-owned importer and independent Roslyn verifier | NotRun |
| RTL/verification/AcceptedRTL authority | NotRun |

**External handoff:** if a different Python canonical wire byte is found,
record both pinned fixture digests and the first differing byte and open an
R2-01 dependency; never edit its Python production code here. Any incompatible
wire migration requires a separately approved schema version and a Core owner.
No PR or modification in the private CircuitWeave repository is part of R2-03.


### Frozen release-wire contract fixture (after real CRLF correction)

The checked-in `Tests/CscCoreV2Parity/r203_release_wire_driver.py` now pins
these **raw** expected SHA-256 values from the existing actual Ubuntu
production-release-stager / compiled C# codec pairing:
[run 44836465436](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44836465436),
job `136633980695`, actual `exit=0` for all positive and refusal checks.
They are compared as immutable expectations; the driver does **not** derive
expected values by copying the output it is inspecting. The digest of the
concatenated JSONL is the existing `ClosureSha256` contract, not a hash of
the original input byte content.

| Primary source rows | index.json SHA256 | concatenated raw JSONL SHA256 |
| ---: | --- | --- |
| 1 | `A8C40E8EAAC55B9DDBD78A56635D6567FDDFA277A7714F80BA02267202CB7131` | `D7021F54A9BD835E933DB7C5D71463B67D135E72462256079CF1A40AD8A9319E` |
| 64 | `5368902CD828E7CC2CA4F782B2D445624F5CBD608B14345D5ABA998389F4EF20` | `826EDFB86D85322CDB821C6F849BA0F5BD134D24B3DD580FA38835AFC8B0BB4A` |
| 65 | `2C97DB17A3579BB50A834C4524298DFDAE97EF56C44F24F27FEA749145E792C2` | `2E39298B7F1B063A8ABE814033C1E7F568A67350C5231307CD0B36709BFCD885` |
| 1001 | `5FF353C3293D50A1774AA62B1C0D35D5BDA20995DFE30E486533F62AC42C28D5` | `27F42F514ABEB3B4B1B28E53515A8099C02D58A7AA8AF6EFEE6E1B2E0118962C` |

Root-selector manifest SHA256 (all four):
`CF02F40E130BF1F63A105962A2BFEA74BC0628B8CEC721B1C69F82EB26D4D9DD`.
Rows shown are **primary source counts**; the transport additionally includes
metadata and, for count 65, an owned-body claim; JSONL descriptor row counts
and sizes are asserted by the existing codec and release verifier.

On that corrected-CRLF Ubuntu run, compiled synthetic test host DLL SHA256:
`AB52766D4B080FEF8B2B8CEB3C48C29B630F879E6D06669247AE096447BCA3EB`;
positive driver log combined SHA256:
`D3128D52D683BB973825A2DECF828FA8101DD3BDD1937323CC79890B29ED11E3`.
This is a reviewed **synthetic fixture**, not any actual Golden software
compiler PE/PDB or independently reviewed Golden closure. A change to one of
these frozen SHA values must be explicitly reviewed as a deliberate fixture
transition, not auto-updated or silently relaxed.

The later R2-03 PR head adds explicit hash reporting for the **test**
PDB and the pinned SDK's Roslyn compiler DLL; these are distinct identities
from any real executed Golden compiler PE/PDB and have not been substituted
for such evidence.

## R2-03 post-merge workflow repair PR #42 (2026-11-21)

- Draft PR: https://github.com/yuriyyak23/HybridCPU-Compilers/pull/42
- Base Bridge master: `d243dd7ecce243cc08a13303cf01247ba7a65024`; independent private Core reference: `b5c44685197a40df99a4d48891120b12475c6ca5`.
- Feature branch: `fix/hb-r2-03-actions-wire-parity-20261121`. PR #34 was merged previously; this is residual CI repair and additional unqualified harness testing, not a wire-schema migration.
- At the baseline run https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44838125677, GitHub returned `failure` with **zero jobs**. The confirmed file corruption was an unterminated `[regex]::Match` expression and a duplicated detached suffix after the workflow footer; that is a **workflow parse/job-discovery problem**, not evidence of a failed executed serializer.
- The repaired workflow preserves the Windows production transport job and the 2-OS `ubuntu-24.04` / `windows-2025` release parity matrix. It pins SDK `11.0.100-rc.2.26504.105`, proves the actual `csc.dll` path/hash, keeps restore/build outputs in OS TEMP, checks a SHA-256 manifest for *all tracked checkout files* before/after and logs command argv, exit, elapsed duration, and separate stdout/stderr SHA-256.
- `R2_03_workflow_guard.py` rejects a detached YAML suffix, duplicate footer/parity invocation, malformed SDK-parent regex, wrong SDK pin, missing OS/matrix jobs. It is a **targeted static guard only**; authoritative YAML/Actions parse requires an actual nonempty job list and runner execution.
- `r203_release_wire_driver.py` retains frozen raw index and concatenated JSONL SHA values and the real compiled production codec host. Additional negative temporary-fixture mutations cover missing chunk, duplicate map/root JSON fields, unknown map schema, path collision/traversal, size lie, and PDB-role promotion. The driver separately refuses a nonexistent output DLL.
- The existing .NET production codec high-water test includes 1, 64, 65, 1001, 8192 rows, 2 MiB source, 32 MiB metadata and 512 MiB aggregate limits. These are **not** claims of an actually executed 64 MiB Golden compiler closure.

### Evidence and external boundary

Earlier repaired workflow job discovery on run https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44854546807 returned three actual jobs, not zero. On that **intermediate** head (`d766f711a50bfb6958398078506f4c5f54a32a40`):
- Ubuntu production codec build, `--wire-limits`, Python release stage and existing 15 negative pairs **passed**.
- Windows pinned SDK restore/build, compiled managed DLL, Roslyn `csc.dll` attestation and `--wire-limits` **passed**, but the release stage refused with `BLOCKED release-stage synthetic fixture: file replaced between check and open`. This refusal originates in `segmented_csc_capture_v2.py::stable_open` during `stage_core_v2.py` input verification, within the **R1-02 / R2-01 upstream boundary**, not the R2-03 codec implementation. Do not suppress the refusal or alter another owner's capture code.
- Final head acceptance remains **NotRun / pending final-head jobs** until both operating systems actually complete the expanded matrix. A queued workflow or static YAML check is **not** `WireParity=Passed`.
- Real approved Golden Csc, OS execution witness, independent private Core reader and accepted RTL all remain **NotRun/Blocked** and are not promoted by synthetic fixture success.

The workflow CI must stay red if Windows capture verification cannot establish identical before/open file identity. Record the actual head SHA, job URLs and failure diagnostic and send the issue to the R1-02/R2-01 owner separately; do not modify their files in this PR.
