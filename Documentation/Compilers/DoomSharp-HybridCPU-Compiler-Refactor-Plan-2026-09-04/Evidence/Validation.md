# Validation — применимость

Основной owner правил новых modernization итераций — [v3/96](../../HybridCPU_Compiler_vNext_Plan_v3/96_TEST_STRATEGY.md). Здесь только predecessor execution/publication ограничения, без второй test queue. Qualification: no.

Compiler-only изменения требуют actual consumer, before/after и targeted/shared harness по затронутому contract. Visual Studio tests дополняются CLI smoke, independent scalar/physical/encoded-byte oracles и bounded diagnostic/loader workloads при необходимости. Proof workload допустим, если проверяет свойство реализации; campaign не заменяет код.

Loader/runtime/GC/EH изменение проверяется по actual positive/negative/loader path. Compiler artifact success не закрывает guest failure без нового artifact и нужного callsite outcome. Historical stability не completion/current-source acceptance.

Final publish claim требует existing mandatory workstream/pack/ABI/runtime outcomes и два clean publish в разных новых roots со сравнением bytes/SHA/size/manifests. Синтетические parser fixtures не release evidence; actual build/run provenance нельзя заменить declaration flags.

Новые outputs/logs/cache/TEMP/CLI/restore и project obj/bin — в новом изолированном каталоге итерации. Старый TempEnv является историческим контекстом. Проверять executable/start/command и competing graphs/guest, чужие процессы не прерывать и не дублировать; timeout продолжает тот же процесс. Source/docs — apply_patch, user changes/frozen reports сохранять.

Для Doom evidence run требуются проверенный независимый launcher contract и exact admitted image/runner. Исторический `Tools/HybridCpuBootBlobEvidence/IndependentRun.md` не входит в текущий checkout. Наличие нового launcher и цепочки допуска следует установить до execution claim. Per-segment pipeline budget Int32.MaxValue не менять без соответствующей задачи. Rejected artifact остаётся unqualified.

Pack/двойной publish/frame/menu/multi-day run/full proof matrix не prerequisites обычного compiler patch. Текущий scope/queue — [v3/95](../../HybridCPU_Compiler_vNext_Plan_v3/95_IMPLEMENTATION_ORDER_AND_COMMITS.md); historical baseline — [Baseline](Baseline.md).
