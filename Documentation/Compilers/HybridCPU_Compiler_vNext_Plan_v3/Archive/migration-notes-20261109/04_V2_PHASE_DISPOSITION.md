# 04 — Phase disposition

Старые P0–P12 не являются implementation order. Ниже semantic mapping, без восстановления historical active-run/next-step claims.

| Тема прежнего плана | Текущий owner | Действие |
|---|---|---|
| toolchain/baseline | P0 | различать compiler SDK, embedded Roslyn, publish SDK и upstream ILCompiler; scoped baseline применить, не начать заново |
| source ownership/emission | P1 | scoped physical cleanup выполнен; новые dependency leaks по consumer |
| backend convergence | P2 | shared stages уже есть; сохранять RA/frontend/target boundaries |
| managed semantics/source maps | P3 | existing ingress hardening/provenance; coverage-driven extensions |
| Roslyn/generator | P4 | existing companion/oracle/generator, optional CSharp15 compatibility |
| policy/cache/cancellation | P5 | existing identities; measured session/cache extensions |
| profile guidance | P6 | existing opt-in modes; profitability отдельная |
| evidence/type safety | P7 | existing summaries; targeted invalid-state/consumer fixes |
| frontends/runtime/release | P8/P9 | per-claim qualification, не blanket final gate |

Состояние code и bounded results определяет [README](../../README.md), не номера фаз.
