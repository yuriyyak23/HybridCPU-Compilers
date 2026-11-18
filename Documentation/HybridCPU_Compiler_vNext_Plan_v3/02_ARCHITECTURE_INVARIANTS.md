# 02 — Architecture invariants

Qualification: no.

HybridCPU-v2 — fixed W=8 typed-slot VLIW, 4-way SMT: lanes0–3 ALU,4–5 LSU,6 shared DMA/MatrixTile transport,7 branch/system. Architectural integer state —32×64-bit registers на VT. Physical registers/rename/commit — внутренний backend. MTILE_LOAD/STORE не scalar LSU; shared lane6 capacity и runtime authority сохраняются.

Compiler owns immutable source/import facts, semantic/effect intent, static dependency/resource facts, deterministic selection policy, lowering/provenance и output identities. OS/provider/runtime сохраняют live capability/Region owner/resource-budget truth, admission, legality, execution, commit, retire, publication.

Не приравнивать:
- semantic fact к OS authority;
- static admissibility к Stage A/B live legality;
- typed-slot metadata/descriptor к permission или transfer;
- profile pressure к capability;
- NoFallbackProof к runtime permission;
- secure requested evidence к grant;
- virtual intent к authoritative VMX state;
- completion к retire/publication.

Evidence — ссылки, scalar summaries и digests existing contracts; не public CLR object graph. Live runtime tokens не кешируются как reusable authorization.

Determinism определяется declared inputs/options/versions/contracts/profile и конкретным output scope. Stable virtual document paths и explicitly owned source map inputs могут участвовать в identity; machine absolute paths/time/observational counters не становятся selectors. Debug/provenance equality не подменяет code equality.

Готовность parse, canonical IR, artifact, actual runtime и publication подтверждается раздельно. Единой оценки готовности без соответствующего evidence нет.

Приоритет: reuse → bounded normalize/extract → новый subsystem только при concrete consumer/correctness need. ISA, CPU/VMX/runtime/game semantics не меняются в рамках compiler modernization.
