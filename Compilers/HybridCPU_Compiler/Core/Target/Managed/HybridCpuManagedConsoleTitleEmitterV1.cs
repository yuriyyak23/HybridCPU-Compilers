using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;
namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedConsoleTitleEmitterV1
{
    public const string Symbol="__hybridcpu_managed_guest_console_set_title_utf16";
    public const string ModuleIdentity="hybridcpu.managed-runtime.guest-console-title/v1";
    public static byte[] Emit()=>HybridCpuManagedConsoleWriteEmitterV1.EmitFor(HybridCpuConsoleServiceContractV1.SetTitleUtf16Operation);
    public static HybridCpuObjectArtifactV1 EmitObject(){byte[] c=Emit();return new HybridCpuObjectWriterV1().Write(new([new(".text",HybridCpuObjectSectionKind.Code,HybridCpuBundleSerializer.BundleSizeBytes,c,(ulong)c.Length)],[new(Symbol,HybridCpuSymbolBinding.Global,HybridCpuSymbolVisibility.Hidden,".text",0,(ulong)c.Length,true)],[],HybridCpuTargetPlatformContractV1.Default.ContractDigest,HybridCpuManagedAbiFamilyV1.Default.ContractDigest));}
}
