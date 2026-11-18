using System;
using System.IO;
using System.Text.Json;
using HybridCPU_ISE.CloseToHSL.Memory.Banks;
using HybridCPU_ISE.CloseToHSL.Memory.MMU;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests;

public sealed class SelectedStoreRetireRevalidationProbeTests
{
    [Fact]
    public void RevokedBankDomainCapabilityFaultsBeforeSelectedStorePublication()
    {
        var memory = new MultiBankMemoryArea(4, 0x1000);
        Processor.MainMemory = memory;
        IOMMU.Initialize();
        IOMMU.RegisterDevice(0);
        IOMMU.Map(0, 0, 0, 0x100000000UL, IOMMUAccessPermissions.ReadWrite);

        const ulong address = 0x100;
        const ulong data = 0x11223344;
        byte[] before = { 0x5A, 0x5A, 0x5A, 0x5A };
        memory.WriteToPosition(before, address);
        // DomainTag 2 is admitted by bit 2 (mask 4) while the selected layout is captured.
        memory.SetBankDomainCapability(0, 4);
        var core = new Processor.CPU_Core(0);
        core.TestPrepareExplicitPacketStoreForWriteBack(4, 0x2000, address, data, 4, 0, domainTag: 2);

        // Revoke the capability after MEM preparation and before retire publication.
        memory.SetBankDomainCapability(0, 1);
        PageFaultException fault = Assert.Throws<PageFaultException>(() => core.TestRunWriteBackStage());
        byte[] after = memory.ReadFromPosition(new byte[4], address, 4);
        Assert.True(fault.IsWrite);
        Assert.Equal(address, fault.FaultAddress);
        Assert.Equal(before, after);
        Assert.Equal(0UL, core.GetPipelineControl().InstructionsRetired);

        string? trace = Environment.GetEnvironmentVariable("CW_SELECTED_STORE_TRACE");
        Assert.False(string.IsNullOrWhiteSpace(trace));
        File.AppendAllText(trace!, JsonSerializer.Serialize(new
        {
            scenario = "selected-store-domain-revoked-before-writeback",
            lane = 4, address, accessSize = 4, domainTag = 2,
            capabilityDuringPrepare = 4, capabilityDuringWriteBack = 1,
            before = Convert.ToHexString(before), after = Convert.ToHexString(after),
            faultAddress = fault.FaultAddress, isWrite = fault.IsWrite,
            retired = core.GetPipelineControl().InstructionsRetired
        }) + Environment.NewLine);
    }
}
