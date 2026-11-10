using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase05GcRootBoundaryTests
{
    [Fact]
    public void RetiredSafepoint_DistinguishesActiveNullAndRawScalarRegisterRoots()
    {
        const string method = "DoomSharp.Core.Graphics.RenderEngine.InitTexturesinstance(System.Object):System.Void";
        const int codeStart = 0x5000;
        const int nativeOffset = 0x20;
        HybridCpuManagedTypeSystemBuildV1 typeBuild = new HybridCpuManagedTypeSystemBuilderV1().Build(
            [new("Node", HybridCpuManagedTypeKindV1.Class, null, [], [])]);
        Assert.True(typeBuild.IsSuccess, typeBuild.Reason);
        HybridCpuManagedTypeSystemV1 types = typeBuild.TypeSystem!;
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x0010_0000, 4096, 0x0010_0000, 0x0020_0000, 4096, 0)).IsSuccess);
        HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types,
            HybridCpuManagedHeapOptionsV1.Create(0x4000_0000, 4096, 4096, -3));
        Assert.True(heap.Initialize().IsSuccess);
        ulong handle = types.TypeHandle(types.Descriptors.Single().TypeId)!.Value;
        HybridCpuManagedHeapResultV1 allocation = heap.Allocate(handle);
        Assert.True(allocation.IsSuccess, allocation.Reason);

        HybridCpuGcInfoEncodingResultV1 gcInfo = HybridCpuManagedAbiEncodingV1.EncodeGcInfo(
            [new(nativeOffset, HybridCpuSafepointCategoryV1.CallSite,
                [new("managed-root", HybridCpuGcReferenceKindV1.ObjectReference,
                    HybridCpuGcLocationKindV1.Register, 10, null)])]);
        Assert.Equal(HybridCpuPlatformFactStatus.Supported, gcInfo.Status);
        byte[] codeManager = HybridCpuManagedAbiEncodingV1.EncodeCodeManagerMetadata(
            [new(method, codeStart, 0x100, gcInfo.Digest, null)]);
        HybridCpuManagedAbiFamilyV1 abi = HybridCpuManagedAbiFamilyV1.Default;
        HybridCpuManagedStackMapRegistrationV1 registration = new HybridCpuManagedStackMapRegistrationV1(method, gcInfo.Bytes, codeManager,
            abi.ContractDigest, abi.TargetContractDigest, abi.NativeAbiDigest,
            HybridCpuManagedAbiFamilyV1.RuntimePackRevision);
        HybridCpuManagedNonMovingGcV1 gc = new HybridCpuManagedNonMovingGcV1(types, heap, abi.ContractDigest,
            abi.TargetContractDigest, abi.NativeAbiDigest, HybridCpuManagedAbiFamilyV1.RuntimePackRevision);
        ulong[] registers = new ulong[64];

        registers[10] = allocation.ObjectReference;
        HybridCpuManagedRetiredSafepointResultV1 valid = gc.CollectRetiredSafepoint(
            [registration], codeStart + nativeOffset, registers, 0x2000, static (_, _) => null, []);
        Assert.Equal(HybridCpuManagedRetiredSafepointStatusV1.Collected, valid.Status);
        Assert.Contains(allocation.ObjectReference,
            Assert.IsType<HybridCpuManagedNonMovingGcResultV1>(valid.Collection).ReachableObjects);

        registers[10] = 0;
        HybridCpuManagedRetiredSafepointResultV1 nullRoot = gc.CollectRetiredSafepoint(
            [registration], codeStart + nativeOffset, registers, 0x2000, static (_, _) => null, []);
        Assert.Equal(HybridCpuManagedRetiredSafepointStatusV1.Collected, nullRoot.Status);

        registers[10] = 0x7c;
        HybridCpuManagedRetiredSafepointResultV1 scalar = gc.CollectRetiredSafepoint(
            [registration], codeStart + nativeOffset, registers, 0x2000, static (_, _) => null, []);
        Assert.Equal(HybridCpuManagedRetiredSafepointStatusV1.Rejected, scalar.Status);
        Assert.Contains("address=0x000000000000007c, source=root", scalar.Reason, StringComparison.Ordinal);
        Assert.Contains($"retired-method='{method}'", scalar.Reason, StringComparison.Ordinal);
        Assert.Contains($"image-pc=0x{codeStart + nativeOffset:x}", scalar.Reason, StringComparison.Ordinal);
        Assert.Contains("x10=0x000000000000007c", scalar.Reason, StringComparison.Ordinal);
    }
}
