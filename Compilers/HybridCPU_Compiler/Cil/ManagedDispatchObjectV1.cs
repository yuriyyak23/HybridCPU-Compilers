using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public static class ManagedDispatchObjectV1
{
    public const string ModuleIdentity = "hybridcpu.managed-runtime.dispatch-metadata/v1";

    public static HybridCpuObjectArtifactV1 Emit(ManagedCallGraphCompilationV1 compilation)
    {
        ManagedDispatchCallPlanV1[] plans = (compilation.DispatchCallPlans ?? [])
            .Where(static plan => plan.Kind == RestrictedCilDispatchKindV1.Interface &&
                ManagedDispatchTypeObjectV1.IsImageOwned(plan)).ToArray();
        ManagedVirtualSlotPlanV1[] virtualPlans=(compilation.VirtualSlotPlans??[]).ToArray();
        if (plans.Length == 0 && virtualPlans.Length == 0 || compilation.TypeUniverse is null)
            throw new ArgumentException("Dispatch metadata requires exact call plans and type universe.", nameof(compilation));
        Dictionary<string, ManagedTypeUniverseRowV1> types = compilation.TypeUniverse.Rows
            .Where(static row => row.StableIdentity.Length != 0)
            .ToDictionary(static row => row.StableIdentity, StringComparer.Ordinal);
        Dictionary<string, HybridCpuManagedMethodSymbolBindingV1> methods = new Dictionary<string, HybridCpuManagedMethodSymbolBindingV1>(StringComparer.Ordinal);
        Dictionary<(ulong RuntimeTypeId, ulong InterfaceTypeId, ulong SlotId), HybridCpuManagedInterfaceDispatchEntryV1> interfaces = new Dictionary<(ulong RuntimeTypeId, ulong InterfaceTypeId, ulong SlotId), HybridCpuManagedInterfaceDispatchEntryV1>();
        Dictionary<(ulong RuntimeTypeId, ulong SlotId), HybridCpuManagedVirtualDispatchEntryV1> virtuals = new Dictionary<(ulong RuntimeTypeId, ulong SlotId), HybridCpuManagedVirtualDispatchEntryV1>();
        foreach (ManagedDispatchCallPlanV1 plan in plans)
            foreach (ManagedDispatchCandidatePlanV1 candidate in plan.Candidates)
            {
                if (!types.TryGetValue(candidate.RuntimeTypeIdentity, out ManagedTypeUniverseRowV1? runtimeType))
                    throw new ArgumentException($"Dispatch runtime type '{candidate.RuntimeTypeIdentity}' has no exact TypeId.", nameof(compilation));
                ulong runtimeTypeId = runtimeType.TypeId;
                if (runtimeType.Descriptor is not { } descriptor || descriptor.TypeId != runtimeTypeId ||
                    descriptor.StableIdentity != runtimeType.StableIdentity ||
                    (descriptor.BaseTypeId ?? 0) != runtimeType.BaseTypeId ||
                    descriptor.DescriptorDigest != HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(descriptor) ||
                    !descriptor.InterfaceTypeIds.Contains(plan.InterfaceTypeId))
                    throw new ArgumentException($"Dispatch runtime type '{candidate.RuntimeTypeIdentity}' lacks an exact descriptor implementing interface {plan.InterfaceTypeId}.", nameof(compilation));
                ulong methodId = NonZeroId(candidate.ImplementationIdentity);
                methods[candidate.ImplementationIdentity] = new(methodId, candidate.ImplementationIdentity, candidate.ImplementationIdentity);
                (ulong runtimeTypeId, ulong InterfaceTypeId, ulong SlotId) key = (runtimeTypeId, plan.InterfaceTypeId, plan.SlotId);
                HybridCpuManagedInterfaceDispatchEntryV1 entry = new HybridCpuManagedInterfaceDispatchEntryV1(runtimeTypeId, plan.InterfaceTypeId, plan.SlotId, methodId, 0);
                if (interfaces.TryGetValue(key, out HybridCpuManagedInterfaceDispatchEntryV1? previous) && previous != entry)
                    throw new ArgumentException($"Conflicting interface dispatch implementations for runtime type {runtimeTypeId}, interface {plan.InterfaceTypeId}, slot {plan.SlotId}.", nameof(compilation));
                interfaces[key] = entry;
            }
        foreach(ManagedVirtualSlotPlanV1 plan in virtualPlans)
            foreach(ManagedVirtualSlotTargetV1 target in plan.Targets)
            {
                if(!types.TryGetValue(target.RuntimeTypeIdentity,out ManagedTypeUniverseRowV1? runtimeType)) continue;
                if(runtimeType.Descriptor is not { } descriptor||
                   descriptor.TypeId!=runtimeType.TypeId||descriptor.DescriptorDigest!=HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(descriptor))
                    throw new ArgumentException($"Virtual runtime type '{target.RuntimeTypeIdentity}' lacks an exact descriptor.",nameof(compilation));
                ulong methodId=NonZeroId(target.ImplementationIdentity);
                string symbol=target.ImplementationIdentity switch
                {
                    "System.Exception.get_Message"=>HybridCpuManagedExceptionGetMessageEmitterV1.Symbol,
                    "System.ArgumentException.get_Message"=>"__hybridcpu_managed_argument_exception_get_message",
                    _=>target.ImplementationIdentity
                };
                methods[target.ImplementationIdentity]=new(methodId,target.ImplementationIdentity,symbol);
                (ulong TypeId, ulong SlotId) key =(runtimeType.TypeId,plan.SlotId);
                HybridCpuManagedVirtualDispatchEntryV1 entry =new HybridCpuManagedVirtualDispatchEntryV1(runtimeType.TypeId,plan.SlotId,methodId,0);
                if(virtuals.TryGetValue(key,out HybridCpuManagedVirtualDispatchEntryV1? previous)&&previous!=entry)
                    throw new ArgumentException($"Conflicting virtual dispatch implementations for runtime type {runtimeType.TypeId}, slot {plan.SlotId}.",nameof(compilation));
                virtuals[key]=entry;
            }
        HybridCpuManagedDispatchMetadataArtifactV1 metadata = new HybridCpuManagedDispatchMetadataEmitterV1().Emit(
            methods.Values, virtuals.Values.ToArray(), interfaces.Values.ToArray());
        if (!metadata.IsSuccess || metadata.Section is null)
            throw new InvalidOperationException("Exact dispatch metadata rejected: " + metadata.Reason);
        List<HybridCpuObjectSymbolV1> symbols = new List<HybridCpuObjectSymbolV1>
        {
            new(HybridCpuManagedDispatchMetadataEmitterV1.Symbol, HybridCpuSymbolBinding.Global,
                HybridCpuSymbolVisibility.Hidden, metadata.Section.Name, 0, metadata.Section.VirtualSize, true)
        };
        symbols.AddRange(methods.Values.Select(static row => new HybridCpuObjectSymbolV1(row.SymbolName,
            HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, null, 0, 0, false)));
        return new HybridCpuObjectWriterV1().Write(new([metadata.Section], symbols,
            metadata.Relocations, HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
    }

    private static ulong NonZeroId(string identity)
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(
            "hybridcpu.managed-method-symbol/v1|" + identity)));
        return value == 0 ? 1 : value;
    }
}
