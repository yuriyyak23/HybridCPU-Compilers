# P1 — Ownership и runtime edge

Canonical facade и compiler stages физически находятся в Core. Upper compiler содержит concrete ISE emission/production/decomposition и runtime-facing tooling. Core зависит от Platform.Contracts; обратная зависимость от concrete ISE не должна появляться. [Project graph](07_PROJECT_GRAPH_DECISION.md).

Новый перенос оправдан конкретной зависимостью или дублированием ответственности. Один source имеет один evaluated compiling owner. Selective compile groups допустимы. При переносе сохраняются namespace/API/defaults и bounded bytes/annotations/relocations/diagnostics. Runtime emission не переносится в compiler-only core.

Это область ответственности, не отдельная очередь. Следующий consumer — [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md); validation — [96](96_TEST_STRATEGY.md); применимость evidence — [00](00_CODE_VERIFICATION_REPORT.md). Общая qualification не подтверждена.
