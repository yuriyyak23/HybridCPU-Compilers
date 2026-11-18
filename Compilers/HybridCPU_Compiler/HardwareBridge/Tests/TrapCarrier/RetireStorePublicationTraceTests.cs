using System;
using System.IO;
using System.Text.Json;
using HybridCPU_ISE.CloseToHSL.Memory.MMU;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests;

public sealed class RetireStorePublicationTraceTests
{
    [Fact]
    public void EmitsLegacyDeferredStoreCommitAndPrevalidationReject()
    {
        IOMMU.Initialize();
        IOMMU.RegisterDevice(0);
        IOMMU.Map(0, 0, 0, 0x100000000UL, IOMMUAccessPermissions.ReadWrite);

        const ulong address = 0x1A00;
        const ulong data = 0x0000000011223344UL;
        byte[] baseline = { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA };
        Processor.MainMemory.WriteToPosition(baseline, address);
        var core = new Processor.CPU_Core(0);
        core.TestRetireLegacyScalarStoreThroughWriteBack(0x2000, address, data, 4, 0);
        byte[] committed = Processor.MainMemory.ReadFromPosition(new byte[8], address, 8);
        Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11, 0xAA, 0xAA, 0xAA, 0xAA }, committed);
        Assert.Equal(1UL, core.GetPipelineControl().InstructionsRetired);
        Assert.Equal(1UL, core.GetPipelineControl().ScalarLanesRetired);
        Emit(new
        {
            scenario = "legacy-deferred-store-success",
            address,
            accessSize = 4,
            data,
            before = Convert.ToHexString(baseline),
            after = Convert.ToHexString(committed),
            retired = core.GetPipelineControl().InstructionsRetired,
            scalarRetired = core.GetPipelineControl().ScalarLanesRetired,
            exception = (string?)null
        });

        const ulong sentinelAddress = 0x1B00;
        byte[] sentinel = { 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A };
        Processor.MainMemory.WriteToPosition(sentinel, sentinelAddress);
        var rejectedCore = new Processor.CPU_Core(0);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => rejectedCore.TestRetireLegacyScalarStoreThroughWriteBack(
                0x2100, ulong.MaxValue - 3, data, 4, 0));
        byte[] afterReject = Processor.MainMemory.ReadFromPosition(new byte[8], sentinelAddress, 8);
        Assert.Equal(sentinel, afterReject);
        Assert.Equal(0UL, rejectedCore.GetPipelineControl().InstructionsRetired);
        Emit(new
        {
            scenario = "legacy-deferred-store-prevalidation-reject",
            address = ulong.MaxValue - 3,
            accessSize = 4,
            before = Convert.ToHexString(sentinel),
            after = Convert.ToHexString(afterReject),
            retired = rejectedCore.GetPipelineControl().InstructionsRetired,
            exception = exception.GetType().Name
        });
    }

    private static void Emit(object observation)
    {
        string? path = Environment.GetEnvironmentVariable("CW_WRITEBACK_FAULT_TRACE");
        Assert.False(string.IsNullOrWhiteSpace(path));
        File.AppendAllText(path!, JsonSerializer.Serialize(observation) + Environment.NewLine);
    }
}
