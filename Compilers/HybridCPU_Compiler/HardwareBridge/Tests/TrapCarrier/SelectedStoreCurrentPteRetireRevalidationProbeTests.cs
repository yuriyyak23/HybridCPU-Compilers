using System;
using System.IO;
using System.Text.Json;
using HybridCPU_ISE.CloseToHSL.Memory.Timing;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Memory;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests;

public sealed class SelectedStoreCurrentPteRetireRevalidationProbeTests
{
    [Fact]
    public void CurrentLeafPteRemapFaultsBeforeSelectedStorePublication()
    {
        var fixture = CreateFixture();
        const ulong address = 0x8000;
        const ulong leafPteAddress = 0x2018;
        const ulong data = 0x1122334455667788;
        byte[] before = { 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A };
        Assert.True(fixture.Memory.TryWritePhysicalRange(0x5000, before));
        Assert.True(fixture.Memory.TryWritePhysicalRange(0x6000, before));
        fixture.Core.TestPrepareExplicitPacketStoreForWriteBack(4, 0x2000, address, data, 8, 0, domainTag: 7, contextId: 3);

        // Change only the live leaf PTE bytes after MEM captured the selected range.
        Assert.True(fixture.Memory.TryWritePhysicalRange(leafPteAddress, BitConverter.GetBytes(0x6007UL)));
        CpuStoreTranslationRemappedException remap = Assert.Throws<CpuStoreTranslationRemappedException>(
            () => fixture.Core.TestRunWriteBackStage());
        byte[] afterOriginal = ReadPhysical(fixture.Memory, 0x5000);
        byte[] afterNew = ReadPhysical(fixture.Memory, 0x6000);
        Assert.Equal(before, afterOriginal);
        Assert.Equal(before, afterNew);
        Assert.Equal(0UL, fixture.Core.GetPipelineControl().InstructionsRetired);
        Emit(new
        {
            scenario = "selected-store-current-leaf-pte-remap-before-writeback",
            address, leafPteAddress, oldLeafPte = 0x5007UL, newLeafPte = 0x6007UL,
            before = Convert.ToHexString(before), originalAfter = Convert.ToHexString(afterOriginal),
            remappedAfter = Convert.ToHexString(afterNew), exception = remap.GetType().Name,
            retired = fixture.Core.GetPipelineControl().InstructionsRetired,
            mappingMutation = "raw-current-leaf-PTE-byte-replacement; no generation bump or guest fence claimed"
        });
    }

    [Fact]
    public void MissingCurrentLeafPteFaultsBeforeSelectedStorePublication()
    {
        var fixture = CreateFixture();
        const ulong address = 0x8000;
        const ulong leafPteAddress = 0x2018;
        const ulong data = 0x1122334455667788;
        byte[] before = { 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5 };
        Assert.True(fixture.Memory.TryWritePhysicalRange(0x5000, before));
        fixture.Core.TestPrepareExplicitPacketStoreForWriteBack(4, 0x2100, address, data, 8, 0, domainTag: 7, contextId: 3);

        // Clear only the live leaf PTE after MEM captured a valid selected range.
        Assert.True(fixture.Memory.TryWritePhysicalRange(leafPteAddress, BitConverter.GetBytes(0UL)));
        CpuInstructionTranslationFaultException fault = Assert.Throws<CpuInstructionTranslationFaultException>(
            () => fixture.Core.TestRunWriteBackStage());
        byte[] after = ReadPhysical(fixture.Memory, 0x5000);
        Assert.Equal(CpuInstructionTranslationFaultReason.SecondStageViolation, fault.Fault.Reason);
        Assert.Equal(before, after);
        Assert.Equal(0UL, fixture.Core.GetPipelineControl().InstructionsRetired);
        Emit(new
        {
            scenario = "selected-store-current-leaf-pte-missing-before-writeback",
            address, leafPteAddress, oldLeafPte = 0x5007UL, newLeafPte = 0UL,
            before = Convert.ToHexString(before), after = Convert.ToHexString(after),
            exception = fault.GetType().Name, faultReason = fault.Fault.Reason.ToString(),
            retired = fixture.Core.GetPipelineControl().InstructionsRetired,
            mappingMutation = "raw-current-leaf-PTE-byte-clear; no generation bump or guest fence claimed"
        });
    }

    [Fact]
    public void UnchangedCurrentLeafPtePublishesSelectedStoreAndRetiresOnce()
    {
        var fixture = CreateFixture();
        const ulong address = 0x8000;
        const ulong leafPteAddress = 0x2018;
        const ulong data = 0x1122334455667788;
        byte[] before = { 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A };
        byte[] expected = { 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11 };
        Assert.True(fixture.Memory.TryWritePhysicalRange(0x5000, before));
        fixture.Core.TestPrepareExplicitPacketStoreForWriteBack(4, 0x2200, address, data, 8, 0, domainTag: 7, contextId: 3);

        // The PTE remains the same mapping captured when MEM prepared the selected layout.
        byte[] leafBefore = new byte[8];
        Assert.True(fixture.Memory.TryReadPhysicalRange(leafPteAddress, leafBefore));
        Assert.Equal(BitConverter.GetBytes(0x5007UL), leafBefore);
        Assert.Equal(before, ReadPhysical(fixture.Memory, 0x5000));
        fixture.Core.TestRunWriteBackStage();

        byte[] after = ReadPhysical(fixture.Memory, 0x5000);
        Assert.Equal(expected, after);
        Assert.Equal(1UL, fixture.Core.GetPipelineControl().InstructionsRetired);
        Assert.Equal(0UL, fixture.Core.GetPipelineControl().ScalarLanesRetired);
        Assert.Equal(1UL, fixture.Core.GetPipelineControl().NonScalarLanesRetired);
        Emit(new
        {
            scenario = "selected-store-current-leaf-pte-unchanged-success",
            address, leafPteAddress, leafPte = BitConverter.ToUInt64(leafBefore),
            data, before = Convert.ToHexString(before), after = Convert.ToHexString(after),
            retired = fixture.Core.GetPipelineControl().InstructionsRetired,
            scalarRetired = fixture.Core.GetPipelineControl().ScalarLanesRetired,
            nonScalarRetired = fixture.Core.GetPipelineControl().NonScalarLanesRetired,
            exception = (string?)null,
            carrierPath = "TESTING helper-staged explicit packet lane; no fetch/decode architectural admission",
            typedAttemptIdentity = false,
            preparedAttemptTicket = false
        });
    }
    private static (Processor.CPU_Core Core, MemoryDomainRuntime Runtime,
        Processor.MultiBankMemoryArea Memory, MemorySubsystem Subsystem) CreateFixture()
    {
        var memory = new Processor.MultiBankMemoryArea(1, 0x10000);
        Assert.True(memory.TryWritePhysicalRange(0x1000, BitConverter.GetBytes(0x2001UL)));
        Assert.True(memory.TryWritePhysicalRange(0x2018, BitConverter.GetBytes(0x5007UL)));
        var runtime = new MemoryDomainRuntime();
        var descriptor = new MemoryDomainDescriptor(
            new AddressSpaceDescriptor(new AddressSpaceId(7, 9, 0x1000, 1, 1, 1),
                AddressSpaceAuthority.RuntimeOwned, 0x3000, 0x1000, 1, false),
            new MemoryTranslationPolicy(),
            new MemoryDomainTranslationControl(true, true, 0, 0x1000, 7, 9, 1,
                MemoryDomainTranslationControl.WriteBackMemoryType),
            dirtyTracking: null, ownsSecondStageTranslation: true);
        var policy = CpuInstructionTranslationPolicy.CreateTwoStageBoundedRegions(
            new(7, 3, 0), [new(0x8000, 0x3000, 0x1000, true, true, false)],
            runtime, descriptor, 9);
        Processor processor = default;
        var subsystem = MemorySubsystem.CreateForMainMemory(ref processor, memory);
        var core = new Processor.CPU_Core(0, CpuCorePlatformContext.CreateFixed(
            memory, ProcessorMode.Emulation, cpuInstructionTranslationPolicy: policy).WithMemorySubsystem(subsystem));
        return (core, runtime, memory, subsystem);
    }

    private static byte[] ReadPhysical(Processor.MainMemoryArea memory, ulong address)
    {
        byte[] bytes = new byte[8];
        Assert.True(memory.TryReadPhysicalRange(address, bytes));
        return bytes;
    }

    private static void Emit(object observation)
    {
        string? path = Environment.GetEnvironmentVariable("CW_SELECTED_STORE_PTE_TRACE");
        Assert.False(string.IsNullOrWhiteSpace(path));
        File.AppendAllText(path!, JsonSerializer.Serialize(observation) + Environment.NewLine);
    }
}
