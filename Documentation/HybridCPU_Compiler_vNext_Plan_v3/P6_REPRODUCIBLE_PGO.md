# P6 — Explicit profile lane

Core содержит explicit opt-in profile compilation и bounded selection trace. Default-off и статическая допустимость кандидатов сохраняются. Observational metrics/resource/topology paths не становятся скрытым selector.

Profile identity и deterministic output доказывают воспроизводимость в заявленном scope, а не profitability. Promotion требует off-baseline, stale/corrupt/version controls, separation training/evaluation и измерения compile overhead плюс output/runtime benefit. Объём measurement определяется claim — [97](97_PERFORMANCE_AND_RESEARCH_EVALUATION.md).

Это область ответственности, не отдельная очередь. Следующий consumer — [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md); validation — [96](96_TEST_STRATEGY.md); применимость evidence — [00](00_CODE_VERIFICATION_REPORT.md). Общая qualification не подтверждена.
