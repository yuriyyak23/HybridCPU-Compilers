# HB-INT-02 / CW-INT-03: C# byte-parity regression (stacked on PR #14)

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
