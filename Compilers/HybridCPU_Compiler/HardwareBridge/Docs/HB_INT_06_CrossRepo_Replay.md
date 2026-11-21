# HB-INT-06 — opt-in cross-repository software-source transport replay

**Owner:** HybridCPU-Compilers / HardwareBridge tests only. The only v2 importer is the Core-owned `BridgeSegmentIndexImporterV2.Import`. No Core implementation, new hardware verifier, source resolver, or lowering authority has been added to Bridge.

## Command contract

`Tests/Integration/Run-CrossRepoReplay.py` takes existing **read-only** Bridge, Core and Golden Git checkouts; exact reviewed `--bridge-sha`, `--core-sha`, `--golden-sha`; reviewed lowercase `--closure-sha` of the actual HB-INT-02 closure; an explicit roots-manifest `--roots` and `--roots-sha`; `--graph`, `--observation`; exact `--sdk` and absolute `--dotnet` executable; and a fresh `--output`, a new direct child of host TEMP. `--scope fixture|golden` is mandatory. HEAD alone does not qualify dirty worktree bytes.

**Real Golden additional mandatory inputs:** `--observer-argv` (JSON array starting with absolute executable), `--observer-argv-sha`, `--observer-exe-sha`, `--graph-sha`, `--observation-sha`; fresh isolated `--pe`, `--pdb` and `--pe-sha`, `--pdb-sha`; `--facts-argv`, `--facts-argv-sha`, `--facts-exe-sha`, `--facts`, `--facts-sha`. Observer and facts executables and invocations are SHA-pinned before running; the output graph/observation and PE/PDB facts must be independently reviewed. The observation must contain actual task-time expanded Csc argv, generated sources, references, compiler bytes, source mapping and options in the pre-existing HB-INT-02 schema. EvaluatedProjectGraph alone and synthetic JSON are not proof of Csc execution. Actual observed outputs must be new and live under external TEMP; no stale output is accepted.

Sequence: content-inventory original Golden (every file other than .git, Git HEAD plus porcelain) → copy to private isolated workspace → execute approved external R1-01 observer (only in Golden mode) → real `segmented_csc_capture_v2.py capture` with reviewed closure SHA (R1-02) → check PE/PDB byte pins and separately invoke software compiler facts (R1-03, only in Golden mode) → `stage_core_v2.py stage` with reviewed roots SHA (R2-01) → build a disposable test-only host from a **writable isolated copy of the pinned Core** and invoke its real `BridgeSegmentIndexImporterV2.Import` (R2-02). The existing producer's direct-TEMP-child constraint is handled with fresh temporary siblings relocated under the evidence root after each completed stage.

Golden source, original csproj, tool options and generated outputs are never modified or restored/built in place. No parent traversal, linked/junction path, duplicate JSON property, stale artifact, missing generated source or wrong role/digest may be admitted by the underlying producers. Raw command argv, executable SHA, rc, SHA/bytes of stdout/stderr and per-stage status are stored in `receipt.json`. Private logs in `logs/*.stdout.bin` and `logs/*.stderr.bin` might contain sensitive compiler diagnostics and **must never be uploaded for real Golden**. Host-side inventory digest is built from sorted UTF-8 `relative-path<TAB>decimal-size<TAB>UPPERCASE-SHA256<LF>` records; sourceUnchanged requires identical before/after file-tree SHA, HEAD and status.

A successful transport reader returns **Candidate / CWINT03_V2_UNQUALIFIED_CLOSURE**. `FixtureTransportOnly` is fixture scope, and `CandidateTransportOnly` is real-byte Golden *transport* scope, **not Verified / accepted RTL / Full Golden E2E**. For fixture the observer and PE/PDB stages are deliberately NotRun. Genuine PE/PDB origin-to-method linkage and independently qualified Roslyn source/verification remain Core-owned CW-INT-04/10 gates and Blocked pending independent receipts. A zero producer exit, index checksum, parsed JSON or a green synthetic CI job must not promote any of these statuses.

## GitHub Actions runbook

`.github/workflows/hb-int-06-cross-repo-replay.yml` has (1) always-on fixture/refusal Python tests, including the existing 1001-file source producer test, (2) explicitly dispatched paired 1001-file **producer → true Core reader** replay, and (3) separately gated real Golden (currently a fail-closed **Blocked** job pending reviewed R1 observer/facts integration, never a false pass). Configure `HB_INT_06_CORE_READ_TOKEN` as a fine-grained short-lived read-only `contents:read` token on private `yuriyyak23/CircuitWeave`, dispatch `approved_fixture=true` with the reviewed exact `reviewed_core_sha`; exact .NET SDK is `11.0.100-rc.2.26504.105`. Checkout uses `persist-credentials: false`. The paired job uploads only the fixture `receipt.json`, not raw source or logs. If not dispatched, Core reader = NotRun, **not Passed**.

Real Golden additionally requires `approved_golden=true`, reviewed `reviewed_golden_sha`, `HB_REAL_GOLDEN_READ_TOKEN`, `HB_REAL_SOFTWARE_BUILD_APPROVED=true`, `HB_REAL_R1_OBSERVER_READY=true`, fixed software build, and an approved host-provisioned exact R1 observer / R1 PE-PDB command and reviewed roots. As shipped this job **explicitly refuses** rather than run an unreviewed build. An owner must supply those inputs and review the executable/argv SHA and no-write guard before enabling it. No GitHub token is printed or left in source checkouts.

Baseline observed 2026-11-20: Bridge master `797b6dd4fc1c7a44f958ebb22d43e745d0e6938c`; Core master `18caf04768df6abf31f5517dbb489e7fea2e1c7e`. Former HB-INT-06 standalone Companion fails; paired Core blocked on `HB_INT_06_CORE_READ_TOKEN`. Re-review all SHAs before dispatch.

## Tests / diagnostic ownership

`python -m unittest -v Tests/Integration/test_cross_repo_replay.py` covers 13 local no-write/dirty/source-hash/link/duplicate JSON/wrong-pin/timeout/rc/log-SHA refusals. Existing `HWIR/EvaluatedProjectGraph/test_{segmented_csc_capture_v2,project_csc_to_core_v2,stage_core_v2}.py` cover source/generated/role/index mutations and a 1001 source producer. Neither local Python test group executes .NET/Csc. For an actual production Core reader refusal: preserve raw stderr SHA, exact SHA pins and failed index role, stop at R2-02; create a CW-INT-03 Core-owned issue/dependency, not a Bridge parser workaround. No Core issue is required unless a verified real producer/consumer incompatibility is reproduced.


## R2-02 — Core-executed positive + negative replay (2026-11-21 update)

**Pinned inspection:** Bridge `master=9befee4f3971b4945c553cf3162ed84af94134e4`; private Core `master=b5c44685197a40df99a4d48891120b12475c6ca5`. These are observations, not future auto-updated review pins. Bridge `8d3453dcc02bfb63710727f4f602edd9bc31efad` and historical runs `44642947832`, `44740086741` are *not* proof of paired Golden replay. The prior successful cross-repo workflow ran only Python fixture refusal tests; the private-Core and Golden jobs were skipped.

**Trust gate:** `workflow_dispatch` must be on `refs/heads/master` of `yuriyyak23/HybridCPU-Compilers`, not PR/head/fork. Select **exactly one** `approved_fixture=true` or `approved_golden=true`, provide a reviewed 40-hex lowercase `reviewed_core_sha`, and additionally `reviewed_golden_sha` for Golden. A fine-grained `HB_INT_06_CORE_READ_TOKEN` must be owner-provisioned with only private Core repository `contents:read`. The runner's implicit `GITHUB_TOKEN` is **not** private-Core access. Missing permission/pin makes the paired job **Blocked (nonzero)**, not green skipped; the dispatch authorization job also fails on unknown/ambiguous mode. On PRs only Python source/mutation-shape tests are authorized and the paired job is correctly **NotRun**. An exact event Bridge SHA checkout and exact private Core SHA checkout are independently verified before commands; token credentials are not persisted. No other repository is modified.

**Actual paired-fixture call:** after the trusted dispatch and token gate, `PrepareFixtureReplay.py --count 1001` produces a *synthetic* fixture; `Run-CrossRepoReplay.py --scope fixture` checks immutable original checkout SHAs, Git status and full-file byte inventories. The real pre-existing HB-INT-02 producer captures the input closure, the real R2-01 stage produces canonical Core-v2 JSONL, and the disposable `CoreReaderHost` is compiled against an isolated copy of exactly pinned Core. The host calls `BridgeSegmentIndexImporterV2.Import` (not a Python importer), returning only `Candidate / CWINT03_V2_UNQUALIFIED_CLOSURE` on a positive match, with >=1001 imported inputs. The host executable DLL SHA and the raw stage index SHA/closure, captured chunk manifests, process argv/executable SHA/rc/stdout/stderr SHA and pre/post staged byte-tree digest enter `receipt.json`. The original Bridge/Core/Golden trees are re-inventoried in `finally` and any drift overrides success to **Blocked**.

**Actual pinned-Core negative matrix:** only *after* Core Candidate on the unmodified stage, eight fresh copies of that stage (seven required corruption classes plus retained raw-chunk SHA mutation) are each submitted to **the same compiled Core importer**. A negative counts as passed only if Core returns `exit=1` **and** the exact expected diagnostic, not merely a process crash or wrong SDK. Inputs and expectations:

| Fixture-only mutation | Expected Core diagnostic |
|---|---|
| staged input byte flip, unchanged source SHA pin | `CWINT03_STALE_BYTES` |
| mutated JSONL chunk, unchanged chunk SHA pin | `CWINT03_V2_CHUNK_SHA` |
| omitted JSONL chunk | `CWINT03_V2_IO` |
| duplicate chunk descriptor/path in a re-pinned index | `CWINT03_V2_CHUNK_ORDER` |
| intentionally wrong caller index pin | `CWINT03_V2_INDEX_SHA` |
| duplicate selector in re-hashed canonical index | `CWINT03_V2_ROOT` |
| duplicate JSON property in independently re-hashed index | `CWINT03_V2_SCHEMA` |
| unauthorized row role with test-only re-hashed chunk, closure and index | `CWINT03_V2_ROLE` |

Re-hashed mutation pins are explicitly **test-only**, never independent source admission. `r2_02_core_mutations.py` prepares bytes but never judges Core admission, while the .NET host alone invokes Core. Stale result files are forbidden (`FileMode.CreateNew`). Any wrong diagnostic, unexecuted Core, failed host build, mutated positive stage, changed executable, or failed no-write inventory results in **Blocked/Failed**. Mutated staged files and raw Core/Golden inputs are not uploaded; only the allowlisted fixture receipt may be uploaded.

**Reproduce from repository root** (no external GitHub permissions needed for Python tests):

```bash
cd Compilers/HybridCPU_Compiler/HardwareBridge
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -v -s Tests/Integration -p 'test_*.py'
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -v -s HWIR/EvaluatedProjectGraph -p 'test_*.py'
```

For real paired Core QA, after owner installs the read-only token and the reviewed Core SHA, dispatch **on trusted master** with `approved_fixture=true`; the `paired-fixture-core` job uses the installed exact `11.0.100-rc.2.26504.105` SDK. `11.0.100` from any separate toy observer is a **different identity**. Local environments without dotnet 11 can run the Python mutation-shape tests but **cannot** claim Core-paired success. Read the job URL and only the allowlisted `receipt.json` after actual dispatch.

**Golden-real:** deliberately a **separate Blocked branch** even when `approved_golden=true` until genuine task-time R1-01 Csc observation, R1-02 source/no-write closure, R1-03 executed PE/PDB and method map, Companion and their reviewed binary/argv SHA receipts exist and are supplied by their respective owners. Neither a synthetic compiler PE fixture, an exit-zero command, nor a Core Candidate proves full Golden closure, verified hardware, or accepted RTL. No private Golden source/stdout/PDB or raw Core snapshots belong in public Actions artifacts. Missing prerequisites are **Blocked**, not silently substituted with fake/fixture results.

**External dependency:** on a witnessed real Bridge producer -> pinned Core diagnostic mismatch, preserve the *smallest* synthetic failing stage, expected/actual diagnostic and both exact SHAs, then ask the CW-INT-03 Core owner to correct its importer in a **separate Core PR**. This R2-02 package never patches private Core, Golden, the SDK pin or other R0–R3 ownership paths. If paired CI has no private token, report Core execution **NotRun/Blocked by owner-provisioned access**, not TestedInCI.

## R2-02 residual closure: November 21, 2026 (post-PR #39)

**Pinned inspection:** Bridge master `d243dd7ecce243cc08a13303cf01247ba7a65024` (the prior `b06cd1d42e88d53f35bd8054e06fcfab6f07bff4` is historical); private Core master `b5c44685197a40df99a4d48891120b12475c6ca5`. PR #39 is merged. [Run 44837963823](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44837963823) reported `fixture-refusals=success`; `paired-fixture-core`, `dispatch-policy`, and `golden-real-approved` were all **skipped**. That run did not execute the private Core importer.

**Incremental code changes:** The trusted paired fixture job relocates its separately checked-out private Core to a fresh `RUNNER_TEMP/R2_02_core_<run>_<attempt>` directory before using it. The original private Core checkout remains read-only; the existing runner builds a Core-dependent disposable host only from a separate TEMP copy, and calls Core-owned `BridgeSegmentIndexImporterV2.Import` without cloning the parser. Core credentials remain checkout-scoped with `persist-credentials: false`. Current positive reader contract remains `Candidate / CWINT03_V2_UNQUALIFIED_CLOSURE`, at least 1001 rows, **not** independent verification.

All seven existing corruption tests are retained. An eighth variant duplicates the first JSONL chunk manifest descriptor while retaining raw input bytes. It independently re-pins its test-only index to reach the genuine Core chunk-order validator and requires `CWINT03_V2_CHUNK_ORDER`, exit=1, from the same host DLL. Unlike replacing a test, this preserves the earlier `CWINT03_V2_CHUNK_SHA` coverage.

**New receipt fail-closed boundary:** `Tests/Integration/R2_02_ReceiptGate.py` independently checks the *shape* of the allowed fixture `receipt.json`: exact Bridge/Core pins and SDK `11.0.100-rc.2.26504.105`; SHA and exit evidence for the SDK probe, producer, R2-01 stage, host build and all Core invocations; >=1001 source rows and exact Core diagnostic; eight specific error codes and `exit=1` for all mutations; unmodified original Bridge/Core/synthetic fixture Golden checkouts and staged workspace. Skipped/partial stages, wrong SDK, duplicated JSON properties, altered host binary pins, dishonest negative `exit=0`, stale positive output, missing inventory or source drift cause nonzero refusal. Gate output never includes private paths or raw diagnostics.

**Scope:** this validator does **not** execute Core by itself, verify software source provenance or promote Candidate to Verified/AcceptedRTL. A local synthetic unit-test PASS proves only receipt policy behavior. Even a successful future paired fixture dispatch proves only actual Core importer *transport* behavior, not real Golden Csc/PE/PDB or hardware correctness.

**Remaining blocked owner work:** owner-provisioned `HB_INT_06_CORE_READ_TOKEN` with private CircuitWeave `contents:read`; explicit approved exact Bridge/Core SHA via master `workflow_dispatch`, and review of the resulting run/job receipt. Golden remains blocked until separate R0-01 Companion, R1-01 true task-time Csc witness, R1-02 no-write closure, R1-03 executed PE/PDB method mapping, independently reviewed Golden SHA and `HB_REAL_GOLDEN_READ_TOKEN` plus independent Core admissibility. No unreviewed build and no private byte payload uploaded. On an actual mismatch create a precise separate CW-INT-03 Core-owner dependency; do not patch Core from Bridge.

**Local bounded tests** (do not run private Core):

```bash
cd Compilers/HybridCPU_Compiler/HardwareBridge
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -v -s Tests/Integration -p 'test_*.py'
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -v -s HWIR/EvaluatedProjectGraph -p 'test_*.py'
```
