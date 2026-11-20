# R0-02 — Private Core read-only CI gate

Scope: .github/workflows/hb-int-06-no-write.yml, Tests/HbInt06.CoreGate.ps1 and this document only. R2-02 cross-repo replay workflow/harness, parent Run-HbInt06.ps1, Core, Golden and software compiler builds are NOT owned here. This gate does not qualify Golden, HWIR, RTL, or accepted hardware.

## Reviewed identities (2026-11-21)

| Object | Reviewed identity |
| --- | --- |
| Bridge master rechecked | 9befee4f3971b4945c553cf3162ed84af94134e4 (one commit after 8d3453dcc02bfb63710727f4f602edd9bc31efad) |
| CircuitWeave Core master rechecked | b5c44685197a40df99a4d48891120b12475c6ca5 |
| R0-02 exact Core review pin | b5c44685197a40df99a4d48891120b12475c6ca5, supplied as the exact reviewed commit in the task; fixed in workflow and gate |
| Superseded Core pin | 18caf04768df6abf31f5517dbb489e7fea2e1c7e |
| Pinned Golden source (unchanged) | 6946ccab9d888f7dae0a4d8e18480f436138acd9 |
| Pinned CI SDK (unchanged) | 11.0.100-rc.2.26504.105 (not the toy observer 11.0.100) |

Prior evidence: [HB-INT-06 run 44740086741](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44740086741), paired Core preflight Blocked (absent credential), separate Windows Companion build Failed; paired Core NotRun. [HB-INT-05 run 44740086765](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44740086765) also Failed with unrelated inventory assertions. Historical Core runs [44739493101](https://github.com/yuriyyak23/CircuitWeave/actions/runs/44739493101) and [44739492781](https://github.com/yuriyyak23/CircuitWeave/actions/runs/44739492781) Failed independently.

## Owner actions and least privileges

1. Protect the Bridge master branch via branch protection or ruleset. The private gate requires GITHUB_REF_PROTECTED=true and exact event/repo/ref/SHA. Push/dispatch is trusted only on protected refs/heads/master. Prevent unreviewed modifications to workflow code on master.
2. In HybridCPU-Compilers repository Settings > Secrets and variables > Actions, the repository owner must create HB_INT_06_CORE_READ_TOKEN. Recommend a short-lived fine-grained PAT restricted to ONLY the private yuriyyak23/CircuitWeave repository with Contents: Read-only plus mandatory metadata. No write, admin, workflow or broad repository privileges. Owner handles expiry, SSO, approvals and rotation. Never put the token in a PR, dispatch input, job log, artifact or commit.
3. Bridge workflow GITHUB_TOKEN has contents: read, with all other permissions denied. Checkout uses persist-credentials: false, no submodules or LFS, and exact Core SHA with full history. GitHub checkout returning success is NOT independent qualification: gate checks origin, SHA, clean checkout, no shallow history, no linked project path, and approved Core test-host project.
4. Do not use pull_request_target to execute untrusted code with secrets. Untrusted pull_request runs see no private credential and the paired Core gate visibly fails Blocked rather than green skipped. Presence of a token does not prove authorization. Private checkout and Core importer remain NotRun if preflight fails.
5. Do not use a moving master ref or automatic SHA update. A later Core upgrade requires owner-reviewed exact 40-character lowercase commit ID, inspection of the pinned Core importer test project, and coordinated updates to the static gate allowlist and workflow exact checkout and runner arguments.

GitHub documentation: [workflow permissions](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax), [secrets](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-secrets), [ref protection context](https://docs.github.com/en/actions/reference/workflows-and-actions/contexts), [fine-grained PAT](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens).

## CI trust and mutation matrix

| Scenario | Preflight | Core checkout | Core importer | Contract job |
| --- | --- | --- | --- | --- |
| PR, fake event, non-master event, wrong repository | Blocked | NotRun | NotRun | failure |
| Master unprotected or event/Bridge SHA mismatch | Blocked | NotRun | NotRun | failure |
| Protected master push/dispatch, token missing or malformed | Blocked | NotRun | NotRun | failure |
| Token present and correct reviewed pin | Passed (presence only) | attempt | NotRun until checked | pending |
| Wrong or uppercase Core pin | Blocked | NotRun | NotRun | failure |
| Credential checkout denied, wrong origin/SHA, shallow, dirty, stale or linked test path | Passed | Blocked | NotRun | failure |
| Checkout accepted, downstream exits nonzero | Passed | Passed | Failed | failure |
| Downstream exits 0 but receipt missing, duplicate/invalid JSON, Core importer/Companion not Passed, changed sources | Passed | Passed | Failed | failure |
| Actual Core test host, Companion and no-write receipt all pass | Passed | Passed | PassedUnqualifiedContractOnly | success for this bounded contract only |

The distinct states are represented in machine-readable stage receipts at RUNNER_TEMP/hb-r0-02-core-gate/{preflight,checkout,downstream}.json, schema hybridcpu.hardwarebridge.r0-02-core-gate/v1. Outputs are new files in runner TEMP and include allowlisted event class, reviewed/requested Core SHA, state, reason, command chain, exit and SHA-256 digests of git stdout/stderr. No command stdout, private code, credential, absolute private checkout path or raw Golden artifact is uploaded by this gate. Typed Blocked exits 2 and Failed exits 1: GitHub shows both as failed jobs; read the receipt to distinguish them.

Existing Run-HbInt06.ps1 still owns actual Bridge/Golden no-write inventory and the Core-owned CW-INT-03 importer test host. This gate records its real exit and rejects missing or invalid runner QA receipts. A synthetic fixture passing never counts as a real Core import. Public paired summary is allowlisted metadata, not verification evidence.

## Regression execution

On a Windows runner with PowerShell 7.3 or newer, invoke:

    pwsh -NoProfile -File Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HbInt06.CoreGate.ps1 -Mode SelfTest

This creates only a synthetic temporary Git repository and exercises trust rejection, token absence/malformed-env, wrong/mixed-case SHA, fake PR events, wrong repo, wrong remote origin, stale path and traversal, dirty checkout, shallow clone, missing paired receipt, duplicate JSON property. No private Core/Golden worktree is used. Synthetic negative tests run on the PR workflow without secrets.

When the owner has installed a scoped read-only token and protected Bridge master, trigger the HB-INT-06 workflow from trusted master using workflow_dispatch. Inspect the Core checkout job, paired validator, published R0-02 receipts and both standalone job conclusions. Do not claim Core reader Passed unless there is a real checkout of commit b5c44685197a40df99a4d48891120b12475c6ca5 and actual Core-owned test execution with checked no-write inventories.

## External typed dependencies

- Repo owner: protected master; explicit private Core read-only credential. Until then Core importer is Blocked/NotRun.
- R0-01 / software compiler owner: real Companion build errors (HybridCPU.Compiler.Core.IR.Artifacts, CompilerEmissionPackage, CompilerSidebandEnvelope and related envelopes) remain Failed, not hidden by token gate.
- CW-INT owners: Core importer, independent Roslyn verifier, source admission and Core CI repairs; no Core changes from this PR.
- R2-02 owner: paired Golden replay workflow/harness, separate from this workflow and script.

No Core PR or Golden source change was made in R0-02. Golden/ Core before-and-after inventories are NotRun if the existing paired runner does not actually execute. PE/PDB and executable identities are recorded only by actual executed runner stages, not generated synthetically by this gate.
