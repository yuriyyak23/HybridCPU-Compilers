# P2 — Общие стадии

Существуют Core IR compiler, ProgramBundler и PostBundleCompiler. CIL/NativeAOT используют shared stages, сохраняя свои ABI/EH/GC/metadata/object/link/image owners. Второй pipeline не нужен.

Порядок сохраняет static dependencies, schedule, bundles, optional bounded RA, final witness/agreement, physical lowering/relocation и serialization. Defaults/raw/value-only paths не меняются попутно. Typed slots и MatrixTile lane6/HardPinned остаются target contracts.

Изменение shared contract проверяется physical/serialized oracle, final agreement/relocations и affected full regressions. Scheduler changes дополнительно требуют соответствующих threshold/corpus проверок из96.

Это область ответственности, не отдельная очередь. Следующий consumer — [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md); validation — [96](96_TEST_STRATEGY.md); применимость evidence — [00](00_CODE_VERIFICATION_REPORT.md). Общая qualification не подтверждена.
