using System.Runtime.CompilerServices;

namespace YAKSys_Hybrid_CPU
{
    public partial struct Processor
    {
        public sealed partial class CPU_Core
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void PublishAssistOwnerSnapshot(
                PodController? pod,
                int localCoreId,
                int ownerVirtualThreadId,
                System.Collections.Generic.IReadOnlyList<Core.DecodedBundleSlotDescriptor> currentBundleSlots)
            {
                if (pod == null || currentBundleSlots == null)
                    return;

                ResolveBundleOwnerScope(
                    currentBundleSlots,
                    out int ownerContextId,
                    out ulong ownerDomainTag);
                if (ownerContextId < 0)
                {
                    pod.InvalidateAssistOwnerSnapshot(localCoreId);
                    return;
                }

                pod.PublishAssistOwnerSnapshot(
                    localCoreId,
                    ownerVirtualThreadId,
                    ownerContextId,
                    ownerDomainTag,
                    _assistRuntimeEpoch);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void ResolveBundleOwnerScope(
                System.Collections.Generic.IReadOnlyList<Core.DecodedBundleSlotDescriptor> currentBundleSlots,
                out int ownerContextId,
                out ulong ownerDomainTag)
            {
                ownerContextId = -1;
                ownerDomainTag = 0;
                if (currentBundleSlots == null)
                    return;

                for (int slotIndex = 0; slotIndex < currentBundleSlots.Count; slotIndex++)
                {
                    Core.MicroOp microOp = currentBundleSlots[slotIndex].MicroOp;
                    if (microOp == null || microOp is Core.NopMicroOp)
                        continue;

                    ownerContextId = microOp.OwnerContextId;
                    ownerDomainTag = microOp.Placement.DomainTag;
                    return;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void ResolveBundleOwnerScope(
                System.Collections.Generic.IReadOnlyList<Core.MicroOp?> bundle,
                out int ownerContextId,
                out ulong ownerDomainTag)
            {
                ownerContextId = -1;
                ownerDomainTag = 0;
                if (bundle == null)
                    return;

                for (int slotIndex = 0; slotIndex < bundle.Count; slotIndex++)
                {
                    Core.MicroOp microOp = bundle[slotIndex];
                    if (microOp == null || microOp is Core.NopMicroOp)
                        continue;

                    ownerContextId = microOp.OwnerContextId;
                    ownerDomainTag = microOp.Placement.DomainTag;
                    return;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void TryNominateInterCoreAssistCandidate(
                PodController? pod,
                int localCoreId,
                int ownerVirtualThreadId,
                System.Collections.Generic.IReadOnlyList<Core.MicroOp?> ownerBundle)
            {
                if (pod?.Scheduler == null || ownerBundle == null)
                    return;

                if (HasForegroundMixedVirtualThreadContour(ownerBundle, ownerVirtualThreadId))
                    return;

                for (int slotIndex = 0; slotIndex < ownerBundle.Count; slotIndex++)
                {
                    Core.MicroOp seed = ownerBundle[slotIndex];
                    if (seed == null ||
                        seed.IsAssist ||
                        !seed.IsMemoryOp ||
                        seed.IsControlFlow)
                    {
                        continue;
                    }

                    int seedVirtualThreadId = seed.VirtualThreadId >= 0 &&
                                              seed.VirtualThreadId < SmtWays
                        ? seed.VirtualThreadId
                        : seed.OwnerThreadId;
                    if (seedVirtualThreadId != ownerVirtualThreadId)
                        continue;

                    if (Core.AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                            seed,
                            localCoreId,
                            pod.PodId,
                            _assistRuntimeEpoch,
                            out Core.AssistInterCoreTransport transport))
                    {
                        pod.Scheduler.NominateInterCoreAssistCandidate(localCoreId, transport);
                        return;
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool HasForegroundMixedVirtualThreadContour(
                System.Collections.Generic.IReadOnlyList<Core.MicroOp?> ownerBundle,
                int ownerVirtualThreadId)
            {
                for (int slotIndex = 0; slotIndex < ownerBundle.Count; slotIndex++)
                {
                    Core.MicroOp microOp = ownerBundle[slotIndex];
                    if (microOp == null || microOp is Core.NopMicroOp)
                        continue;

                    int microOpVirtualThreadId = microOp.VirtualThreadId >= 0 &&
                                                 microOp.VirtualThreadId < SmtWays
                        ? microOp.VirtualThreadId
                        : microOp.OwnerThreadId;
                    if (microOpVirtualThreadId == ownerVirtualThreadId ||
                        !CanVirtualThreadIssueInForeground(microOpVirtualThreadId))
                    {
                        continue;
                    }

                    return true;
                }

                return false;
            }
        }
    }
}
