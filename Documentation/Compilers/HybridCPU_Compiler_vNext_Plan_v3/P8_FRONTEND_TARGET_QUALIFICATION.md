# P8 — Frontend и target scopes

Native, CIL, NativeAOT, Roslyn и LLVM имеют разные input/ABI/metadata/host contracts. Shared backend не означает одинаковую coverage. Analyzer/generator — tooling; Oracle — validation consumer, не production selector.

Реализованные contours перечислены в [93](93_ACCEPTANCE_MATRIX.md). Для расширения фиксируются exact inputs/options/target, positive/unsupported/malformed controls, deterministic relation и соответствующий artifact. Cross-frontend comparison объявляет semantic relation; byte equality не универсальна.

LLVM broader CFG/ABI/runtime reachability и shipping host integration требуют своих acceptance. Loader/guest/publication evidence необходимо только при заявлении этих уровней.

Это область ответственности, не отдельная очередь. Следующий consumer — [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md); validation — [96](96_TEST_STRATEGY.md); применимость evidence — [00](00_CODE_VERIFICATION_REPORT.md). Общая qualification не подтверждена.
