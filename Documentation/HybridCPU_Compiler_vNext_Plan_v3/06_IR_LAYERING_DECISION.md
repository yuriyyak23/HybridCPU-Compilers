# 06 — Existing IR layering decision

Сохраняется rich IrProgram semantic model, не вводятся HIR/MIR/LIR и массовые renames.

Различать existing responsibilities: semantic intent/effects → dependency/resource facts → schedule/placement witness → materialized bundles/carriers → lowering/relocation → structural evidence. Статические architectural types могут использоваться Core; mutable runtime implementation и authority-bearing state остаются у runtime owner.

Новые DTO/adapter допустимы для конкретного dependency leak/consumer. CompilerProductionProviderContracts и scalar proof summary уже отделены от concrete runtime carriers; не повторять extraction. Intentional Compile Remove/Include groups проверять по actual compiling owner новой зависимости, не создавать отдельный full IR taxonomy проект.

Physical lane/opcode в IR допустим, когда это именно stable architecture contract; source-level APIs не экспонируют reserved carrier fields как source execution permission. Internal CLR graph не public wire ABI.

Для каждого extraction: одна responsibility, API/namespace и semantics сохраняются, targeted Release + old/new bytes/annotations/relocations/diagnostics на bounded corpus. Никаких mandatory layer-ledger, plugin universe или second artifact hierarchy без потребителя.
