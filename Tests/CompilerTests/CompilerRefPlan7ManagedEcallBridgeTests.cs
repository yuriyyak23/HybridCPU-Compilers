using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;
using HybridCPU_ISE.CloseToHSL.Core.Runtime.Managed;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Pipeline;
using YAKSys_Hybrid_CPU.Core.Registers;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7ManagedEcallBridgeTests
{
    [Fact]
    public void RetirementHandler_TransfersReceiverResultAndAdvancesOneBundle()
    {
        DeterministicRuntimeKernelV1 kernel = KernelWithContext(0);
        ulong observedReceiver = 0;
        HybridCpuIseManagedEcallBridgeV1 bridge = new HybridCpuIseManagedEcallBridgeV1(kernel, receiver =>
        {
            observedReceiver = receiver;
            return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, 0x3000_0040);
        });
        FsmCpuState state = Envelope(pc: 0x0010_0000, receiver: 0x3000_0010);
        PipelineFsmEventHandler handler = new PipelineFsmEventHandler(new CsrFile(), ecallBridge: bridge);

        PipelineState result = handler.Handle(Ecall(), PipelineState.Task, state, PrivilegeLevel.User);

        Assert.Equal(PipelineState.Task, result);
        Assert.Equal(0x3000_0010UL, observedReceiver);
        Assert.Equal(0x3000_0040UL, Read(state, HybridCpuExternalServiceEcallContractV1.ResultValueRegister));
        Assert.Equal((ulong)HybridCpuExternalServiceStatusV1.Success,
            Read(state, HybridCpuExternalServiceEcallContractV1.ResultStatusRegister));
        Assert.Equal(0x0010_0100UL, state.ReadPc(0));
    }

    [Fact]
    public void InvalidEnvelope_IsContainedWithoutCallingRuntime()
    {
        int calls = 0;
        HybridCpuIseManagedEcallBridgeV1 bridge = new HybridCpuIseManagedEcallBridgeV1(KernelWithContext(0), _ =>
        {
            calls++;
            return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, 1);
        });
        FsmCpuState state = Envelope(0x1000, 0x3000_0010);
        state.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.ArgumentCountRegister, 2);

        Assert.True(bridge.TryHandle(Ecall(), state, PrivilegeLevel.User));
        Assert.Equal(0, calls);
        Assert.Equal((ulong)HybridCpuExternalServiceStatusV1.InvalidRequest,
            Read(state, HybridCpuExternalServiceEcallContractV1.ResultStatusRegister));
        Assert.Equal(0x1100UL, state.ReadPc(0));
    }

    [Fact]
    public void UnknownServiceAndUnrepresentableNextPc_FallThroughToTrapPath()
    {
        HybridCpuIseManagedEcallBridgeV1 bridge = new HybridCpuIseManagedEcallBridgeV1(KernelWithContext(0), _ =>
            new(HybridCpuManagedShapeStatusV1.Success, string.Empty, 1));
        FsmCpuState unknown = Envelope(0x1000, 1);
        unknown.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.ServiceRegister,
            (ulong)HybridCpuHostServiceV1.Console);
        Assert.False(bridge.TryHandle(Ecall(), unknown, PrivilegeLevel.User));

        FsmCpuState overflow = Envelope(ulong.MaxValue - 255, 1);
        Assert.False(bridge.TryHandle(Ecall(), overflow, PrivilegeLevel.User));
        Assert.Equal(ulong.MaxValue - 255, overflow.ReadPc(0));
    }

    [Fact]
    public void NullReferenceAllocation_RequiresZeroArgumentsAndReturnsRootedObject()
    {
        int calls = 0;
        HybridCpuIseManagedEcallBridgeV1 bridge = new HybridCpuIseManagedEcallBridgeV1(KernelWithContext(0),
            allocateNullReferenceException: () =>
            {
                calls++;
                return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, 0x3000_0080);
            });
        FsmCpuState state = Envelope(0x1000, 0);
        state.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.OperationRegister,
            HybridCpuManagedRuntimeEcallContractV1.AllocateNullReferenceExceptionOperation);
        state.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.ArgumentCountRegister, 0);

        Assert.True(bridge.TryHandle(Ecall(), state, PrivilegeLevel.User));
        Assert.Equal(1, calls);
        Assert.Equal(0x3000_0080UL, Read(state, HybridCpuExternalServiceEcallContractV1.ResultValueRegister));
        Assert.Equal((ulong)HybridCpuExternalServiceStatusV1.Success,
            Read(state, HybridCpuExternalServiceEcallContractV1.ResultStatusRegister));
    }

    [Fact]
    public void NullReferenceAllocation_RejectsMissingObjectAndWrongArgumentCount()
    {
        int calls = 0;
        HybridCpuIseManagedEcallBridgeV1 bridge = new HybridCpuIseManagedEcallBridgeV1(KernelWithContext(0),
            allocateNullReferenceException: () =>
            {
                calls++;
                return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, 0);
            });
        FsmCpuState missing = Envelope(0x1000, 0);
        missing.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.OperationRegister,
            HybridCpuManagedRuntimeEcallContractV1.AllocateNullReferenceExceptionOperation);
        missing.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.ArgumentCountRegister, 0);
        Assert.True(bridge.TryHandle(Ecall(), missing, PrivilegeLevel.User));
        Assert.Equal((ulong)HybridCpuExternalServiceStatusV1.ProviderFailure,
            Read(missing, HybridCpuExternalServiceEcallContractV1.ResultStatusRegister));

        FsmCpuState wrongCount = Envelope(0x2000, 0);
        wrongCount.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.OperationRegister,
            HybridCpuManagedRuntimeEcallContractV1.AllocateNullReferenceExceptionOperation);
        Assert.True(bridge.TryHandle(Ecall(), wrongCount, PrivilegeLevel.User));
        Assert.Equal(1, calls);
        Assert.Equal((ulong)HybridCpuExternalServiceStatusV1.InvalidRequest,
            Read(wrongCount, HybridCpuExternalServiceEcallContractV1.ResultStatusRegister));
    }

    [Fact]
    public void ObjectAllocation_RecordsExactProviderFailureReason()
    {
        const string reason = "Allocation requires an exact runtime-owned class TypeDescriptor handle and digest.";
        HybridCpuIseManagedEcallBridgeV1 bridge = new HybridCpuIseManagedEcallBridgeV1(KernelWithContext(0),
            allocateObject: _ => new(false, 0, reason));
        FsmCpuState state = Envelope(0x1000, 230);
        state.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.OperationRegister,
            HybridCpuManagedRuntimeEcallContractV1.AllocateObjectOperation);

        Assert.True(bridge.TryHandle(Ecall(), state, PrivilegeLevel.User));
        Assert.Equal((ulong)HybridCpuExternalServiceStatusV1.ProviderFailure,
            Read(state, HybridCpuExternalServiceEcallContractV1.ResultStatusRegister));
        Assert.Equal(5UL, Read(state, HybridCpuExternalServiceEcallContractV1.ResultErrorRegister));
        Assert.Equal(reason, bridge.LastManagedReason);
    }

    [Fact]
    public void Int64ArrayLoad_TransfersFullWidthAndContainsInvalidProviders()
    {
        foreach (long scalar in new[] { long.MinValue, -1L, 0, 1, (long)uint.MaxValue + 1, long.MaxValue }) {
            int calls = 0;
            HybridCpuIseManagedEcallBridgeV1 bridge = new HybridCpuIseManagedEcallBridgeV1(KernelWithContext(0), arrayLoadInt64: (array, index) => {
                calls++; Assert.Equal(0x30000010UL, array); Assert.Equal(-1, index);
                return new(true, unchecked((ulong)scalar), string.Empty);
            });
            FsmCpuState state = Request(unchecked((ulong)-1L));
            Assert.True(bridge.TryHandle(Ecall(), state, PrivilegeLevel.User));
            Assert.Equal(1, calls);
            Assert.Equal(unchecked((ulong)scalar), Read(state, HybridCpuExternalServiceEcallContractV1.ResultValueRegister));
            Assert.Equal(0UL, Read(state, HybridCpuExternalServiceEcallContractV1.ResultErrorRegister));
            Assert.Equal((ulong)HybridCpuExternalServiceStatusV1.Success, Read(state, HybridCpuExternalServiceEcallContractV1.ResultStatusRegister));
            FsmCpuState invalid = Request(0x00000000ffffffffUL);
            Assert.True(bridge.TryHandle(Ecall(), invalid, PrivilegeLevel.User));
            Assert.Equal(1, calls);
            Assert.NotEqual((ulong)HybridCpuExternalServiceStatusV1.Success, Read(invalid, HybridCpuExternalServiceEcallContractV1.ResultStatusRegister));
        }
        foreach (int mode in new[] { 0, 1, 2, 3 }) {
            HybridCpuIseManagedEcallBridgeV1 bridge = new HybridCpuIseManagedEcallBridgeV1(KernelWithContext(0), arrayLoadInt64: mode == 0 ? null : (_, _) =>
                mode == 1 ? new(true, 0x30000080, string.Empty, IsManagedThrow: true) :
                mode == 2 ? new(false, 0, "denied") : throw new InvalidOperationException());
            FsmCpuState state = Request(0);
            Assert.True(bridge.TryHandle(Ecall(), state, PrivilegeLevel.User));
            Assert.Equal(mode == 1 ? (ulong)HybridCpuExternalServiceStatusV1.Success : (ulong)HybridCpuExternalServiceStatusV1.ProviderFailure,
                Read(state, HybridCpuExternalServiceEcallContractV1.ResultStatusRegister));
            if (mode == 1) {
                Assert.Equal(0x30000080UL, Read(state, HybridCpuExternalServiceEcallContractV1.ResultValueRegister));
                Assert.Equal(1UL, Read(state, HybridCpuExternalServiceEcallContractV1.ResultErrorRegister));
            }
        }
        static FsmCpuState Request(ulong index) {
            FsmCpuState state = Envelope(0x1000, 0x30000010);
            state.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.OperationRegister, HybridCpuManagedRuntimeEcallContractV1.ArrayLoadInt64Operation);
            state.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.ArgumentCountRegister, 2);
            state.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.SecondArgumentRegister, index);
            return state;
        }
    }

    [Fact]
    public void NullableValue_TransfersFullWidthOrManagedThrowAndRejectsNoncanonicalPresence()
    {
        foreach (long scalar in new[] { long.MinValue, -1L, 0, 1, long.MaxValue }) foreach (bool present in new[] { false, true }) {
            int calls = 0;
            HybridCpuIseManagedEcallBridgeV1 bridge = new HybridCpuIseManagedEcallBridgeV1(KernelWithContext(0), nullableValue: (value, hasValue) => {
                calls++; Assert.Equal(scalar, value); Assert.Equal(present, hasValue);
                return new(true, hasValue ? unchecked((ulong)value) : 0x30000080, string.Empty, IsManagedThrow: !hasValue);
            });
            FsmCpuState state = Request(scalar, present ? 1UL : 0UL);
            Assert.True(bridge.TryHandle(Ecall(), state, PrivilegeLevel.User)); Assert.Equal(1, calls);
            Assert.Equal(present ? unchecked((ulong)scalar) : 0x30000080, Read(state, HybridCpuExternalServiceEcallContractV1.ResultValueRegister));
            Assert.Equal(present ? 0UL : 1UL, Read(state, HybridCpuExternalServiceEcallContractV1.ResultErrorRegister));
            Assert.Equal((ulong)HybridCpuExternalServiceStatusV1.Success, Read(state, HybridCpuExternalServiceEcallContractV1.ResultStatusRegister));
            FsmCpuState invalid = Request(scalar, 2);
            Assert.True(bridge.TryHandle(Ecall(), invalid, PrivilegeLevel.User)); Assert.Equal(1, calls);
            Assert.Equal((ulong)HybridCpuExternalServiceStatusV1.ProviderFailure, Read(invalid, HybridCpuExternalServiceEcallContractV1.ResultStatusRegister));
        }
        foreach (int mode in new[] { 0, 1, 2 }) {
            HybridCpuIseManagedEcallBridgeV1 bridge = new HybridCpuIseManagedEcallBridgeV1(KernelWithContext(0), nullableValue: mode == 0 ? null :
                (_, _) => mode == 1 ? new(false, 0, "denied") : throw new InvalidOperationException());
            FsmCpuState state = Request(0, 0);
            Assert.True(bridge.TryHandle(Ecall(), state, PrivilegeLevel.User));
            Assert.Equal((ulong)HybridCpuExternalServiceStatusV1.ProviderFailure, Read(state, HybridCpuExternalServiceEcallContractV1.ResultStatusRegister));
        }
        static FsmCpuState Request(long scalar, ulong present) {
            FsmCpuState state = Envelope(0x1000, unchecked((ulong)scalar));
            state.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.OperationRegister, HybridCpuManagedRuntimeEcallContractV1.NullableValueInt64CheckedOperation);
            state.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.ArgumentCountRegister, 2);
            state.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.SecondArgumentRegister, present);
            return state;
        }
    }

    private static FsmCpuState Envelope(ulong pc, ulong receiver)
    {
        FsmCpuState state = new FsmCpuState();
        state.WritePc(0, pc);
        state.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.ServiceRegister,
            (ulong)HybridCpuHostServiceV1.ManagedRuntime);
        state.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.OperationRegister,
            HybridCpuManagedRuntimeEcallContractV1.ArgumentExceptionGetMessageOperation);
        state.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.BufferAddressRegister, 0);
        state.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.BufferLengthRegister, 0);
        state.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.BufferAccessRegister,
            (ulong)HybridCpuHostBufferAccessV1.None);
        state.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.ArgumentCountRegister, 1);
        state.WriteRegister(0, HybridCpuExternalServiceEcallContractV1.FirstArgumentRegister, receiver);
        return state;
    }

    private static EcallEvent Ecall() => new()
    {
        VtId = 0, BundleSerial = 1,
        EcallCode = unchecked((long)HybridCpuExternalServiceEcallContractV1.EcallNumber)
    };

    private static ulong Read(FsmCpuState state, int register) =>
        unchecked((ulong)state.ReadRegister(0, register));

    private static DeterministicRuntimeKernelV1 KernelWithContext(int carrier)
    {
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x0010_0000, 4096, 0x0010_0000, 0x0020_0000, 4096, 0)).IsSuccess);
        return kernel;
    }
}
