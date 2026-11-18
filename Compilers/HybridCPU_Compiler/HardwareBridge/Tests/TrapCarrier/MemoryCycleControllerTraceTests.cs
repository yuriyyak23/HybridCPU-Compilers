using HybridCPU_ISE.Arch;
using HybridCPU_ISE.CloseToHSL.Memory.MMU;
using HybridCPU_ISE.CloseToHSL.Memory.Timing;
using System.Text.Json;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests.Architecture;

public sealed class MemoryCycleControllerTraceTests
{
    [Fact]
    public void ScalarReadArbitration_ReportsServicePublicationTakeAndCancellation()
    {
        string tracePath = Environment.GetEnvironmentVariable("CW_MEMORY_CYCLE_TRACE")
            ?? throw new InvalidOperationException("CW_MEMORY_CYCLE_TRACE is required.");
        File.WriteAllText(tracePath, string.Empty);
        WithMappedMemory((mainMemory, memory) =>
        {
            const ulong explicitAddress = 0x180;
            const ulong singleLaneAddress = 0x188;
            const ulong explicitValue = 0x1111_2222_3333_4444UL;
            const ulong singleLaneValue = 0x5555_6666_7777_8888UL;
            Assert.True(mainMemory.TryWritePhysicalRange(explicitAddress, BitConverter.GetBytes(explicitValue)));
            Assert.True(mainMemory.TryWritePhysicalRange(singleLaneAddress, BitConverter.GetBytes(singleLaneValue)));

            MemoryAdmissionResult explicitAdmission = memory.CycleController
                .TryAcceptExplicitPacketScalarLoad(0, explicitAddress, 8);
            MemoryAdmissionResult singleLaneAdmission = memory.CycleController
                .TryAcceptSingleLaneScalarLoad(0, singleLaneAddress, 8);
            Assert.Equal(MemoryAdmissionStatus.Accepted, explicitAdmission.Status);
            Assert.Equal(MemoryAdmissionStatus.Accepted, singleLaneAdmission.Status);
            Assert.Equal(1UL, explicitAdmission.RequestId.Value);
            Assert.Equal(2UL, singleLaneAdmission.RequestId.Value);
            Emit("accepted", memory, explicitAdmission.RequestId, singleLaneAdmission.RequestId);

            memory.AdvanceCycles(1);
            bool explicitReadyAtCycle1 = memory.CycleController.TryTakeCompletion(explicitAdmission.RequestId, out _);
            bool singleLaneReadyAtCycle1 = memory.CycleController.TryTakeCompletion(singleLaneAdmission.RequestId, out _);
            Assert.False(explicitReadyAtCycle1);
            Assert.False(singleLaneReadyAtCycle1);
            Emit("after-edge-1", memory, explicitAdmission.RequestId, singleLaneAdmission.RequestId,
                explicitReadyAtCycle1, singleLaneReadyAtCycle1);

            memory.AdvanceCycles(1);
            bool explicitReadyAtCycle2 = memory.CycleController.TryTakeCompletion(
                explicitAdmission.RequestId, out MemoryCompletion? explicitCompletion);
            bool singleLaneReadyAtCycle2 = memory.CycleController.TryTakeCompletion(singleLaneAdmission.RequestId, out _);
            Assert.True(explicitReadyAtCycle2);
            Assert.True(explicitCompletion!.Succeeded);
            Assert.Equal(2UL, explicitCompletion.PublishedCycle);
            Assert.Equal(explicitValue, BitConverter.ToUInt64(explicitCompletion.Data.Span));
            Assert.False(singleLaneReadyAtCycle2);
            Emit("after-edge-2", memory, explicitAdmission.RequestId, singleLaneAdmission.RequestId,
                explicitReadyAtCycle2, singleLaneReadyAtCycle2,
                explicitCompletion.PublishedCycle, BitConverter.ToUInt64(explicitCompletion.Data.Span));

            memory.AdvanceCycles(1);
            bool singleLaneReadyAtCycle3 = memory.CycleController.TryTakeCompletion(
                singleLaneAdmission.RequestId, out MemoryCompletion? singleLaneCompletion);
            Assert.True(singleLaneReadyAtCycle3);
            Assert.True(singleLaneCompletion!.Succeeded);
            Assert.Equal(3UL, singleLaneCompletion.PublishedCycle);
            Assert.Equal(singleLaneValue, BitConverter.ToUInt64(singleLaneCompletion.Data.Span));
            Assert.Equal(0, memory.CycleController.OutstandingExplicitPacketScalarLoads);
            Assert.Equal(0, memory.CycleController.OutstandingSingleLaneScalarLoads);
            Emit("after-edge-3", memory, explicitAdmission.RequestId, singleLaneAdmission.RequestId,
                true, singleLaneReadyAtCycle3, singleLaneCompletion.PublishedCycle,
                BitConverter.ToUInt64(singleLaneCompletion.Data.Span));

            MemoryAdmissionResult canceledAdmission = memory.CycleController
                .TryAcceptSingleLaneScalarLoad(0, 0x198, 8);
            Assert.Equal(MemoryAdmissionStatus.Accepted, canceledAdmission.Status);
            Assert.True(memory.CycleController.TryCancel(canceledAdmission.RequestId));
            Assert.False(memory.CycleController.TryCancel(canceledAdmission.RequestId));
            Emit("canceled-before-service", memory, explicitAdmission.RequestId, singleLaneAdmission.RequestId,
                canceledId: canceledAdmission.RequestId.Value);

            memory.AdvanceCycles(2);
            bool canceledPublished = memory.CycleController.TryTakeCompletion(canceledAdmission.RequestId, out _);
            Assert.False(canceledPublished);
            Assert.Equal(0, memory.CycleController.OutstandingSingleLaneScalarLoads);
            Emit("after-cancel-drain", memory, explicitAdmission.RequestId, singleLaneAdmission.RequestId,
                canceledPublished: canceledPublished, canceledId: canceledAdmission.RequestId.Value);
        });

        void Emit(string scenario, MemorySubsystem memory, MemoryRequestId explicitId, MemoryRequestId singleLaneId,
            bool explicitReady = false, bool singleLaneReady = false, ulong publishedCycle = 0, ulong data = 0,
            ulong canceledId = 0, bool canceledPublished = false)
        {
            File.AppendAllText(tracePath, JsonSerializer.Serialize(new
            {
                scenario,
                memoryCycle = memory.CycleController.MemoryCycle,
                explicitRequestId = explicitId.Value,
                singleLaneRequestId = singleLaneId.Value,
                outstandingExplicitPacketScalarLoads = memory.CycleController.OutstandingExplicitPacketScalarLoads,
                outstandingSingleLaneScalarLoads = memory.CycleController.OutstandingSingleLaneScalarLoads,
                explicitReady,
                singleLaneReady,
                publishedCycle,
                data,
                canceledId,
                canceledPublished
            }) + Environment.NewLine);
        }
    }

    [Fact]
    public void SingleLaneScalarStoreAdmission_ReportsCapacityBackpressureAndCancelRelease()
    {
        string tracePath = Environment.GetEnvironmentVariable("CW_MEMORY_STORE_ADMISSION_TRACE")
            ?? throw new InvalidOperationException("CW_MEMORY_STORE_ADMISSION_TRACE is required.");
        File.WriteAllText(tracePath, string.Empty);
        WithMappedMemory((mainMemory, memory) =>
        {
            const ulong address = 0x300;
            byte[] baseline = Enumerable.Repeat((byte)0xA5, 8).ToArray();
            Assert.True(mainMemory.TryWritePhysicalRange(address, baseline));

            MemoryAdmissionResult invalidSize = memory.CycleController.TryAcceptSingleLaneScalarStore(
                0, address, 3, BitConverter.GetBytes(0x1234UL));
            Assert.Equal(MemoryAdmissionStatus.Rejected, invalidSize.Status);
            Assert.False(invalidSize.RequestId.IsValid);
            Emit("invalid-size-rejected", 0, Array.Empty<MemoryRequestId>(), invalidSize,
                mainMemory.ReadFromPosition(new byte[8], address, 8));

            MemoryAdmissionResult shortData = memory.CycleController.TryAcceptSingleLaneScalarStore(
                0, address, 8, new byte[4]);
            Assert.Equal(MemoryAdmissionStatus.Rejected, shortData.Status);
            Assert.False(shortData.RequestId.IsValid);
            Emit("short-data-rejected", 0, Array.Empty<MemoryRequestId>(), shortData,
                mainMemory.ReadFromPosition(new byte[8], address, 8));

            List<MemoryRequestId> acceptedIds = new();
            MemoryAdmissionResult lastAccepted = default;
            for (int index = 0; index < MemoryCycleController.SingleLaneScalarStoreCapacity; index++)
            {
                lastAccepted = memory.CycleController.TryAcceptSingleLaneScalarStore(
                    0, address + (ulong)(index * 8), 8, BitConverter.GetBytes((ulong)(0x100 + index)));
                Assert.Equal(MemoryAdmissionStatus.Accepted, lastAccepted.Status);
                Assert.True(lastAccepted.RequestId.IsValid);
                acceptedIds.Add(lastAccepted.RequestId);
            }
            Emit("capacity-full", MemoryCycleController.SingleLaneScalarStoreCapacity,
                acceptedIds, lastAccepted, mainMemory.ReadFromPosition(new byte[8], address, 8));

            MemoryAdmissionResult full = memory.CycleController.TryAcceptSingleLaneScalarStore(
                0, address + 0x80, 8, BitConverter.GetBytes(0xCAFEUL));
            Assert.Equal(MemoryAdmissionStatus.Backpressured, full.Status);
            Assert.False(full.RequestId.IsValid);
            Emit("backpressured", MemoryCycleController.SingleLaneScalarStoreCapacity,
                acceptedIds, full, mainMemory.ReadFromPosition(new byte[8], address, 8));

            Assert.True(memory.CycleController.TryCancel(acceptedIds[0]));
            Assert.False(memory.CycleController.TryCancel(acceptedIds[0]));
            MemoryAdmissionResult released = memory.CycleController.TryAcceptSingleLaneScalarStore(
                0, address + 0x80, 8, BitConverter.GetBytes(0xCAFEUL));
            Assert.Equal(MemoryAdmissionStatus.Accepted, released.Status);
            Assert.True(released.RequestId.IsValid);
            Assert.Equal(MemoryCycleController.SingleLaneScalarStoreCapacity,
                memory.CycleController.OutstandingSingleLaneScalarStores);
            Assert.Equal(baseline, mainMemory.ReadFromPosition(new byte[8], address, 8));
            Emit("cancel-released-capacity", memory.CycleController.OutstandingSingleLaneScalarStores,
                acceptedIds, released, mainMemory.ReadFromPosition(new byte[8], address, 8));

            void Emit(string scenario, int outstanding, IReadOnlyList<MemoryRequestId> ids,
                MemoryAdmissionResult result, byte[] observedBytes)
            {
                File.AppendAllText(tracePath, JsonSerializer.Serialize(new
                {
                    scenario,
                    outstandingSingleLaneScalarStores = outstanding,
                    acceptedRequestIds = ids.Select(id => id.Value).ToArray(),
                    admissionStatus = result.Status.ToString(),
                    admissionRequestId = result.RequestId.Value,
                    admissionRequestIdValid = result.RequestId.IsValid,
                    observedBytes = Convert.ToHexString(observedBytes).ToLowerInvariant(),
                }) + Environment.NewLine);
            }
        });
    }

    private static void WithMappedMemory(Action<Processor.MainMemoryArea, MemorySubsystem> body)
    {
        Processor.MainMemoryArea originalMainMemory = Processor.MainMemory;
        MemorySubsystem? originalMemorySubsystem = Processor.Memory;
        try
        {
            Processor.MultiBankMemoryArea mainMemory = new(4, 0x1000UL);
            Processor.MainMemory = mainMemory;
            IOMMU.Initialize();
            IOMMU.RegisterDevice(0);
            Assert.True(IOMMU.Map(0, 0, 0, 0x1000UL, IOMMUAccessPermissions.ReadWrite));
            Processor processor = default;
            MemorySubsystem memory = new(ref processor);
            Processor.Memory = memory;
            body(mainMemory, memory);
        }
        finally
        {
            Processor.MainMemory = originalMainMemory;
            Processor.Memory = originalMemorySubsystem;
        }
    }
}
