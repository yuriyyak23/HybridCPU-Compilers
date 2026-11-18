# 93 — Матрица возможностей и acceptance

9 ноября 2026. «Реализовано» означает ограниченный контракт текущего owner, а не полную языковую или runtime совместимость. Последние сохранённые runs и применимость хэшей — [00](00_CODE_VERIFICATION_REPORT.md). Новая test campaign при обновлении документации не проводилась.

| Область | Реализовано в существующих owners | Граница |
|---|---|---|
| Core | Canonical facade, shared scheduling/bundling/agreement/post-bundle, bounded witnessed RA | Не новый ISA и не runtime authority |
| C# companion | Diagnostics/NoHeap, deterministic PE/PDB, CLI source→image | Default CSharp13; CSharp15 требует explicit matching SDK lane; не full-language support |
| BodyWorld | Bounded closure, exact definitions/generic contexts, explicit body roles | Declaration-only reference не даёт body authority |
| Provenance/maps | Source/artifact/import/options identities, primary PE/PDB maps | Dependency не получает primary PDB |
| Scalar values | Immutable single-scalar I4/I8 constructors/getters/factories/local/copy/join, selected external definitions | Exact nominal/layout/purity proof; не general aggregate ABI |
| Arguments | Primitive/reference assignments, проверенные arrays/String consumers | Layout/accessor/fault authority не выводится из ObjectReference |
| Nullable locals | Primitive carriers и enum integral underlyings, default/copy/presence/accessors, checked Value | Nominal widths и internal lowering; не общий nullable/EH ABI |
| Private Nullable<int> | Primary non-root input/return, fallback computations, scalar/nullable dependencies | Public/root/external signatures и broader byref/EH отдельны |
| Scalar helpers/CFG | Pure direct/transitive calls, typed forward CFG, доказанные counted/nested loops | Original budgets, no-wrap, faults, cctor/EH/role/identity guards |
| Constrained helpers | Exact signed I4/I8 owner/method contexts, private static 1–8 `U` operands, `U` return, `default(U)` | Zero-input, arity9, general constraints/byref/aggregate не открыты |
| Cancellation | Scoped checkpoints в ingress/shared orchestration/backend/link | Не universal latency или shared concurrency guarantee |
| Policies/profile | Immutable policies, explicit opt-in profile lane/trace | Нет доказанного общего performance win или persistent cache acceptance |
| Runtime adapter | Concrete emission/transport contract | Image success не loader/guest/publication |
| Release tools | Structural validation, identities, scalar summaries | Декларации clean builds и parser success не actual build/execution receipts |

## Последний компонентный блок

v137 подтвердил generic scalar zero в 48 source и 48 physical cases: arity3–8 × selected I4/I8 × owner int/long × direct/transitive. Unary/binary zero дополнительно проверены CLI. Full default/SDK/Managed runs имеют exit 0; counts и manifests хранятся в00/report, а не дублируются здесь.

Проверены deterministic PE/PDB/maps/image repeat, independent scalar/host/encoded/serialized oracle, presence/extrema/high-bit/overflow, original inputs и eager call counts. Generic/concrete I8 zero семантически эквивалентны; code-byte equality для них не заявлена. Negative families охватывают roles/constraints/identity/body/effects/EH/byref/budgets/fault/cancel/reuse/diagnostic precedence в указанном в report объёме, не все возможные комбинации языка.

## Общие пределы

MatrixTile lane6/HardPinned сохраняется. Release wide-remainder exact equality и независимый quotient/MUL/SUB physical oracle подтверждены последним компонентным набором. Debug equivalence не следует из Release результата.

Full solution, произвольная BCL, async/unsafe/interop/trimming, general aggregates, loader/guest/publication и обычная стабильность preview host не квалифицированы этими runs. Общая qualification — **нет**. Следующий consumer определяется только [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md); широкие ограничения — [99](99_BLOCKERS_AND_CLAIM_BOUNDARIES.md).
