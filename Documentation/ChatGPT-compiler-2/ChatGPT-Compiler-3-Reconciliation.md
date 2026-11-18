# Compiler 3 reconciliation

Status: 2026-09-03. The third audit is backlog input, not authority.

| Topic | Current disposition |
|---|---|
| Publish versus component qualification | Preserved. Derived `eh-unwind` evidence does not bypass `HCPUB1004`. |
| Release evidence | Structural envelope checking exists, but phase-specific capability extraction and loader-backed proof remain required. |
| Guest ABI | Bounded compiler-side bindings exist; an end-to-end real-image CPU corpus remains required. |
| DIM | Bounded only; do not claim full C# DIM. Keep unsupported forms in negative corpora. |
| `Kernel.NoHeap` | Source diagnostics exist; SDK wiring and transitive graph proof remain open. |
| Dependency body authority | Open: neighbouring DLL presence alone must not grant all RVA bodies authority. |

The active order is in `Open-Tasks.md`. Do not reorder it based on audit severity labels.
