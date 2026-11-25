# P3 — CIL и semantic proofs

RestrictedCilImporterV1 — semantic owner managed ingress. BodyWorld supplies explicit selected bodies и exact definitions; malformed metadata/header/token/generic/EH и budget rejection остаются fail-closed. Наличие controls не является exhaustive fuzz/security qualification.

Расширение идёт от actual emitted CIL/metadata к конкретному decoder/proof/lowering gap и source→image. Exact role/nominal identity/context/purity/CFG/SSA/EH/fault/original budgets обязательны. Unsupported и BudgetExhausted не объединяются. Discovery не даёт admission.

Readers имеют call-owned lifetime; dependency не получает primary PDB. Persistent cache/concurrency не следует из sequential reuse. Реализованные scalar/nullable/loop scopes — [93](93_ACCEPTANCE_MATRIX.md), код — [94](94_SOURCE_INDEX.md).

Это область ответственности, не отдельная очередь. Следующий consumer — [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md); validation — [96](96_TEST_STRATEGY.md); применимость evidence — [00](00_CODE_VERIFICATION_REPORT.md). Общая qualification не подтверждена.
