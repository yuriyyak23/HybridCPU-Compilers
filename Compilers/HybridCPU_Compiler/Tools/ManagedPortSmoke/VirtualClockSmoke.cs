using System.Numerics;
using System.Reflection;
using HybridCPU.ManagedRuntime;
using HybridCPU.RuntimeKernel;
using HybridCPU.Platform.Contracts;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Cil;

internal static class VirtualClockSmoke
{
    public static void Run()
    {
        if(HybridCpuStaticLinkOptionsV1.Production.MaximumInputs<
           ScalarControlFlowV2ProfileContractV1.Default.Budgets.MaximumReachableMethods+256)
            throw new Exception("Static-link input capacity does not cover the managed method budget plus runtime objects.");
        HybridCpuLinkInputV1[] overLinkBudget =Enumerable.Range(0,HybridCpuStaticLinkOptionsV1.Production.MaximumInputs+1)
            .Select(i=>new HybridCpuLinkInputV1("budget-"+i,[])).ToArray();
        if(new HybridCpuStaticLinkerV1().Link(overLinkBudget).Status!=HybridCpuLinkStatusV1.Invalid)
            throw new Exception("Static linker did not reject an input count above its finite production limit.");
        foreach (ulong frequency in new[] { 1UL, 35UL, 1000UL, (ulong)long.MaxValue, ulong.MaxValue })
        {
            DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
            if (!kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
                0x100000, 4096, 0x100000, 0x200000, 4096, 0, frequency)).IsSuccess) throw new Exception("Clock kernel boot failed.");
            HybridCpuManagedFixedRateClockV1 clock = new HybridCpuManagedFixedRateClockV1(kernel, frequency);
            foreach (ulong ticks in new[] { 0UL, 1UL, 28UL, 29UL, 999UL, 1000UL, (ulong)int.MaxValue,
                (ulong)long.MaxValue, ulong.MaxValue })
            {
                kernel.AdvanceMonotonicTime(ticks);
                foreach (int rate in new[] { 1, 35, int.MaxValue })
                {
                    BigInteger expected = (BigInteger)ticks * rate / frequency;
                    if (expected > int.MaxValue)
                    {
                        bool rejected = false;
                        try { clock.ReadTics(rate); } catch (OverflowException) { rejected = true; }
                        if (!rejected) throw new Exception("Virtual clock wrapped instead of rejecting Int32 overflow.");
                    }
                    else if (clock.ReadTics(rate) != (int)expected || clock.ReadTics(rate) != (int)expected)
                        throw new Exception("Virtual clock conversion or repeat-read stability differs from exact arithmetic.");
                }
            }
            kernel.AdvanceMonotonicTime(0);
            if (kernel.MonotonicTicks() != ulong.MaxValue) throw new Exception("Virtual clock accepted backwards kernel time.");
        }
        bool invalidFrequency = false, invalidRate = false;
        try { _ = new HybridCpuManagedFixedRateClockV1(new DeterministicRuntimeKernelV1(), 0); }
        catch (ArgumentOutOfRangeException) { invalidFrequency = true; }
        try { new HybridCpuManagedFixedRateClockV1(new DeterministicRuntimeKernelV1(), 1000).ReadTics(0); }
        catch (ArgumentOutOfRangeException) { invalidRate = true; }
        if (!invalidFrequency || !invalidRate) throw new Exception("Virtual clock requires explicit positive rates.");
        DeterministicMockHostServiceProviderV1 provider = new DeterministicMockHostServiceProviderV1();
        provider.Register(HybridCpuHostServiceV1.Clock, HybridCpuVirtualClockServiceContractV1.ReadTicksOperation,
            new(HybridCpuExternalServiceStatusV1.Success, 9999, 0, "not the virtual kernel clock"));
        DeterministicRuntimeKernelV1 serviceKernel = new DeterministicRuntimeKernelV1(provider);
        serviceKernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x100000, 4096, 0x100000, 0x200000, 4096, 0, 1000));
        serviceKernel.AdvanceMonotonicTime(1234);
        HybridCpuExternalServiceRequestV1 draft = new HybridCpuExternalServiceRequestV1(serviceKernel.CurrentContext()!.ContextId,
            HybridCpuHostServiceV1.Clock, HybridCpuVirtualClockServiceContractV1.ReadTicksOperation,
            HybridCpuPrivilegeModeV1.User, 0, 0, HybridCpuHostBufferAccessV1.None, [], "");
        HybridCpuExternalServiceRequestV1 Sign(HybridCpuExternalServiceRequestV1 request) =>
            request with { TransitionDigest = HybridCpuManagedInteropContractV1.ComputeTransitionDigest(request) };
        HybridCpuExternalServiceResultV1 read = serviceKernel.ExternalServiceTransition(Sign(draft));
        if (!read.IsSuccess || read.ReturnValue != 1234 || provider.Trace().Count != 0)
            throw new Exception("Kernel clock service delegated to an ambient host provider.");
        foreach (HybridCpuExternalServiceRequestV1? invalid in new[] { draft with { ContextId = 0 }, draft with { Arguments = [1UL] },
            draft with { BufferAddress = 0x100000, BufferLength = 8, BufferAccess = HybridCpuHostBufferAccessV1.Read } })
            if (serviceKernel.ExternalServiceTransition(Sign(invalid)).IsSuccess)
                throw new Exception("Clock service accepted a foreign context or nonempty payload.");
        if (serviceKernel.ExternalServiceTransition(draft).IsSuccess)
            throw new Exception("Clock service accepted an unsigned request.");
        HybridCpuExternalServiceEcallV1 envelope = new HybridCpuExternalServiceEcallV1(
            HybridCpuExternalServiceEcallContractV1.EcallNumber,
            (ulong)HybridCpuHostServiceV1.Clock,
            HybridCpuVirtualClockServiceContractV1.ReadTicksOperation,
            0, 0, (ulong)HybridCpuHostBufferAccessV1.None, 0);
        HybridCpuExternalServiceEcallResultV1 ecall = serviceKernel.ExternalServiceEcallTransition(
            serviceKernel.CurrentContext()!.ContextId, HybridCpuPrivilegeModeV1.User, envelope);
        if (ecall.Status != HybridCpuExternalServiceStatusV1.Success || ecall.ReturnValue != 1234 ||
            string.IsNullOrWhiteSpace(ecall.ResultDigest))
            throw new Exception("Trusted ECALL gateway did not sign and dispatch the exact clock request.");
        HybridCpuExternalServiceEcallResultV1 doomEcall = serviceKernel.ExternalServiceEcallTransition(
            serviceKernel.CurrentContext()!.ContextId, HybridCpuPrivilegeModeV1.User,
            envelope with { Operation = HybridCpuVirtualClockServiceContractV1.ReadDoomTicsOperation });
        if (doomEcall.Status != HybridCpuExternalServiceStatusV1.Success || doomEcall.ReturnValue != 43)
            throw new Exception("Kernel did not apply the boot-owned 1000 Hz to exact Doom 35 Hz projection.");
        foreach (HybridCpuExternalServiceEcallV1 invalidEnvelope in new[]
        {
            envelope with { EcallNumber = 0 },
            envelope with { ArgumentCount = 2 },
            envelope with { Service = ulong.MaxValue },
            envelope with { BufferAccess = ulong.MaxValue }
        })
            if (serviceKernel.ExternalServiceEcallTransition(serviceKernel.CurrentContext()!.ContextId,
                    HybridCpuPrivilegeModeV1.User, invalidEnvelope).Status != HybridCpuExternalServiceStatusV1.InvalidRequest)
                throw new Exception("Trusted ECALL gateway accepted a malformed register envelope.");
        if (serviceKernel.ExternalServiceEcallTransition(serviceKernel.CurrentContext()!.ContextId,
                HybridCpuPrivilegeModeV1.Machine, envelope).Status != HybridCpuExternalServiceStatusV1.InvalidRequest ||
            serviceKernel.ExternalServiceEcallTransition(0, HybridCpuPrivilegeModeV1.User, envelope).Status !=
                HybridCpuExternalServiceStatusV1.InvalidRequest)
            throw new Exception("Trusted ECALL gateway accepted machine privilege or a foreign context.");
        DeterministicRuntimeKernelV1 missingTimebase = new DeterministicRuntimeKernelV1();
        if (missingTimebase.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
                0x100000, 4096, 0x100000, 0x200000, 4096, 0, 0)).IsSuccess)
            throw new Exception("Kernel boot accepted an unspecified virtual-clock frequency.");
        byte[] thunk = HybridCpuManagedDoomClockEmitterV1.Emit();
        int ecallCount = Enumerable.Range(0, thunk.Length / HybridCpuBundleSerializer.BundleSizeBytes)
            .Count(index => (BitConverter.ToUInt64(thunk, index * HybridCpuBundleSerializer.BundleSizeBytes) >> 48) ==
                (uint)HybridCpuOpcode.ECALL);
        HybridCpuObjectArtifactV1 thunkObject = HybridCpuManagedDoomClockEmitterV1.EmitObject();
        if (ecallCount != 1 || thunkObject.Status != HybridCpuObjectStatusV1.Success ||
            thunkObject.Symbols is not [{ Name: HybridCpuManagedDoomClockEmitterV1.Symbol, IsDefinition: true }])
            throw new Exception("Doom clock thunk does not contain one exact ECALL or a linkable symbol.");
        MethodInfo guestServiceGate = typeof(ScalarControlFlowV2ObjectLinkerV1).GetMethod(
            "IsGuestServiceTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
        string[] admittedGuestServices =
        [
            HybridCpuManagedDoomClockEmitterV1.Symbol,
            HybridCpuManagedFramebufferEmitterV1.Symbol,
            HybridCpuManagedConsoleWriteEmitterV1.Symbol,
            HybridCpuManagedDoomWaitEmitterV1.Symbol,
            HybridCpuManagedBootBlobEmitterV1.Symbol,
            HybridCpuManagedGuestProcessExitEmitterV1.Symbol,
            HybridCpuManagedFramebufferPresentEmitterV1.Symbol,
            HybridCpuManagedConsoleTitleEmitterV1.Symbol,
            HybridCpuManagedInputPullEmitterV1.Symbol,
            HybridCpuManagedPaletteEmitterV1.Symbol
        ];
        if (admittedGuestServices.Any(symbol => !(bool)guestServiceGate.Invoke(null, [symbol])!) ||
            (bool)guestServiceGate.Invoke(null, ["__hybridcpu_unowned_guest_service"])!)
            throw new Exception("Direct-callee admission must match only guest-service symbols with linker-owned runtime objects.");
        MethodInfo undefinedTargets = typeof(ScalarControlFlowV2ObjectLinkerV1).GetMethod(
            "BuildUndefinedCallTargets", BindingFlags.Static | BindingFlags.NonPublic)!;
        HybridCpuObjectRelocationV1 serviceRelocation = new HybridCpuObjectRelocationV1(".text", 2,
            HybridCpuRelocationKind.ManagedCallRelativeSigned16,
            HybridCpuManagedConsoleWriteEmitterV1.Symbol, 0);
        string[] objectTargets = (string[])undefinedTargets.Invoke(null,
            ["self", new[] { "managed", "self" }, new[] { serviceRelocation }, new HashSet<string>(["thunked"], StringComparer.Ordinal)])!;
        if (!objectTargets.SequenceEqual(new[] { HybridCpuManagedConsoleWriteEmitterV1.Symbol, "managed" }))
            throw new Exception("Every exact relocated guest-service call must have a deterministic undefined HCO symbol.");
        DeterministicMockHostServiceProviderV1 graphicsProvider = new DeterministicMockHostServiceProviderV1();
        graphicsProvider.Register(HybridCpuHostServiceV1.Graphics,
            HybridCpuFramebufferServiceContractV1.InitializeOperation,
            new(HybridCpuExternalServiceStatusV1.Success, 0, 0, string.Empty));
        graphicsProvider.Register(HybridCpuHostServiceV1.Graphics,
            HybridCpuFramebufferServiceContractV1.PresentOperation,
            new(HybridCpuExternalServiceStatusV1.Success, 0, 0, string.Empty));
        graphicsProvider.Register(HybridCpuHostServiceV1.Graphics,
            HybridCpuFramebufferServiceContractV1.UpdatePaletteOperation,
            new(HybridCpuExternalServiceStatusV1.Success, 0, 0, string.Empty));
        DeterministicRuntimeKernelV1 graphicsKernel = new DeterministicRuntimeKernelV1(graphicsProvider);
        graphicsKernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a',64),
            0x100000,131072,0x100000,0x200000,4096,0,1000));
        HybridCpuExternalServiceEcallV1 framebufferEnvelope = envelope with
        {
            Service=(ulong)HybridCpuHostServiceV1.Graphics,
            Operation=HybridCpuFramebufferServiceContractV1.InitializeOperation,
            ArgumentCount=1,
            Argument0=HybridCpuFramebufferServiceContractV1.PackDimensions(320,200)
        };
        if (graphicsKernel.ExternalServiceEcallTransition(graphicsKernel.CurrentContext()!.ContextId,
                HybridCpuPrivilegeModeV1.User,framebufferEnvelope).Status != HybridCpuExternalServiceStatusV1.Success ||
            graphicsProvider.Trace().Count != 1)
            throw new Exception("Exact framebuffer initialization did not reach the checked provider boundary.");
        HybridCpuExternalServiceEcallV1 presentEnvelope =envelope with { Service=(ulong)HybridCpuHostServiceV1.Graphics,
            Operation=HybridCpuFramebufferServiceContractV1.PresentOperation,BufferAddress=0x101000,
            BufferLength=320*200,BufferAccess=(ulong)HybridCpuHostBufferAccessV1.Read };
        if(graphicsKernel.ExternalServiceEcallTransition(graphicsKernel.CurrentContext()!.ContextId,
               HybridCpuPrivilegeModeV1.User,presentEnvelope).Status!=HybridCpuExternalServiceStatusV1.Success||
           graphicsProvider.Trace().Count!=2)
            throw new Exception("Exact initialized framebuffer payload did not reach the provider.");
        if(graphicsKernel.ExternalServiceEcallTransition(graphicsKernel.CurrentContext()!.ContextId,
               HybridCpuPrivilegeModeV1.User,presentEnvelope with { BufferLength=320*200-1 }).Status==
               HybridCpuExternalServiceStatusV1.Success||graphicsProvider.Trace().Count!=2)
            throw new Exception("Framebuffer present accepted a non-width*height payload.");
        byte[] presentThunk=HybridCpuManagedFramebufferPresentEmitterV1.Emit();
        if(Enumerable.Range(0,presentThunk.Length/HybridCpuBundleSerializer.BundleSizeBytes).Count(index=>
               (BitConverter.ToUInt64(presentThunk,index*HybridCpuBundleSerializer.BundleSizeBytes)>>48)==
                   (uint)HybridCpuOpcode.ECALL)!=1||HybridCpuManagedFramebufferPresentEmitterV1.EmitObject().Status!=HybridCpuObjectStatusV1.Success)
            throw new Exception("Framebuffer present thunk lacks one ECALL or valid HCO.");
        HybridCpuExternalServiceEcallV1 paletteEnvelope =presentEnvelope with { Operation=HybridCpuFramebufferServiceContractV1.UpdatePaletteOperation,
            BufferLength=HybridCpuFramebufferServiceContractV1.PaletteBytes };
        if(graphicsKernel.ExternalServiceEcallTransition(graphicsKernel.CurrentContext()!.ContextId,
               HybridCpuPrivilegeModeV1.User,paletteEnvelope).Status!=HybridCpuExternalServiceStatusV1.Success||
           graphicsProvider.Trace().Count!=3)
            throw new Exception("Exact initialized RGB24 palette did not reach the provider.");
        if(graphicsKernel.ExternalServiceEcallTransition(graphicsKernel.CurrentContext()!.ContextId,
               HybridCpuPrivilegeModeV1.User,paletteEnvelope with { BufferLength=767 }).Status==
               HybridCpuExternalServiceStatusV1.Success||graphicsProvider.Trace().Count!=3)
            throw new Exception("Palette update accepted a non-768-byte payload.");
        DeterministicRuntimeKernelV1 uninitializedPaletteKernel =new DeterministicRuntimeKernelV1(graphicsProvider);
        uninitializedPaletteKernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest,new string('a',64),
            0x100000,131072,0x100000,0x200000,4096,0,1000));
        if(uninitializedPaletteKernel.ExternalServiceEcallTransition(uninitializedPaletteKernel.CurrentContext()!.ContextId,
               HybridCpuPrivilegeModeV1.User,paletteEnvelope).Status==HybridCpuExternalServiceStatusV1.Success||
           graphicsProvider.Trace().Count!=3)
            throw new Exception("Palette update was admitted before framebuffer initialization.");
        byte[] paletteThunk=HybridCpuManagedPaletteEmitterV1.Emit();
        if(Enumerable.Range(0,paletteThunk.Length/HybridCpuBundleSerializer.BundleSizeBytes).Count(index=>
               (BitConverter.ToUInt64(paletteThunk,index*HybridCpuBundleSerializer.BundleSizeBytes)>>48)==
                   (uint)HybridCpuOpcode.ECALL)!=1||HybridCpuManagedPaletteEmitterV1.EmitObject().Status!=HybridCpuObjectStatusV1.Success)
            throw new Exception("Palette thunk lacks one ECALL or valid HCO.");
        foreach (ulong invalidDimensions in new[] { 0UL,
                     HybridCpuFramebufferServiceContractV1.PackDimensions(-1,200),
                     HybridCpuFramebufferServiceContractV1.PackDimensions(320,20000) })
            if (graphicsKernel.ExternalServiceEcallTransition(graphicsKernel.CurrentContext()!.ContextId,
                    HybridCpuPrivilegeModeV1.User,framebufferEnvelope with { Argument0=invalidDimensions }).Status !=
                HybridCpuExternalServiceStatusV1.InvalidRequest)
                throw new Exception("Framebuffer service accepted invalid packed dimensions.");
        byte[] framebufferThunk=HybridCpuManagedFramebufferEmitterV1.Emit();
        if (Enumerable.Range(0,framebufferThunk.Length/HybridCpuBundleSerializer.BundleSizeBytes)
                .Count(index=>(BitConverter.ToUInt64(framebufferThunk,index*HybridCpuBundleSerializer.BundleSizeBytes)>>48)==
                    (uint)HybridCpuOpcode.ECALL) != 1 ||
            HybridCpuManagedFramebufferEmitterV1.EmitObject().Status != HybridCpuObjectStatusV1.Success)
            throw new Exception("Framebuffer thunk lacks one exact ECALL or a valid HCO object.");
        DeterministicMockHostServiceProviderV1 consoleProvider =new DeterministicMockHostServiceProviderV1();
        consoleProvider.Register(HybridCpuHostServiceV1.Console,HybridCpuConsoleServiceContractV1.WriteUtf16Operation,
            new(HybridCpuExternalServiceStatusV1.Success,0,0,string.Empty));
        consoleProvider.Register(HybridCpuHostServiceV1.Console,HybridCpuConsoleServiceContractV1.SetTitleUtf16Operation,
            new(HybridCpuExternalServiceStatusV1.Success,0,0,string.Empty));
        DeterministicRuntimeKernelV1 consoleKernel =new DeterministicRuntimeKernelV1(consoleProvider);
        consoleKernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest,new string('a',64),
            0x100000,4096,0x100000,0x200000,4096,0,1000));
        HybridCpuExternalServiceEcallV1 consoleEnvelope =envelope with { Service=(ulong)HybridCpuHostServiceV1.Console,
            Operation=HybridCpuConsoleServiceContractV1.WriteUtf16Operation,BufferAddress=0x100020,
            BufferLength=4,BufferAccess=(ulong)HybridCpuHostBufferAccessV1.Read };
        if(consoleKernel.ExternalServiceEcallTransition(consoleKernel.CurrentContext()!.ContextId,
               HybridCpuPrivilegeModeV1.User,consoleEnvelope).Status!=HybridCpuExternalServiceStatusV1.Success ||
           consoleProvider.Trace().Count!=1)
            throw new Exception("Bounded UTF-16 console buffer did not reach the provider.");
        foreach(HybridCpuExternalServiceEcallV1? invalidConsole in new[]{consoleEnvelope with { BufferLength=3 },
                    consoleEnvelope with { BufferLength=(ulong)HybridCpuConsoleServiceContractV1.MaximumCodeUnits*2+2 },
                    consoleEnvelope with { ArgumentCount=1,Argument0=1 }})
            if(consoleKernel.ExternalServiceEcallTransition(consoleKernel.CurrentContext()!.ContextId,
                   HybridCpuPrivilegeModeV1.User,invalidConsole).Status==HybridCpuExternalServiceStatusV1.Success)
                throw new Exception("Console service accepted an invalid UTF-16 buffer contract.");
        if(consoleProvider.Trace().Count!=1)
            throw new Exception("Invalid console buffer reached the provider.");
        HybridCpuExternalServiceEcallV1 consoleTitleEnvelope =consoleEnvelope with { Operation=HybridCpuConsoleServiceContractV1.SetTitleUtf16Operation };
        if(consoleKernel.ExternalServiceEcallTransition(consoleKernel.CurrentContext()!.ContextId,
               HybridCpuPrivilegeModeV1.User,consoleTitleEnvelope).Status!=HybridCpuExternalServiceStatusV1.Success ||
           consoleProvider.Trace().Count!=2)
            throw new Exception("Bounded UTF-16 console title did not reach the provider.");
        byte[] consoleThunk=HybridCpuManagedConsoleWriteEmitterV1.Emit();
        if(Enumerable.Range(0,consoleThunk.Length/HybridCpuBundleSerializer.BundleSizeBytes)
               .Count(index=>(BitConverter.ToUInt64(consoleThunk,index*HybridCpuBundleSerializer.BundleSizeBytes)>>48)==
                   (uint)HybridCpuOpcode.ECALL)!=1 || HybridCpuManagedConsoleWriteEmitterV1.EmitObject().Status!=HybridCpuObjectStatusV1.Success)
            throw new Exception("Console thunk lacks one exact ECALL or a valid HCO object.");
        byte[] consoleTitleThunk=HybridCpuManagedConsoleTitleEmitterV1.Emit();
        if(Enumerable.Range(0,consoleTitleThunk.Length/HybridCpuBundleSerializer.BundleSizeBytes)
               .Count(index=>(BitConverter.ToUInt64(consoleTitleThunk,index*HybridCpuBundleSerializer.BundleSizeBytes)>>48)==
                   (uint)HybridCpuOpcode.ECALL)!=1 || HybridCpuManagedConsoleTitleEmitterV1.EmitObject().Status!=HybridCpuObjectStatusV1.Success)
            throw new Exception("Console title thunk lacks one exact ECALL or a valid HCO object.");
        InputProvider inputProvider =new InputProvider(
            new(true,0,"registry-v1",string.Empty),new(true,0x100180,"registry-v1",string.Empty));
        DeterministicRuntimeKernelV1 inputKernel =new DeterministicRuntimeKernelV1(managedInputProvider:inputProvider);
        inputKernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest,new string('a',64),
            0x100000,4096,0x100000,0x200000,4096,0,1000));
        HybridCpuExternalServiceEcallV1 inputEnvelope =envelope with { Service=(ulong)HybridCpuHostServiceV1.Input,
            Operation=HybridCpuInputServiceContractV1.PullOperation };
        HybridCpuExternalServiceEcallResultV1 emptyInput =inputKernel.ExternalServiceEcallTransition(inputKernel.CurrentContext()!.ContextId,
            HybridCpuPrivilegeModeV1.User,inputEnvelope);
        HybridCpuExternalServiceEcallResultV1 queuedInput =inputKernel.ExternalServiceEcallTransition(inputKernel.CurrentContext()!.ContextId,
            HybridCpuPrivilegeModeV1.User,inputEnvelope);
        if(emptyInput.Status!=HybridCpuExternalServiceStatusV1.Success||emptyInput.ReturnValue!=0||
           queuedInput.Status!=HybridCpuExternalServiceStatusV1.Success||queuedInput.ReturnValue!=0x100180||
           inputProvider.PullCount!=2)
            throw new Exception("Trusted nullable managed input reference was not transferred exactly.");
        if(inputKernel.ExternalServiceEcallTransition(inputKernel.CurrentContext()!.ContextId,
               HybridCpuPrivilegeModeV1.User,inputEnvelope with { ArgumentCount=1,Argument0=1 }).Status==
           HybridCpuExternalServiceStatusV1.Success||inputProvider.PullCount!=2)
            throw new Exception("Invalid input pull reached the trusted managed registry.");
        DeterministicRuntimeKernelV1 invalidInputKernel =new DeterministicRuntimeKernelV1(managedInputProvider:
            new InputProvider(new HybridCpuManagedInputProviderResultV1(true,0x100180,string.Empty,string.Empty)));
        invalidInputKernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest,new string('a',64),
            0x100000,4096,0x100000,0x200000,4096,0,1000));
        if(invalidInputKernel.ExternalServiceEcallTransition(invalidInputKernel.CurrentContext()!.ContextId,
               HybridCpuPrivilegeModeV1.User,inputEnvelope).Status!=HybridCpuExternalServiceStatusV1.ProviderFailure)
            throw new Exception("Input registry without identity evidence was not rejected fail-closed.");
        byte[] inputThunk=HybridCpuManagedInputPullEmitterV1.Emit();
        if(Enumerable.Range(0,inputThunk.Length/HybridCpuBundleSerializer.BundleSizeBytes)
               .Count(index=>(BitConverter.ToUInt64(inputThunk,index*HybridCpuBundleSerializer.BundleSizeBytes)>>48)==
                   (uint)HybridCpuOpcode.ECALL)!=1||HybridCpuManagedInputPullEmitterV1.EmitObject().Status!=HybridCpuObjectStatusV1.Success)
            throw new Exception("Input pull thunk lacks one exact ECALL or a valid HCO object.");
        DeterministicRuntimeKernelV1 waitKernel =new DeterministicRuntimeKernelV1();
        waitKernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest,new string('a',64),
            0x100000,4096,0x100000,0x200000,4096,0,1000));
        waitKernel.AdvanceMonotonicTime(1234);
        HybridCpuExternalServiceEcallV1 waitEnvelope =envelope with { Operation=HybridCpuVirtualClockServiceContractV1.WaitUntilDoomTicOperation,
            ArgumentCount=1,Argument0=44 };
        HybridCpuExternalServiceEcallResultV1 wait=waitKernel.ExternalServiceEcallTransition(
            waitKernel.CurrentContext()!.ContextId,HybridCpuPrivilegeModeV1.User,waitEnvelope);
        if(wait.Status!=HybridCpuExternalServiceStatusV1.Success ||
           wait.Disposition!=HybridCpuExternalServiceEcallDispositionV1.Parked ||
           waitKernel.Deadlines() is not [{ DeadlineTick:1258 }] ||
           waitKernel.Contexts().Single().State!=HybridCpuExecutionContextStateV1.Parked)
            throw new Exception("Doom wait did not park at the exact ceil-converted source deadline.");
        waitKernel.AdvanceMonotonicTime(1257);
        if(waitKernel.Contexts().Single().State!=HybridCpuExecutionContextStateV1.Parked)
            throw new Exception("Doom wait woke before its exact source deadline.");
        waitKernel.AdvanceMonotonicTime(1258);
        if(waitKernel.Contexts().Single().State!=HybridCpuExecutionContextStateV1.Runnable || waitKernel.Deadlines().Count!=0)
            throw new Exception("Doom wait did not become runnable exactly at its source deadline.");
        DeterministicRuntimeKernelV1 immediateKernel =new DeterministicRuntimeKernelV1();
        immediateKernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest,new string('a',64),
            0x100000,4096,0x100000,0x200000,4096,0,1000)); immediateKernel.AdvanceMonotonicTime(1000);
        HybridCpuExternalServiceEcallResultV1 immediate =immediateKernel.ExternalServiceEcallTransition(immediateKernel.CurrentContext()!.ContextId,
            HybridCpuPrivilegeModeV1.User,waitEnvelope with { Argument0=35 });
        if(immediate.Status!=HybridCpuExternalServiceStatusV1.Success ||
           immediate.Disposition!=HybridCpuExternalServiceEcallDispositionV1.Resume)
            throw new Exception("Already-reached Doom deadline did not resume immediately.");
        byte[] waitThunk=HybridCpuManagedDoomWaitEmitterV1.Emit();
        if(Enumerable.Range(0,waitThunk.Length/HybridCpuBundleSerializer.BundleSizeBytes)
               .Count(index=>(BitConverter.ToUInt64(waitThunk,index*HybridCpuBundleSerializer.BundleSizeBytes)>>48)==
                   (uint)HybridCpuOpcode.ECALL)!=1 || HybridCpuManagedDoomWaitEmitterV1.EmitObject().Status!=HybridCpuObjectStatusV1.Success)
            throw new Exception("Doom wait thunk lacks one exact ECALL or a valid HCO object.");
        HybridCpuManagedNativeResolverV1 resolver = new HybridCpuManagedNativeResolverV1();
        if (!resolver.Register(HybridCpuVirtualClockServiceContractV1.Signature, HybridCpuHostServiceV1.Clock,
                HybridCpuVirtualClockServiceContractV1.ReadTicksOperation) || resolver.Resolve(HybridCpuVirtualClockServiceContractV1.Signature) is null ||
            resolver.Resolve(HybridCpuVirtualClockServiceContractV1.Signature with { ReturnValue = new(HybridCpuInteropValueKindV1.SignedInteger, 4, 4) }) is not null)
            throw new Exception("Clock native symbol binding did not retain its exact u64 signature.");
        Console.WriteLine("PASS kernel virtual clock conversion and trusted exact ECALL envelope (loader binding still required)");
    }

    private sealed class InputProvider(params HybridCpuManagedInputProviderResultV1[] results) :
        IHybridCpuManagedInputProviderV1
    {
        private readonly Queue<HybridCpuManagedInputProviderResultV1> _results=new(results);
        public int PullCount { get; private set; }
        public HybridCpuManagedInputProviderResultV1 Pull()
        {
            PullCount++;
            return _results.Count==0?new(false,0,string.Empty,"empty test provider"):_results.Dequeue();
        }
    }
}
