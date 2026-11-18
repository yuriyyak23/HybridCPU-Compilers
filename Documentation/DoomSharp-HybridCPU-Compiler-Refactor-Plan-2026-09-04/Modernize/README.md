# Modernize — foundation и переход к v3

M0–M6 завершены в пределах scoped acceptance. Продолжение — [v3 README](../../HybridCPU_Compiler_vNext_Plan_v3/README.md), единственная очередь — [v3/95](../../HybridCPU_Compiler_vNext_Plan_v3/95_IMPLEMENTATION_ORDER_AND_COMMITS.md).

[Roadmap](Roadmap.md) содержит mapping и ограничения; [Code review](Code-Review.md) — актуальные архитектурные решения без старой audit очереди. [Validation](../Evidence/Validation.md) определяет scope проверок.

Сохранять existing importer/shared scheduler/bundler/post-bundle/RA/object-link-image и runtime authority boundary. Новый consumer должен доходить до фактического image; evidence и extraction не заменяют функциональность. Qualification: no.
