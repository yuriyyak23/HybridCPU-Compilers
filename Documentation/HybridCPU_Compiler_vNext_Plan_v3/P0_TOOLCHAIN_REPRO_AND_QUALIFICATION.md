# P0 — Toolchain и воспроизводимость

Owner различает compiler SDK, embedded Roslyn, publish SDK и upstream ILCompiler identities. Текущие pins — [98](98_ROSLYN_DOTNET11_CSHARP15_POLICY.md). Изменение одного не означает upgrade остальных или guest support.

Для заявленного reproducibility relation фиксируются output-affecting inputs/options/references/policies/profile, actual host/TargetPath и изолированные outputs. Code bytes, diagnostics и PE/PDB/maps provenance — разные отношения. Cross-root/thread/cold-warm modes проверяются, когда они реализованы и заявлены, а не как универсальная матрица.

Это область ответственности, не отдельная очередь. Следующий consumer — [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md); validation — [96](96_TEST_STRATEGY.md); применимость evidence — [00](00_CODE_VERIFICATION_REPORT.md). Общая qualification не подтверждена.
