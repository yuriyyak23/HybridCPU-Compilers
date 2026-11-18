# 08 — Покрытие remaining scope

Актуальные implemented/open scopes собраны один раз в [93](../../93_ACCEPTANCE_MATRIX.md), code facts — [00](../../00_CODE_VERIFICATION_REPORT.md). Этот файл не содержит второй status ledger.

Работа выбирается по [95](../../95_IMPLEMENTATION_ORDER_AND_COMMITS.md): actual C# consumer → мешающая semantics/metadata/CFG/ABI/backend dependency → bounded functional implementation и source→image acceptance.

Persistent cache/concurrency/daemon, broader LLVM, default-on PGO, generic math/SIMD, host AOT и research evaluation требуют concrete consumer/measurement или отдельной задачи. Не превращать их в prerequisites обычной C# модернизации. [Toolchain policy](../../98_ROSLYN_DOTNET11_CSHARP15_POLICY.md). Qualification: no.
