using System;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Compiler-side latency matrix for dependency analysis.
    /// Keeps dependency analyzers consumer-aware without reopening the wider scheduler design.
    /// </summary>
    internal static class HybridCpuDependencyLatencyMatrix
    {
        public static byte ResolveRegisterRawLatency(IrInstruction producer, IrInstruction consumer)
        {
            int baseLatency = Math.Max(1, (int)producer.Annotation.MinimumLatencyCycles);
            int matrixFloor = producer.Annotation.LatencyClass switch
            {
                IrLatencyClass.LoadUse => consumer.Annotation.ResourceClass switch
                {
                    IrResourceClass.ControlFlow => 8,
                    IrResourceClass.System => 8,
                    _ => 8
                },
                IrLatencyClass.Vector => 2,
                IrLatencyClass.Serialized => Math.Max(1, (int)producer.Annotation.MinimumLatencyCycles),
                _ when consumer.Annotation.ControlFlowKind != IrControlFlowKind.None => 4,
                _ => 1
            };

            return SaturateToByte(Math.Max(baseLatency, matrixFloor));
        }

        public static byte ResolveRegisterWarLatency(IrInstruction producer, IrInstruction consumer)
        {
            _ = producer;
            _ = consumer;
            return 0;
        }

        public static byte ResolveRegisterWawLatency(IrInstruction producer, IrInstruction consumer)
        {
            bool touchesLoadStorePath =
                producer.Annotation.ResourceClass == IrResourceClass.LoadStore ||
                consumer.Annotation.ResourceClass == IrResourceClass.LoadStore;

            if (!touchesLoadStorePath)
            {
                return 0;
            }

            int baseLatency = Math.Max(
                1,
                Math.Max((int)producer.Annotation.MinimumLatencyCycles, (int)consumer.Annotation.MinimumLatencyCycles));
            int matrixFloor = producer.Annotation.MemoryReadRegion is not null ? 6 : 4;
            return SaturateToByte(Math.Max(baseLatency, matrixFloor));
        }

        public static byte ResolveMemoryLatency(
            IrInstruction producer,
            IrInstruction consumer,
            IrMemoryDependencyPrecision precision)
        {
            int baseLatency = Math.Max(
                1,
                Math.Max((int)producer.Annotation.MinimumLatencyCycles, (int)consumer.Annotation.MinimumLatencyCycles));

            bool hasWrite = producer.Annotation.MemoryWriteRegion is not null || consumer.Annotation.MemoryWriteRegion is not null;
            int matrixFloor = hasWrite ? 4 : 2;
            if (precision == IrMemoryDependencyPrecision.May)
            {
                matrixFloor = Math.Max(1, matrixFloor - 1);
            }

            if (producer.Annotation.IsBarrierLike || consumer.Annotation.IsBarrierLike)
            {
                matrixFloor = Math.Max(matrixFloor, 2);
            }

            if (consumer.Annotation.ResourceClass is IrResourceClass.ControlFlow or IrResourceClass.System)
            {
                matrixFloor = Math.Max(matrixFloor, 2);
            }

            return SaturateToByte(Math.Max(baseLatency, matrixFloor));
        }

        public static byte ResolveControlLatency(IrInstruction producer, IrInstruction consumer)
        {
            int baseLatency = Math.Max(1, (int)producer.Annotation.MinimumLatencyCycles);
            int matrixFloor = producer.Annotation.ControlFlowKind is IrControlFlowKind.Return or IrControlFlowKind.Stop
                ? 2
                : 1;

            if (producer.Annotation.IsBarrierLike || consumer.Annotation.Serialization != IrSerializationKind.None)
            {
                matrixFloor = Math.Max(matrixFloor, 2);
            }

            return SaturateToByte(Math.Max(baseLatency, matrixFloor));
        }

        public static byte ResolveSerializationLatency(
            IrInstruction producer,
            IrInstruction consumer,
            bool hasPinnedLaneDependency)
        {
            if (hasPinnedLaneDependency &&
                producer.Annotation.Serialization == IrSerializationKind.None &&
                consumer.Annotation.Serialization == IrSerializationKind.None)
            {
                return 1;
            }

            int baseLatency = Math.Max(
                1,
                Math.Max((int)producer.Annotation.MinimumLatencyCycles, (int)consumer.Annotation.MinimumLatencyCycles));
            int matrixFloor = producer.Annotation.IsBarrierLike || consumer.Annotation.IsBarrierLike
                ? 2
                : 1;

            return SaturateToByte(Math.Max(baseLatency, matrixFloor));
        }

        public static byte ResolveLoadAdjacentScalarFollowThroughLatency(IrInstruction producer, IrInstruction consumer)
        {
            int baseLatency = Math.Max(
                1,
                Math.Max((int)producer.Annotation.MinimumLatencyCycles, (int)consumer.Annotation.MinimumLatencyCycles));
            return SaturateToByte(Math.Max(baseLatency, 6));
        }

        public static byte ResolveInterBlockLatency(
            byte minimumLatencyCycles,
            IrControlFlowEdgeKind edgeKind,
            IrInstructionDependencyKind dependencyKind)
        {
            int edgeFloor = edgeKind switch
            {
                IrControlFlowEdgeKind.Branch => 2,
                IrControlFlowEdgeKind.Return => 2,
                IrControlFlowEdgeKind.Stop => 2,
                _ => 1
            };

            if (dependencyKind is IrInstructionDependencyKind.Control or IrInstructionDependencyKind.Serialization)
            {
                edgeFloor++;
            }

            return SaturateToByte(Math.Max((int)minimumLatencyCycles, edgeFloor));
        }

        private static byte SaturateToByte(int latencyCycles)
        {
            return latencyCycles >= byte.MaxValue
                ? byte.MaxValue
                : (byte)Math.Max(0, latencyCycles);
        }
    }
}
