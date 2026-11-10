# HybridCPU native ABI v2

This document is the design authority for `HybridCpuNativeAbiContractV2`. It completes the
previously unsupported native function/stack portion of the immutable Phase 08B v1 platform
contract. Historical v1 evidence remains replayable; Phase 20 and later native-function
lowering must use v2 and pin its digest.

## Architecture bindings

- The architectural namespace is the 32 x 64-bit GPR namespace from
  `HybridCpuTargetMachineContractV1`; x0 remains fixed zero.
- Calls are `JAL rd=x1, displacement`. Returns are `JALR rd=x0, rs1=x1, imm=0`.
  `CALL` and `RET` remain prohibited pseudo-opcodes.
- x2 is the stack pointer. It is an ordinary architectural GPR at execution time; the compiler
  owns only its convention and frame arithmetic, never its runtime value or publication.
- x8 is the frame pointer when a frame needs one. x3 and x4 are reserved for global/thread
  platform state and are not allocator candidates.
- Integer/pointer arguments use x10-x17; scalar and small aggregate results use x10-x11.
  These roles are consistent with the existing x17 ECALL-number contract.
- The caller-saved set is x1, x5-x7, x10-x17 and x28-x31. The callee-saved set is x8-x9 and
  x18-x27. x0, x2, x3 and x4 are fixed/reserved rather than allocator-managed saved values.

## Stack and unsupported contours

The stack grows downward, has 16-byte alignment and no red zone. Fixed slots are addressed
from the adjusted x2 value with non-negative offsets. Prologue and epilogue adjustments are
exact opposites. Dynamic allocation and stack probing are unsupported and fail closed.

Arguments consume one register per eight-byte chunk when the complete value fits in at most
two available argument registers; otherwise the complete value is stack-passed. Results up to
16 bytes use x10-x11; larger results use an indirect-result pointer in x10. Varargs, tail calls
and special-contour calls are unsupported. These are explicit HybridCPU rules, not inherited
host, LLVM or .NET defaults.

All classification and frame layout uses deterministic count/size budgets from the contract.
No wall-clock input participates in a decision.

## Authority boundary

The ABI assigns compiler-visible locations only. It does not allocate physical rename entries,
read a live register file, authorize execution, publish a result, commit memory, or retire an
instruction. Runtime legality and publication remain with the CPU/runtime pipeline.
