using System.Text.Json;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Memory;
using YAKSys_Hybrid_CPU.Core.Pipeline;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests.VmxRefactoring;

public sealed class TranslationFaultRuntimeProbeTests
{
    [Fact]
    public void CaptureProductionFetchTranslationFault()
    {
        string output = Environment.GetEnvironmentVariable("CW_TRANSLATION_FAULT_RUNTIME_TRACE_OUTPUT")
            ?? throw new InvalidOperationException("CW_TRANSLATION_FAULT_RUNTIME_TRACE_OUTPUT is required.");
        var memory = new Processor.MultiBankMemoryArea(1, 0x4000);
        memory.SetLength(0x4000);
        var scope = new CpuInstructionTranslationScope(7, 3, 0);
        var policy = CpuInstructionTranslationPolicy.CreateBoundedRegions(scope,
            [new CpuInstructionTranslationRegion(0x1000, 0, 0x400,
                Readable: true, Writable: true, Executable: true)]);
        var context = CpuCorePlatformContext.CreateFixed(memory, ProcessorMode.Emulation,
            cpuInstructionTranslationPolicy: policy);
        var core = new Processor.CPU_Core(0, context);
        core.PrepareExecutionStart(0, activeVtId: 0);
        CpuInstructionTranslationFaultException exception =
            Assert.Throws<CpuInstructionTranslationFaultException>(core.ExecutePipelineCycle);
        CpuInstructionTranslationFault fault = exception.Fault;
        CpuInstructionTranslationRequest request = fault.Request;
        CpuInstructionTranslationScope faultScope = request.Scope;
        CpuInstructionTranslationOperationIdentity operation = request.OperationIdentity;
        ulong? failingAddress = null;
        string? failingAddressException = null;
        var failingAddressProperty = fault.GetType().GetProperty("FailingVirtualAddress");
        if (failingAddressProperty is null)
            failingAddressException = "PropertyNotPresentInRevision";
        else
        {
            try { failingAddress = Convert.ToUInt64(failingAddressProperty.GetValue(fault)); }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is OverflowException)
            { failingAddressException = ex.InnerException.GetType().Name; }
        }
        object? Optional(string name) => fault.GetType().GetProperty(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic)?.GetValue(fault);
        ulong? requestStartGpa = Optional("RequestStartGuestPhysicalAddress") is object value
            ? Convert.ToUInt64(value) : null;
        var observation = new
        {
            schema = "circuitweave.processor-cpu-translation-fault-runtime/v1",
            scenario = "production-fetch-unmapped-address",
            exceptionType = exception.GetType().FullName,
            isValid = fault.IsValid,
            failingVirtualAddress = failingAddress,
            failingVirtualAddressException = failingAddressException,
            fault = new
            {
                reason = (byte)fault.Reason,
                request = new
                {
                    scope = new
                    {
                        domainId = faultScope.DomainId,
                        contextId = faultScope.ContextId,
                        virtualThreadId = faultScope.VirtualThreadId,
                    },
                    accessKind = (byte)request.AccessKind,
                    virtualAddress = request.VirtualAddress,
                    accessSize = request.AccessSize,
                    operationIdentity = new
                    {
                        attemptId = operation.AttemptId,
                        eventId = operation.EventId,
                        memoryOperationId = operation.MemoryOperationId,
                    },
                },
                ownerEpoch = fault.OwnerEpoch,
                qualification = fault.Qualification,
                auxiliary = fault.Auxiliary,
                faultStage = (byte)fault.FaultStage,
                hasGuestPhysicalAddress = fault.HasGuestPhysicalAddress,
                guestPhysicalAddress = fault.GuestPhysicalAddress,
                memoryDomainOwnerEpoch = fault.MemoryDomainOwnerEpoch,
                addressSpaceGeneration = fault.AddressSpaceGeneration,
                addressSpaceIdentity = fault.AddressSpaceIdentity,
                optionalActiveFields = new
                {
                    failureOffset = Optional("FailureOffset"),
                    translationGeneration = Optional("TranslationGeneration"),
                    hasRequestStartGuestPhysicalAddress = requestStartGpa.HasValue,
                    requestStartGuestPhysicalAddress = requestStartGpa,
                },
            },
        };
        File.WriteAllText(output, JsonSerializer.Serialize(observation,
            new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
    }

    [Fact]
    public void CaptureProductionScalarMemoryTranslationFaults()
    {
        string output = (Environment.GetEnvironmentVariable("CW_TRANSLATION_FAULT_RUNTIME_TRACE_OUTPUT")
            ?? throw new InvalidOperationException("CW_TRANSLATION_FAULT_RUNTIME_TRACE_OUTPUT is required.")) + ".scalar-memory.json";
        var memory = new Processor.MultiBankMemoryArea(1, 0x4000);
        memory.SetLength(0x4000);
        var scope = new CpuInstructionTranslationScope(7, 3, 0);
        var policy = CpuInstructionTranslationPolicy.CreateBoundedRegions(scope,
            [new CpuInstructionTranslationRegion(0x2000, 0x200, 0x1000,
                Readable: true, Writable: false, Executable: false)]);
        var core = new Processor.CPU_Core(0, CpuCorePlatformContext.CreateFixed(memory,
            ProcessorMode.Emulation, cpuInstructionTranslationPolicy: policy));

        LoadMicroOp load = new()
        {
            Address = 0x4000, Size = 8, OpCode = (uint)Processor.CPU_Core.InstructionsEnum.LD,
            DestRegID = 9, BaseRegID = 1, WritesRegister = true,
            OwnerContextId = scope.ContextId, OwnerThreadId = scope.VirtualThreadId,
            VirtualThreadId = scope.VirtualThreadId,
            Placement = new SlotPlacementMetadata { RequiredSlotClass = SlotClass.LsuClass,
                PinningKind = SlotPinningKind.ClassFlexible, DomainTag = scope.DomainId },
        };
        load.InitializeMetadata();
        StoreMicroOp store = new()
        {
            Address = 0x2080, Size = 8, OpCode = (uint)Processor.CPU_Core.InstructionsEnum.SD,
            OwnerContextId = scope.ContextId, OwnerThreadId = scope.VirtualThreadId,
            VirtualThreadId = scope.VirtualThreadId,
            Placement = new SlotPlacementMetadata { RequiredSlotClass = SlotClass.LsuClass,
                PinningKind = SlotPinningKind.ClassFlexible, DomainTag = scope.DomainId },
        };
        store.InitializeMetadata();

        var observations = new[]
        {
            Observe("production-scalar-load-unmapped", Assert.Throws<CpuInstructionTranslationFaultException>(() => load.Execute(ref core))),
            Observe("production-scalar-store-access-denied", Assert.Throws<CpuInstructionTranslationFaultException>(() => store.Execute(ref core))),
        };
        File.WriteAllText(output, JsonSerializer.Serialize(observations,
            new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
    }

    [Fact]
    public void CaptureProductionSecondStageFetchTranslationFault()
    {
        string output = (Environment.GetEnvironmentVariable("CW_TRANSLATION_FAULT_RUNTIME_TRACE_OUTPUT")
            ?? throw new InvalidOperationException("CW_TRANSLATION_FAULT_RUNTIME_TRACE_OUTPUT is required.")) + ".second-stage.json";
        Processor.Memory = null;
        var memory = new Processor.MultiBankMemoryArea(1, 0x8000);
        memory.SetLength(0x8000);
        Assert.True(memory.TryWritePhysicalRange(0x1000, BitConverter.GetBytes(0x2001UL)));
        const ushort domain = 7;
        const ushort addressSpaceId = 9;
        var scope = new CpuInstructionTranslationScope(domain, 3, 0);
        var addressSpace = new AddressSpaceDescriptor(
            new AddressSpaceId(domain, addressSpaceId, 0x1000, 1, 1, 1),
            AddressSpaceAuthority.RuntimeOwned, guestPhysicalBase: 0x3000,
            sizeBytes: 0x1000, generation: 1, compatibilityProjectionEnabled: false);
        var control = new MemoryDomainTranslationControl(TranslationEnabled: true,
            AddressSpaceTaggingEnabled: true, AddressSpaceRoot: 0, SecondStageRoot: 0x1000,
            DomainTag: domain, AddressSpaceTag: addressSpaceId, AddressSpaceGeneration: 1,
            DefaultMemoryType: MemoryDomainTranslationControl.WriteBackMemoryType);
        var descriptor = new MemoryDomainDescriptor(addressSpace, new MemoryTranslationPolicy(), control,
            dirtyTracking: null, ownsSecondStageTranslation: true);
        var runtime = new MemoryDomainRuntime();
        var policy = CpuInstructionTranslationPolicy.CreateTwoStageBoundedRegions(scope,
            [new CpuInstructionStageOneRegion(0x1000, 0x3000, 0x1000,
                Readable: true, Writable: true, Executable: true)], runtime, descriptor, addressSpaceId);
        var core = new Processor.CPU_Core(0, CpuCorePlatformContext.CreateFixed(memory,
            ProcessorMode.Emulation, cpuInstructionTranslationPolicy: policy));
        core.PrepareExecutionStart(0x1000, activeVtId: 0);
        CpuInstructionTranslationFaultException exception =
            Assert.Throws<CpuInstructionTranslationFaultException>(core.ExecutePipelineCycle);
        File.WriteAllText(output, JsonSerializer.Serialize(Observe("production-fetch-second-stage-violation", exception),
            new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
    }

    [Fact]
    public void CaptureProductionSecondStageScalarMemoryTranslationFaults()
    {
        string output = (Environment.GetEnvironmentVariable("CW_TRANSLATION_FAULT_RUNTIME_TRACE_OUTPUT")
            ?? throw new InvalidOperationException("CW_TRANSLATION_FAULT_RUNTIME_TRACE_OUTPUT is required.")) + ".second-stage-scalar-memory.json";
        Processor.Memory = null;
        var memory = new Processor.MultiBankMemoryArea(1, 0x8000);
        memory.SetLength(0x8000);
        Assert.True(memory.TryWritePhysicalRange(0x1000, BitConverter.GetBytes(0x2001UL)));
        const ushort domain = 7;
        const ushort addressSpaceId = 9;
        var scope = new CpuInstructionTranslationScope(domain, 3, 0);
        var addressSpace = new AddressSpaceDescriptor(
            new AddressSpaceId(domain, addressSpaceId, 0x1000, 1, 1, 1),
            AddressSpaceAuthority.RuntimeOwned, guestPhysicalBase: 0x3000,
            sizeBytes: 0x1000, generation: 1, compatibilityProjectionEnabled: false);
        var control = new MemoryDomainTranslationControl(TranslationEnabled: true,
            AddressSpaceTaggingEnabled: true, AddressSpaceRoot: 0, SecondStageRoot: 0x1000,
            DomainTag: domain, AddressSpaceTag: addressSpaceId, AddressSpaceGeneration: 1,
            DefaultMemoryType: MemoryDomainTranslationControl.WriteBackMemoryType);
        var descriptor = new MemoryDomainDescriptor(addressSpace, new MemoryTranslationPolicy(), control,
            dirtyTracking: null, ownsSecondStageTranslation: true);
        var runtime = new MemoryDomainRuntime();
        var policy = CpuInstructionTranslationPolicy.CreateTwoStageBoundedRegions(scope,
            [new CpuInstructionStageOneRegion(0x1000, 0x3000, 0x1000,
                Readable: true, Writable: true, Executable: true)], runtime, descriptor, addressSpaceId);
        var core = new Processor.CPU_Core(0, CpuCorePlatformContext.CreateFixed(memory,
            ProcessorMode.Emulation, cpuInstructionTranslationPolicy: policy));

        LoadMicroOp load = new() { Address = 0x1000, Size = 8,
            OpCode = (uint)Processor.CPU_Core.InstructionsEnum.LD, DestRegID = 9, BaseRegID = 1,
            WritesRegister = true, OwnerContextId = scope.ContextId, OwnerThreadId = scope.VirtualThreadId,
            VirtualThreadId = scope.VirtualThreadId,
            Placement = new SlotPlacementMetadata { RequiredSlotClass = SlotClass.LsuClass,
                PinningKind = SlotPinningKind.ClassFlexible, DomainTag = domain } };
        load.InitializeMetadata();
        StoreMicroOp store = new() { Address = 0x1000, Size = 8, Value = 0x1122334455667788UL,
            OpCode = (uint)Processor.CPU_Core.InstructionsEnum.SD, BaseRegID = 1,
            OwnerContextId = scope.ContextId, OwnerThreadId = scope.VirtualThreadId,
            VirtualThreadId = scope.VirtualThreadId,
            Placement = new SlotPlacementMetadata { RequiredSlotClass = SlotClass.LsuClass,
                PinningKind = SlotPinningKind.ClassFlexible, DomainTag = domain } };
        store.InitializeMetadata();

        object[] observations =
        [
            Observe("production-second-stage-scalar-load-violation", Assert.Throws<CpuInstructionTranslationFaultException>(() => load.Execute(ref core))),
            Observe("production-second-stage-scalar-store-violation", Assert.Throws<CpuInstructionTranslationFaultException>(() => store.Execute(ref core))),
        ];
        File.WriteAllText(output, JsonSerializer.Serialize(observations,
            new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
    }

    [Fact]
    public async Task CaptureProductionSecondStageRebindRaceAndStaleRetry()
    {
        string output = (Environment.GetEnvironmentVariable("CW_TRANSLATION_FAULT_RUNTIME_TRACE_OUTPUT")
            ?? throw new InvalidOperationException("CW_TRANSLATION_FAULT_RUNTIME_TRACE_OUTPUT is required.")) + ".second-stage-rebind-race.json";
        Processor.Memory = null;
        var memory = new BlockingSecondStageMemory();
        Assert.True(memory.TryWritePhysicalRange(0x1000, BitConverter.GetBytes(0x2001UL)));
        Assert.True(memory.TryWritePhysicalRange(0x2018, BitConverter.GetBytes(0x500FUL)));
        const ushort domain = 7;
        const ushort addressSpaceId = 9;
        var scope = new CpuInstructionTranslationScope(domain, 3, 0);
        var addressSpace = new AddressSpaceDescriptor(new AddressSpaceId(domain, addressSpaceId, 0x1000, 1, 1, 1),
            AddressSpaceAuthority.RuntimeOwned, guestPhysicalBase: 0x3000, sizeBytes: 0x1000,
            generation: 1, compatibilityProjectionEnabled: false);
        var control = new MemoryDomainTranslationControl(TranslationEnabled: true,
            AddressSpaceTaggingEnabled: true, AddressSpaceRoot: 0, SecondStageRoot: 0x1000,
            DomainTag: domain, AddressSpaceTag: addressSpaceId, AddressSpaceGeneration: 1,
            DefaultMemoryType: MemoryDomainTranslationControl.WriteBackMemoryType);
        var descriptor = new MemoryDomainDescriptor(addressSpace, new MemoryTranslationPolicy(), control,
            dirtyTracking: null, ownsSecondStageTranslation: true);
        var runtime = new MemoryDomainRuntime();
        var policy = CpuInstructionTranslationPolicy.CreateTwoStageBoundedRegions(scope,
            [new CpuInstructionStageOneRegion(0x1000, 0x3000, 0x1000, true, true, true)],
            runtime, descriptor, addressSpaceId);
        var core = new Processor.CPU_Core(0, CpuCorePlatformContext.CreateFixed(memory,
            ProcessorMode.Emulation, cpuInstructionTranslationPolicy: policy));
        core.PrepareExecutionStart(0x1000, activeVtId: 0);
        memory.Arm();
        Task<Exception?> execution = Task.Run(() => Record.Exception(core.ExecutePipelineCycle));
        Assert.True(memory.WalkEntered.Wait(TimeSpan.FromSeconds(5)));
        ulong originalGeneration = runtime.CurrentAddressSpaceGeneration;
        Exception? inflightRebindException = Record.Exception(() =>
            runtime.RebindAuthoritativeTranslationViewAfterRestore(Descriptor(), addressSpaceId));
        ulong generationAfterInflightRebind = runtime.CurrentAddressSpaceGeneration;
        memory.ReleaseWalk.Set();
        Exception? firstCycleException = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Exception? postCycleRebindException = null;
        bool? postCycleRebindSucceeded = null;
        if (inflightRebindException is not null)
        {
            postCycleRebindException = Record.Exception(() =>
            {
                MemoryDomainSourceBindResult result =
                    runtime.RebindAuthoritativeTranslationViewAfterRestore(Descriptor(), addressSpaceId);
                postCycleRebindSucceeded = result.IsBound;
            });
        }
        Exception? retryException = Record.Exception(core.ExecutePipelineCycle);
        CpuInstructionTranslationFault? retryFault = retryException switch
        {
            CpuInstructionTranslationSourceStaleException stale => stale.Fault,
            CpuInstructionTranslationFaultException fault => fault.Fault,
            _ => null,
        };
        var observation = new
        {
            schema = "circuitweave.processor-second-stage-rebind-race-runtime/v1",
            scenario = "inflight-rebind-attempt-then-postcycle-stale-retry",
            generationBeforeInflightRebind = originalGeneration,
            generationAfterInflightRebind,
            inflightRebindExceptionType = inflightRebindException?.GetType().FullName,
            inflightRebindSucceeded = inflightRebindException is null,
            generationUnchangedDuringWalk = generationAfterInflightRebind == originalGeneration,
            firstCycleExceptionType = firstCycleException?.GetType().FullName,
            firstCycleCompletedWithoutException = firstCycleException is null,
            postCycleRebindAttempted = inflightRebindException is not null,
            postCycleRebindSucceeded,
            postCycleRebindExceptionType = postCycleRebindException?.GetType().FullName,
            retryExceptionType = retryException?.GetType().FullName,
            retryFault = retryFault is null ? null : ObserveFault(retryFault.Value),
        };
        File.WriteAllText(output, JsonSerializer.Serialize(observation,
            new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
    }

    private static object Observe(string scenario, CpuInstructionTranslationFaultException exception)
    {
        return ObserveFault(exception.Fault, scenario, exception.GetType().FullName);
    }

    private static object ObserveFault(CpuInstructionTranslationFault fault, string? scenario = null, string? exceptionType = null)
    {
        var request = fault.Request;
        object? Optional(string name) => fault.GetType().GetProperty(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic)?.GetValue(fault);
        return new
        {
            schema = "circuitweave.processor-cpu-translation-fault-runtime/v1",
            scenario,
            exceptionType,
            isValid = fault.IsValid,
            fault = new
            {
                reason = (byte)fault.Reason,
                request = new { scope = new { domainId = request.Scope.DomainId,
                    contextId = request.Scope.ContextId, virtualThreadId = request.Scope.VirtualThreadId },
                    accessKind = (byte)request.AccessKind, virtualAddress = request.VirtualAddress,
                    accessSize = request.AccessSize, operationIdentity = new {
                        attemptId = request.OperationIdentity.AttemptId,
                        eventId = request.OperationIdentity.EventId,
                        memoryOperationId = request.OperationIdentity.MemoryOperationId } },
                ownerEpoch = fault.OwnerEpoch, qualification = fault.Qualification,
                auxiliary = fault.Auxiliary, faultStage = (byte)fault.FaultStage,
                hasGuestPhysicalAddress = fault.HasGuestPhysicalAddress,
                guestPhysicalAddress = fault.GuestPhysicalAddress,
                memoryDomainOwnerEpoch = fault.MemoryDomainOwnerEpoch,
                addressSpaceGeneration = fault.AddressSpaceGeneration,
                addressSpaceIdentity = fault.AddressSpaceIdentity,
                optionalActiveFields = new { failureOffset = Optional("FailureOffset"),
                    translationGeneration = Optional("TranslationGeneration"),
                    hasRequestStartGuestPhysicalAddress = false,
                    requestStartGuestPhysicalAddress = (ulong?)null },
            },
        };
    }

    private static MemoryDomainDescriptor Descriptor(ulong guestPhysicalBase = 0x3000)
    {
        const ushort domain = 7;
        const ushort addressSpaceId = 9;
        var addressSpace = new AddressSpaceDescriptor(new AddressSpaceId(domain, addressSpaceId, 0x1000, 1, 1, 1),
            AddressSpaceAuthority.RuntimeOwned, guestPhysicalBase: guestPhysicalBase, sizeBytes: 0x1000,
            generation: 1, compatibilityProjectionEnabled: false);
        var control = new MemoryDomainTranslationControl(TranslationEnabled: true,
            AddressSpaceTaggingEnabled: true, AddressSpaceRoot: 0, SecondStageRoot: 0x1000,
            DomainTag: domain, AddressSpaceTag: addressSpaceId, AddressSpaceGeneration: 1,
            DefaultMemoryType: MemoryDomainTranslationControl.WriteBackMemoryType);
        return new MemoryDomainDescriptor(addressSpace, new MemoryTranslationPolicy(), control,
            dirtyTracking: null, ownsSecondStageTranslation: true);
    }

    private sealed class BlockingSecondStageMemory : Processor.MultiBankMemoryArea
    {
        private int _armed;
        internal BlockingSecondStageMemory() : base(1, 0x8000) => SetLength(0x8000);
        internal ManualResetEventSlim WalkEntered { get; } = new(false);
        internal ManualResetEventSlim ReleaseWalk { get; } = new(false);
        internal void Arm() => Interlocked.Exchange(ref _armed, 1);
        public override bool TryReadPhysicalRange(ulong physicalAddress, Span<byte> buffer)
        {
            if (physicalAddress == 0x1000 && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                WalkEntered.Set();
                if (!ReleaseWalk.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Timed out waiting for the second-stage source rebind probe.");
            }
            return base.TryReadPhysicalRange(physicalAddress, buffer);
        }
    }
}
