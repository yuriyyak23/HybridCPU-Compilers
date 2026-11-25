# P9 — Release и performance claims

Existing ReleaseQualification проверяет manifest/matrix/identity/artifact consistency. Clean-build flags — producer declarations; actual commands/isolated roots/exits/input identities нужны отдельно. [Ownership contract](../../../Compilers/HybridCPU_Compiler/ReleaseQualification/README.md).

Promotion выбирает конкретный contour и claim: reproducible publish требует independent builds, execution — runtime reachability/completion, publication — OS/provider evidence. Равные hashes, frozen stability и component smoke этого не заменяют. Не требуется универсальная многодневная нагрузка для compiler-only patch.

Research performance package — отдельный scope с corpus/train/evaluation identities, raw results, machine configuration и variance. Внутренний witness или шумный microbenchmark не даёт численного release performance claim.

Это область ответственности, не отдельная очередь. Следующий consumer — [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md); validation — [96](96_TEST_STRATEGY.md); применимость evidence — [00](00_CODE_VERIFICATION_REPORT.md). Общая qualification не подтверждена.
