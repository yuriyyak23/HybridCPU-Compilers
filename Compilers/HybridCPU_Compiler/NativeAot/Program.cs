using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;

namespace HybridCPU.Compiler.NativeAot;

internal static class Program
{
    private const string BodyPresentationEnvironment = "HYBRIDCPU_NATIVEAOT_BODY_PRESENTATION_V1";
    private const string BoundedRecursionEnvironment = "HYBRIDCPU_BOUNDED_RECURSION_V1";
    private const string MetadataInputsEnvironment = "HYBRIDCPU_METADATA_ONLY_INPUTS_V1";
    private sealed record MetadataInput(string Path, string Sha256);

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "write-pack-manifest")
            return WritePackManifest(args);
        if (args.Length > 0 && args[0] == "publish-bringup")
            return PublishBringup(args);
        if (args.Length > 0 && args[0] == "publish-graph")
            return PublishGraph(args);

        using CancellationTokenSource cancellation = new CancellationTokenSource();
        object lifetime = new();
        bool completed = false;
        ConsoleCancelEventHandler handler = (_, signal) =>
        {
            lock (lifetime)
            {
                if (completed) return;
                signal.Cancel = true;
                cancellation.Cancel();
            }
        };
        Console.CancelKeyPress += handler;
        try { return Compile(cancellation.Token, args); }
        catch (OperationCanceledException exception) when (exception.CancellationToken == cancellation.Token)
        {
            Console.Error.WriteLine("HCNAOT2003: non-publish compilation cancelled before output write.");
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            lock (lifetime) completed = true;
        }
    }

    /// <summary>Caller-owned non-publish compile lifetime; cancellation ends at the synchronous output write boundary.</summary>
    internal static int Compile(CancellationToken cancellationToken, string[] args)
    {
        NativeAotSeamRequestV1? request = Parse(args, out NativeAotSeamDiagnosticV1? parseError);
        if (request is null)
        {
            Console.Error.WriteLine($"{parseError!.Code}: {parseError.Message}");
            return 2;
        }

        if (!string.Equals(request.SourceCommit, NativeAotSeamBaselineV1.SourceCommit, StringComparison.Ordinal) ||
            !string.Equals(request.PatchDigest, NativeAotSeamBaselineV1.PatchDigest, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("HCNAOT1001: unsupported dotnet/runtime source or patch-set digest");
            return 3;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!SingNextPclSidebandV1.TryPrepare(request.OutputPath,
                out SingNextPclEmissionContextV1? pclContext, out string? pclDiagnostic))
        {
            DeleteRejectedPclOutput(request.OutputPath);
            Console.Error.WriteLine(pclDiagnostic);
            return 13;
        }

        cancellationToken.ThrowIfCancellationRequested();
        string? presentationPath = Environment.GetEnvironmentVariable(BodyPresentationEnvironment);
        if (request.OutputKind == NativeAotSeamOutputKindV1.RestrictedImage &&
            !string.IsNullOrWhiteSpace(presentationPath))
            return CompilePresentedImage(request, presentationPath, pclContext, cancellationToken);

        RestrictedCilImportResultV1 imported = new RestrictedCilImporterV1(
            mode: request.OutputKind == NativeAotSeamOutputKindV1.RestrictedImage
                ? RestrictedCilImportModeV1.ScalarControlFlowV2
                : RestrictedCilImportModeV1.RestrictedScalarV1).ImportFile(
            request.AssemblyPath, new(request.TypeName, request.MethodName), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (imported.Status != RestrictedCilImportStatusV1.Success || imported.Program is null || imported.Provenance is null)
        {
            if (request.OutputKind == NativeAotSeamOutputKindV1.RestrictedImage &&
                imported.Status == RestrictedCilImportStatusV1.Unsupported &&
                imported.Diagnostics.Any(diagnostic => diagnostic.Code is "HCCIL1009" or "HCCIL1011" or "HCCIL1301" or "HCCIL1501"))
                return CompileStandaloneLinkedImage(request, pclContext, cancellationToken);
            foreach (IrFrontendDiagnosticV1 diagnostic in imported.Diagnostics)
                Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
            return imported.Status == RestrictedCilImportStatusV1.Unsupported ? 4 : 5;
        }

        if (!SingNextPclCanonicalIrV1.TryMaterialize(pclContext, imported.Program,
                out string? pclInputDiagnostic))
        {
            DeleteRejectedPclOutput(request.OutputPath);
            Console.Error.WriteLine(pclInputDiagnostic);
            return 15;
        }

        byte[] code;
        string? allocationWitnessDigest = null;
        try
        {
            IrRegisterAllocationResultV1 Allocate(IrProgramSchedule schedule, IrProgramBundlingResult bundles)
            {
                HybridCpuMiiResourceModelV1 resourceModel = HybridCpuMiiResourceModelV1.Create(
                    new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8);
                return new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(
                    cancellationToken, schedule, bundles,
                    resourceModel: resourceModel,
                    options: HybridCpuRegisterAllocationOptionsV1.Qualification);
            }
            (IrProgramSchedule _, IrProgramBundlingResult? bundles, IrRegisterAllocationResultV1? allocation) = HybridCpuProgramBundler.CompileWithAllocation(cancellationToken, imported.Program,
                input => new HybridCpuLocalListScheduler().ScheduleProgram(cancellationToken, input), new HybridCpuBundleFormer(),
                request.OutputKind == NativeAotSeamOutputKindV1.RestrictedImage ? Allocate : null);
            if (request.OutputKind == NativeAotSeamOutputKindV1.RestrictedImage)
            {
                if (allocation!.Status != IrRegisterAllocationStatusV1.Allocated || allocation.Witness is null)
                {
                    Console.Error.WriteLine($"HCNAOT2002: register allocation rejected the restricted image: {allocation.Reason}");
                    return 8;
                }
                allocationWitnessDigest = allocation.Witness.WitnessDigest;
            }
            code = request.OutputKind == NativeAotSeamOutputKindV1.RestrictedImage
                ? HybridCpuIrProgramCompiler.CompileAllocated(cancellationToken, allocation!).ProgramImage
                : HybridCpuPostBundleCompiler.Compile(cancellationToken, bundles,
                    resolveControlFlowTargets: false, emitAnnotations: false).ProgramImage;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"HCNAOT2001: Core rejected the qualified method: {exception.Message}");
            return 6;
        }

        string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
        if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);
        NativeAotSeamArtifactV1 artifact = new NativeAotSeamArtifactV1(
            NativeAotSeamBaselineV1.SchemaId,
            NativeAotSeamStatusV1.Success,
            NativeAotSeamBaselineV1.SourceCommit,
            NativeAotSeamBaselineV1.PatchDigest,
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest,
            RestrictedCilSupportMatrixV1.Default.ContractDigest,
            RestrictedCilSupportMatrixV1.OptionsDigest(RestrictedCilImportBudgetsV1.Production),
            imported.Provenance.MethodIdentity,
            imported.Provenance.CilMethodToken,
            imported.Provenance.PeSha256,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(code)).ToLowerInvariant(),
            code.Length,
            Array.Empty<string>(),
            Array.Empty<string>(),
            null,
            null,
            Array.Empty<NativeAotSeamDiagnosticV1>());
        object manifest = artifact;
        byte[] output = code;
        if (request.OutputKind is NativeAotSeamOutputKindV1.RelocatableObject or NativeAotSeamOutputKindV1.RestrictedImage)
        {
            HybridCpuObjectArtifactV1 objectArtifact = new HybridCpuObjectWriterV1().Write(new(
                [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuObjectFormatContractV1.BundleAlignmentBytes, code, (ulong)code.Length)],
                [new(imported.Provenance.MethodIdentity, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default,
                    ".text", 0, (ulong)code.Length, IsDefinition: true)],
                Array.Empty<HybridCpuObjectRelocationV1>(),
                HybridCpuTargetPlatformContractV1.Default.ContractDigest,
                HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
            if (objectArtifact.Status != HybridCpuObjectStatusV1.Success)
            {
                foreach (HybridCpuObjectDiagnosticV1 diagnostic in objectArtifact.Diagnostics)
                    Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
                return 7;
            }
            if (request.OutputKind == NativeAotSeamOutputKindV1.RelocatableObject)
            {
                output = objectArtifact.Bytes;
                manifest = new NativeAotObjectArtifactV1(
                    "hybridcpu.nativeaot-object/v1",
                    artifact,
                    HybridCpuObjectFormatContractV1.ContractDigest,
                    HybridCpuObjectFormatContractV1.OptionsDigest,
                    objectArtifact.MetadataDigest,
                    objectArtifact.ObjectSha256,
                    objectArtifact.Bytes.Length);
            }
            else
            {
                HybridCpuStaticLinkArtifactV1 linked = new HybridCpuStaticLinkerV1().Link(
                    [new HybridCpuLinkInputV1(imported.Provenance.ManagedModuleIdentity, objectArtifact.Bytes)]);
                if (linked.Status != HybridCpuLinkStatusV1.Success)
                {
                    foreach (HybridCpuLinkDiagnosticV1 diagnostic in linked.Diagnostics)
                        Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
                    return 9;
                }
                HybridCpuRestrictedImageV1 image = new HybridCpuRestrictedImageBuilderV1().Build(
                    new(linked, imported.Provenance.MethodIdentity,
                        HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes));
                if (image.Status != HybridCpuStartupStatusV1.Success)
                {
                    foreach (HybridCpuStartupDiagnosticV1 diagnostic in image.Diagnostics)
                        Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
                    return 10;
                }
                output = image.PackageBytes;
                manifest = new NativeAotRestrictedImageArtifactV1(
                    "hybridcpu.nativeaot-restricted-image/v1",
                    artifact,
                    HybridCpuRegisterAllocationContractV1.Default.ContractDigest,
                    HybridCpuRegisterAllocationOptionsV1.Qualification.OptionsDigest,
                    allocationWitnessDigest ?? throw new InvalidOperationException("Qualified allocation witness is absent."),
                    HybridCpuObjectFormatContractV1.ContractDigest,
                    objectArtifact.ObjectSha256,
                    HybridCpuStaticLinkOptionsV1.Production.OptionsDigest,
                    linked.LinkMapDigest,
                    HybridCpuRestrictedStartupOptionsV1.Production.OptionsDigest,
                    imported.Provenance.MethodIdentity,
                    image.EntryAddress,
                    image.PackageSha256,
                    image.PackageBytes.Length,
                    ProfileId: ScalarControlFlowV2ProfileContractV1.ProfileId);
            }
        }
        return NativeAotSeamOutputV1.Write(request.OutputPath, output, manifest, pclContext, cancellationToken);
    }

    private static int CompilePresentedImage(
        NativeAotSeamRequestV1 request,
        string presentationPath,
        SingNextPclEmissionContextV1? pclContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativeAotBodyPresentationV1? presentation;
        try
        {
            presentation = JsonSerializer.Deserialize<NativeAotBodyPresentationV1>(File.ReadAllText(presentationPath));
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            Console.Error.WriteLine($"HCNAOT3001: body presentation cannot be read: {exception.Message}");
            return 11;
        }
        byte[] pe = File.ReadAllBytes(request.AssemblyPath);
        string peSha = Convert.ToHexString(SHA256.HashData(pe)).ToLowerInvariant();
        NativeAotPresentedBodyV1[] canonicalBodies = presentation?.PresentedBodies?
            .Where(static body => !string.IsNullOrWhiteSpace(body.TypeName) && !string.IsNullOrWhiteSpace(body.MethodName))
            .Distinct().OrderBy(static body => body.TypeName, StringComparer.Ordinal)
            .ThenBy(static body => body.MethodName, StringComparer.Ordinal)
            .ThenBy(static body => body.MetadataToken).ToArray() ?? [];
        if (presentation is null || presentation.Root is null || presentation.PresentedBodies is null ||
            presentation.SchemaId != NativeAotBodyPresentationV1.Schema ||
            presentation.SchemaVersion != NativeAotBodyPresentationV1.Version ||
            presentation.ProfileId != ScalarControlFlowV2ProfileContractV1.ProfileId ||
            presentation.AssemblySha256 != peSha || presentation.Root.TypeName != request.TypeName ||
            presentation.Root.MethodName != request.MethodName || presentation.Root.MetadataToken == 0 ||
            presentation.PresentedBodies.Count == 0 ||
            canonicalBodies.Length != presentation.PresentedBodies.Count ||
            !canonicalBodies.SequenceEqual(presentation.PresentedBodies) ||
            !canonicalBodies.Contains(presentation.Root) ||
            presentation.ContractDigest != ComputePresentationDigest(presentation))
        {
            Console.Error.WriteLine("HCNAOT3002: body presentation identity, root, assembly, or digest is invalid");
            return 12;
        }
        RestrictedCilMethodSelectorV1[] bodies = presentation.PresentedBodies
            .Select(static body => new RestrictedCilMethodSelectorV1(body.TypeName, body.MethodName, body.MetadataToken))
            .ToArray();
        ManagedBodyWorldModuleV1[] dependencyModules = Directory
            .GetFiles(Path.GetDirectoryName(request.AssemblyPath)!, "*.dll")
            .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(request.AssemblyPath),
                StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(static path => File.ReadAllBytes(path))
            .Where(static bytes => IsManagedPe(bytes))
            .Select(static bytes => new ManagedBodyWorldModuleV1(bytes,
                $"nativeaot-module:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}"))
            .ToArray();
        ManagedBodyWorldModuleV1[] metadataInputs;
        try { metadataInputs = ReadMetadataInputs(); }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"HCNAOT3006: metadata-only pack inputs are invalid: {e.Message}");
            return 17;
        }
        ManagedBoundedRecursionOptionsV1? recursionOptions = null;
        string? recursionJson = Environment.GetEnvironmentVariable(BoundedRecursionEnvironment);
        if (!string.IsNullOrWhiteSpace(recursionJson))
        {
            try { recursionOptions = JsonSerializer.Deserialize<ManagedBoundedRecursionOptionsV1>(recursionJson); }
            catch (JsonException) { }
            if (recursionOptions is null || !recursionOptions.IsValid || !recursionOptions.Enabled)
            {
                Console.Error.WriteLine("HCNAOT3005: bounded-recursion V1 options are invalid");
                return 16;
            }
        }
        ManagedCallGraphCompilationV1 compilation = new RestrictedCilImporterV1(
            mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            runtimeExternalBindings: DoomSharpGuestAbiBindingPackageV1.Bindings).ImportBodyWorld(new(
                ManagedBodyWorldModeV1.AdapterPresented, pe, $"nativeaot:{peSha}",
                [new(request.TypeName, request.MethodName, presentation.Root.MetadataToken)], bodies, recursionOptions,
                dependencyModules, metadataInputs), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (compilation.Status != RestrictedCilImportStatusV1.Success || compilation.Graph is null)
        {
            foreach (IrFrontendDiagnosticV1 diagnostic in compilation.Diagnostics)
                Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}" +
                    (string.IsNullOrWhiteSpace(diagnostic.StableSourceIdentity)
                        ? string.Empty : $" [{diagnostic.StableSourceIdentity}]"));
            return 13;
        }
        return EmitLinkedImage(request, compilation, peSha, presentation.ContractDigest, pclContext, cancellationToken);
    }

    private static int CompileStandaloneLinkedImage(NativeAotSeamRequestV1 request,
        SingNextPclEmissionContextV1? pclContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] pe = File.ReadAllBytes(request.AssemblyPath);
        string peSha = Convert.ToHexString(SHA256.HashData(pe)).ToLowerInvariant();
        // Fresh closed-module admission resolves actual reachable managed callees.
        // An unresolved helper/external target or unsupported body still fails closed.
        ManagedBodyWorldModuleV1[] metadataInputs;
        try { metadataInputs = ReadMetadataInputs(); }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"HCNAOT3006: metadata-only pack inputs are invalid: {e.Message}");
            return 17;
        }
        ManagedCallGraphCompilationV1 compilation = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
                $"standalone:{peSha}", [new(request.TypeName, request.MethodName)], [], MetadataOnlyModules: metadataInputs), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (compilation.Status != RestrictedCilImportStatusV1.Success || compilation.Graph is null)
        {
            foreach (IrFrontendDiagnosticV1 diagnostic in compilation.Diagnostics)
                Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
            return 13;
        }
        return EmitLinkedImage(request, compilation, peSha, null, pclContext, cancellationToken);
    }

    private static int EmitLinkedImage(NativeAotSeamRequestV1 request,
        ManagedCallGraphCompilationV1 compilation, string peSha, string? bodyPresentationDigest,
        SingNextPclEmissionContextV1? pclContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (compilation.Graph is null) return 13;
        ManagedCompiledMethodV1 root = compilation.Methods.Single(method =>
            compilation.Graph!.RootIdentities.Contains(method.Identity.StableIdentity, StringComparer.Ordinal));
        if (root.Identity.CanonicalSignature is not ("():System.Int32" or "():System.Void"))
        {
            Console.Error.WriteLine("HCNAOT3004: restricted managed entry must be static parameterless void or int32");
            return 14;
        }
        ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(
            cancellationToken, compilation, root.Identity.StableIdentity);
        if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || linked.RestrictedImage is null)
        {
            foreach (ScalarControlFlowV2LinkDiagnosticV1 diagnostic in linked.Diagnostics)
                Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
            return 15;
        }
        ScalarControlFlowV2MethodObjectV1 rootObject = linked.MethodObjects.Single(method =>
            method.MethodIdentity == root.Identity.StableIdentity);
        ManagedWorkstreamDiscoveryV1 workstreams = ManagedWorkstreamDiscoveryContractV1.Discover(compilation, linked);
        if (!workstreams.IsComplete)
        {
            Console.Error.WriteLine($"HCWORK1002: post-link runtime HCOs have no workstream classification: {string.Join(", ", workstreams.UnclassifiedRuntimeModules)}");
            return 18;
        }
        if (!SingNextPclCanonicalIrV1.TryMaterializeGraph(pclContext, compilation.Methods,
                compilation.Graph.GraphDigest, root.Identity.StableIdentity,
                out string? pclInputDiagnostic))
        {
            DeleteRejectedPclOutput(request.OutputPath);
            Console.Error.WriteLine(pclInputDiagnostic);
            return 15;
        }
        NativeAotSeamArtifactV1 codeArtifact = new NativeAotSeamArtifactV1(
            NativeAotSeamBaselineV1.SchemaId, NativeAotSeamStatusV1.Success,
            NativeAotSeamBaselineV1.SourceCommit, NativeAotSeamBaselineV1.PatchDigest,
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest,
            RestrictedCilSupportMatrixV1.Default.ContractDigest,
            RestrictedCilSupportMatrixV1.OptionsDigest(RestrictedCilImportBudgetsV1.Production),
            root.Identity.StableIdentity, $"0x{root.Identity.MetadataToken:x8}", peSha,
            rootObject.CodeSha256, rootObject.CodeBytes,
            linked.LinkedImage!.AppliedRelocations.Select(static relocation =>
                $"{relocation.ModuleIdentity}:{relocation.Offset}:{relocation.Kind}:{relocation.TargetSymbol}").ToArray(),
            Array.Empty<string>(), null, null, Array.Empty<NativeAotSeamDiagnosticV1>());
        HybridCpuRestrictedImageV1 image = linked.RestrictedImage;
        NativeAotRestrictedImageArtifactV1 artifact = new NativeAotRestrictedImageArtifactV1(
            "hybridcpu.nativeaot-restricted-image/v2", codeArtifact,
            HybridCpuRegisterAllocationContractV1.Default.ContractDigest,
            HybridCpuRegisterAllocationOptionsV1.Qualification.OptionsDigest,
            rootObject.AllocationWitnessDigest, HybridCpuObjectFormatContractV1.ContractDigest,
            rootObject.ObjectArtifact.ObjectSha256, HybridCpuStaticLinkOptionsV1.Production.OptionsDigest,
            linked.LinkedImage.LinkMapDigest, HybridCpuRestrictedStartupOptionsV1.Production.OptionsDigest,
            root.Identity.StableIdentity, image.EntryAddress, image.PackageSha256, image.PackageBytes.Length,
            ScalarControlFlowV2ProfileContractV1.ProfileId, bodyPresentationDigest,
            compilation.Graph.GraphDigest, linked.MethodObjects.Count, linked.OrderedObjectDigest, linked.ProvenanceDigest,
            compilation.Graph.HasBoundedRecursion ? ManagedBoundedRecursionContractV1.Default.ContractDigest : null,
            compilation.Graph.RecursionProofs?.SingleOrDefault()?.ProofDigest,
            linked.RecursionStackEvidence?.EvidenceDigest,
            compilation.Graph.HasBoundedRecursion ? compilation.Graph.MaximumDynamicDepth : null,
            linked.RecursionStackEvidence?.RequiredStackBytes,
            workstreams.RequiredWorkstreams, workstreams.Evidence,
            workstreams.UnclassifiedRuntimeModules, workstreams.ContractDigest);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        return NativeAotSeamOutputV1.Write(request.OutputPath, image.PackageBytes, artifact, pclContext, cancellationToken);
    }

    private static void DeleteRejectedPclOutput(string outputPath)
        => NativeAotSeamOutputV1.RejectBestEffort(outputPath);

    private static int WritePackManifest(string[] args)
    {
        if (args.Length != 3 || args[1] != "--out" || string.IsNullOrWhiteSpace(args[2]))
        {
            Console.Error.WriteLine("HCPUB0001: expected: write-pack-manifest --out O");
            return 20;
        }
        string path = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(HybridCpuSdkPackContractV1.CreateManifest(),
            new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return 0;
    }

    private static int PublishBringup(string[] args)
    {
        string[] keys = ["--assembly", "--type", "--method", "--out", "--manifest", "--target-rid", "--host-rid", "--sdk-version", "--requirements"];
        if (args.Length != 1 + keys.Length * 2)
        {
            Console.Error.WriteLine($"HCPUB0002: expected publish-bringup plus exactly: {string.Join(", ", keys)}");
            return 21;
        }
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index += 2)
        {
            if (!keys.Contains(args[index], StringComparer.Ordinal) || !values.TryAdd(args[index], args[index + 1]))
            {
                Console.Error.WriteLine("HCPUB0003: publish arguments must be unique known named pairs");
                return 22;
            }
        }
        if (keys.Any(key => !values.ContainsKey(key)) || keys.Where(key => key != "--requirements").Any(key => string.IsNullOrWhiteSpace(values[key])))
        {
            Console.Error.WriteLine("HCPUB0004: one or more publish arguments are missing");
            return 23;
        }
        HybridCpuRuntimePackManifestV1? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<HybridCpuRuntimePackManifestV1>(File.ReadAllText(values["--manifest"]));
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            Console.Error.WriteLine($"HCPUB0005: runtime-pack manifest cannot be read: {exception.Message}");
            return 24;
        }
        if (manifest is null)
        {
            Console.Error.WriteLine("HCPUB0005: runtime-pack manifest is empty");
            return 24;
        }
        string[] declaredRequirements = values["--requirements"].Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string? validation = HybridCpuSdkPackContractV1.Validate(manifest, values["--target-rid"], values["--host-rid"], values["--sdk-version"], declaredRequirements);
        if (validation is not null)
        {
            Console.Error.WriteLine(validation);
            return 25;
        }
        string output = Path.GetFullPath(values["--out"]);
        int compile = Compile(CancellationToken.None, ["compile-image", "--assembly", values["--assembly"], "--type", values["--type"],
            "--method", values["--method"], "--out", output, "--source-commit", manifest.SourceCommit,
            "--patch-digest", manifest.PatchDigest]);
        if (compile != 0) return compile;
        NativeAotRestrictedImageArtifactV1? image = JsonSerializer.Deserialize<NativeAotRestrictedImageArtifactV1>(File.ReadAllText(output + ".json"));
        if (image is null)
        {
            Console.Error.WriteLine("HCPUB2001: image manifest is absent after compilation");
            return 26;
        }
        (string[] derivedRequirements, string? workstreamFailure) = ValidatePostLinkWorkstreams(
            manifest, image, values["--target-rid"], values["--host-rid"], values["--sdk-version"], declaredRequirements);
        if (workstreamFailure is not null)
        {
            DeleteRejectedImage(output);
            Console.Error.WriteLine(workstreamFailure);
            return 25;
        }
        HybridCpuPublishProvenanceV1 provenance = new HybridCpuPublishProvenanceV1(
            HybridCpuSdkPackContractV1.ProvenanceSchemaId, manifest.ContractDigest, manifest.PackVersion,
            manifest.TargetRid, values["--host-rid"], values["--sdk-version"], manifest.SourceCommit,
            manifest.PatchDigest, manifest.PublishPatchDigest, manifest.PatchSetDigest,
            manifest.TargetContractDigest, manifest.ManagedAbiDigest,
            manifest.ManagedFeatureSetDigest, HybridCpuSdkPackContractV1.LinkerIdentity, manifest.LinkOptionsDigest,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(values["--assembly"]))).ToLowerInvariant(),
            image.PackageSha256, image.PackageLength, derivedRequirements, null, null, null, null, null, false,
            HybridCpuSdkPackContractV1.OptionsDigest(values["--host-rid"], values["--sdk-version"], derivedRequirements),
            DeclaredWorkstreams: declaredRequirements, WorkstreamDiscoveryDigest: image.WorkstreamDiscoveryDigest);
        File.WriteAllText(output + ".provenance.json", JsonSerializer.Serialize(provenance,
            new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return 0;
    }

    private static int PublishGraph(string[] args)
    {
        string[] requiredKeys = ["--assembly", "--type", "--method", "--out", "--manifest", "--target-rid", "--host-rid",
            "--sdk-version", "--requirements", "--dotnet-host", "--ilcompiler", "--runtime-refs", "--adapter", "--pack-files"];
        string[] keys = [.. requiredKeys, "--presented-methods", "--recursion-depth", "--recursion-stack-bytes", "--rejected-evidence-out"];
        if (args.Length < 1 + requiredKeys.Length * 2 || args.Length > 1 + keys.Length * 2 || args.Length % 2 == 0)
        {
            Console.Error.WriteLine($"HCPUB0006: expected publish-graph plus: {string.Join(", ", requiredKeys)} and optional --presented-methods");
            return 27;
        }
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index += 2)
        {
            if (!keys.Contains(args[index], StringComparer.Ordinal) || !values.TryAdd(args[index], args[index + 1]))
            {
                Console.Error.WriteLine("HCPUB0007: graph-publish arguments must be unique known named pairs");
                return 28;
            }
        }
        if (requiredKeys.Any(key => !values.ContainsKey(key)) ||
            requiredKeys.Where(key => key != "--requirements").Any(key => string.IsNullOrWhiteSpace(values[key])))
        {
            Console.Error.WriteLine("HCPUB0008: one or more graph-publish arguments are missing");
            return 29;
        }
        bool hasRecursionDepth = values.TryGetValue("--recursion-depth", out string? recursionDepthText);
        bool hasRecursionStack = values.TryGetValue("--recursion-stack-bytes", out string? recursionStackText);
        ManagedBoundedRecursionOptionsV1? recursionOptions = null;
        if (hasRecursionDepth != hasRecursionStack || hasRecursionDepth &&
            (!int.TryParse(recursionDepthText, out int recursionDepth) ||
             !int.TryParse(recursionStackText, out int recursionStack) ||
             !(recursionOptions = ManagedBoundedRecursionOptionsV1.Create(true, recursionDepth, recursionStack)).IsValid))
        {
            Console.Error.WriteLine("HCPUB0010: bounded recursion requires valid paired --recursion-depth and --recursion-stack-bytes V1 options");
            return 36;
        }
        if (!File.Exists(values["--dotnet-host"]) || !File.Exists(values["--ilcompiler"]) ||
            !File.Exists(values["--adapter"]) || !File.Exists(values["--pack-files"]) || !Directory.Exists(values["--runtime-refs"]))
        {
            Console.Error.WriteLine("HCPUB1006: exact dotnet host, ILCompiler, adapter or runtime references are missing");
            return 30;
        }
        string? installedPackValidation = HybridCpuSdkPackContractV1.ValidateInstalledPackFiles(
            values["--pack-files"], values["--adapter"], values["--ilcompiler"], values["--runtime-refs"]);
        if (installedPackValidation is not null)
        {
            Console.Error.WriteLine(installedPackValidation);
            return 34;
        }
        HybridCpuRuntimePackManifestV1? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<HybridCpuRuntimePackManifestV1>(File.ReadAllText(values["--manifest"]));
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            Console.Error.WriteLine($"HCPUB0005: runtime-pack manifest cannot be read: {exception.Message}");
            return 24;
        }
        if (manifest is null)
        {
            Console.Error.WriteLine("HCPUB0005: runtime-pack manifest is empty");
            return 24;
        }
        string[] declaredRequirements = values["--requirements"].Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string? validation = HybridCpuSdkPackContractV1.Validate(manifest, values["--target-rid"], values["--host-rid"], values["--sdk-version"], declaredRequirements);
        if (validation is not null)
        {
            Console.Error.WriteLine(validation);
            return 25;
        }

        string assembly = Path.GetFullPath(values["--assembly"]);
        string output = Path.GetFullPath(values["--out"]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string? rejectedEvidenceOutput = values.TryGetValue("--rejected-evidence-out", out string? configuredEvidence)
            ? Path.GetFullPath(configuredEvidence)
            : null;
        if (rejectedEvidenceOutput is not null &&
            (!rejectedEvidenceOutput.EndsWith(".unqualified.hcexe", StringComparison.Ordinal) ||
             string.Equals(rejectedEvidenceOutput, output, StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("HCPUB0011: rejected evidence output must be a distinct *.unqualified.hcexe path");
            return 37;
        }
        NativeAotBodyPresentationV1? presentation = null;
        string? presentationPath = null;
        values.TryGetValue("--presented-methods", out string? presentedMethods);
        try
        {
            presentation = CreateBodyPresentation(assembly, values["--type"], values["--method"], presentedMethods);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"HCNAOT3003: {exception.Message}");
            return 35;
        }
        presentationPath = Path.Combine(Path.GetDirectoryName(output)!, $"hybridcpu-body-world-{Guid.NewGuid():N}.json");
        File.WriteAllText(presentationPath, JsonSerializer.Serialize(presentation,
            new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        ProcessStartInfo start = new ProcessStartInfo(Path.GetFullPath(values["--dotnet-host"]))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (presentationPath is not null)
            start.Environment[BodyPresentationEnvironment] = presentationPath;
        // Already covered by ValidateInstalledPackFiles above. The child verifies the
        // exact bytes again; these declarations never become presented CIL bodies.
        start.Environment[MetadataInputsEnvironment] = JsonSerializer.Serialize(
            new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "mscorlib.dll" }
                .Select(name => Path.Combine(Path.GetFullPath(values["--runtime-refs"]), name))
                .Where(File.Exists).Select(path => new MetadataInput(path,
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant())).ToArray());
        if (recursionOptions is not null)
            start.Environment[BoundedRecursionEnvironment] = JsonSerializer.Serialize(recursionOptions);
        start.ArgumentList.Add(Path.GetFullPath(values["--ilcompiler"]));
        string[] runtimeReferences = Directory.GetFiles(values["--runtime-refs"], "*.dll").Order(StringComparer.Ordinal).ToArray();
        string[] applicationReferences = Directory.GetFiles(Path.GetDirectoryName(assembly)!, "*.dll").Order(StringComparer.Ordinal).ToArray();
        foreach (string reference in runtimeReferences.Concat(applicationReferences).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add("--reference");
            start.ArgumentList.Add(reference);
        }
        AddProcessPair(start, "--out", output);
        start.ArgumentList.Add("--noscan");
        AddProcessPair(start, "--singlemethodtypename", $"{values["--type"]}, {Path.GetFileNameWithoutExtension(assembly)}");
        AddProcessPair(start, "--singlemethodname", values["--method"]);
        AddProcessPair(start, "--codegenopt", $"HybridCpuDotNetHost={Path.GetFullPath(values["--dotnet-host"])}");
        AddProcessPair(start, "--codegenopt", $"HybridCpuAdapter={Path.GetFullPath(values["--adapter"])}");
        AddProcessPair(start, "--codegenopt", $"HybridCpuAssembly={assembly}");
        AddProcessPair(start, "--codegenopt", $"HybridCpuType={values["--type"]}");
        AddProcessPair(start, "--codegenopt", $"HybridCpuMethod={values["--method"]}");
        AddProcessPair(start, "--codegenopt", $"HybridCpuSourceCommit={manifest.SourceCommit}");
        AddProcessPair(start, "--codegenopt", $"HybridCpuPatchDigest={manifest.PatchDigest}");
        AddProcessPair(start, "--codegenopt", "HybridCpuOutputKind=image");
        start.ArgumentList.Add(assembly);
        string responsePath = Path.Combine(Path.GetDirectoryName(output)!, $"hybridcpu-ilcompiler-{Guid.NewGuid():N}.rsp");
        string ilCompilerAssembly = start.ArgumentList[0];
        File.WriteAllLines(responsePath, start.ArgumentList.Skip(1).Select(QuoteResponseArgument));
        start.ArgumentList.Clear();
        start.ArgumentList.Add(ilCompilerAssembly);
        start.ArgumentList.Add("@" + responsePath);
        string stdout;
        string stderr;
        int exitCode;
        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Pinned ILCompiler did not start.");
            Task<string> stdoutRead = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrRead = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(stdoutRead, stderrRead);
            stdout = stdoutRead.GetAwaiter().GetResult();
            stderr = stderrRead.GetAwaiter().GetResult();
            exitCode = process.ExitCode;
        }
        finally
        {
            if (presentationPath is not null)
                File.Delete(presentationPath);
            File.Delete(responsePath);
        }
        if (exitCode != 0)
        {
            Console.Error.WriteLine($"HCPUB2002: ILCompiler graph publication failed with exit {exitCode}: {stderr}{stdout}");
            return 31;
        }
        if (!File.Exists(output) || !File.Exists(output + ".json"))
        {
            Console.Error.WriteLine("HCPUB2003: ILCompiler graph did not emit the image and sidecar");
            return 32;
        }
        NativeAotRestrictedImageArtifactV1? image = JsonSerializer.Deserialize<NativeAotRestrictedImageArtifactV1>(File.ReadAllText(output + ".json"));
        if (image is null || image.PackageSha256 != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant())
        {
            Console.Error.WriteLine("HCPUB2004: ILCompiler graph image sidecar does not match the emitted image");
            return 33;
        }
        (string[] derivedRequirements, string? workstreamFailure) = ValidatePostLinkWorkstreams(
            manifest, image, values["--target-rid"], values["--host-rid"], values["--sdk-version"], declaredRequirements);
        if (workstreamFailure is not null)
        {
            string? evidenceFailure = null;
            try
            {
                if (rejectedEvidenceOutput is not null)
                    WriteRejectedEvidence(output, rejectedEvidenceOutput, workstreamFailure, derivedRequirements, image);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                evidenceFailure = $"HCPUB1013: rejected evidence could not be quarantined: {exception.Message}";
            }
            finally
            {
                DeleteRejectedImage(output);
            }
            if (evidenceFailure is not null)
            {
                Console.Error.WriteLine(evidenceFailure);
                return 37;
            }
            Console.Error.WriteLine(workstreamFailure);
            return 25;
        }
        string runtimeReferenceSetDigest = HybridCpuSdkPackContractV1.Hash(string.Join('|', runtimeReferences.Select(path =>
            $"{Path.GetFileName(path)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()}")));
        HybridCpuPublishProvenanceV1 provenance = new HybridCpuPublishProvenanceV1(
            HybridCpuSdkPackContractV1.ProvenanceSchemaId, manifest.ContractDigest, manifest.PackVersion,
            manifest.TargetRid, values["--host-rid"], values["--sdk-version"], manifest.SourceCommit,
            manifest.PatchDigest, manifest.PublishPatchDigest, manifest.PatchSetDigest,
            manifest.TargetContractDigest, manifest.ManagedAbiDigest, manifest.ManagedFeatureSetDigest,
            HybridCpuSdkPackContractV1.LinkerIdentity, manifest.LinkOptionsDigest,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))).ToLowerInvariant(),
            image.PackageSha256, image.PackageLength, derivedRequirements,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(values["--ilcompiler"]))).ToLowerInvariant(),
            runtimeReferenceSetDigest,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(values["--pack-files"]))).ToLowerInvariant(),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(values["--dotnet-host"]))).ToLowerInvariant(),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(values["--adapter"]))).ToLowerInvariant(),
            true,
            HybridCpuSdkPackContractV1.OptionsDigest(values["--host-rid"], values["--sdk-version"], derivedRequirements),
            presentation?.ProfileId, presentation?.ContractDigest,
            image.ManagedGraphDigest, image.CompiledMethodCount, image.OrderedObjectDigest, image.BackendProvenanceDigest,
            image.BoundedRecursionContractDigest, image.RecursionProofDigest, image.RecursionStackEvidenceDigest,
            image.MaximumDynamicDepth, image.RequiredStackBytes,
            declaredRequirements, image.WorkstreamDiscoveryDigest);
        File.WriteAllText(output + ".provenance.json", JsonSerializer.Serialize(provenance,
            new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return 0;
    }

    private static (string[] DerivedRequirements, string? Failure) ValidatePostLinkWorkstreams(
        HybridCpuRuntimePackManifestV1 manifest,
        NativeAotRestrictedImageArtifactV1 image,
        string targetRid,
        string hostRid,
        string sdkVersion,
        IReadOnlyList<string> declaredRequirements)
    {
        if (string.IsNullOrWhiteSpace(image.ManagedGraphDigest) || string.IsNullOrWhiteSpace(image.OrderedObjectDigest) ||
            !ManagedWorkstreamDiscoveryContractV1.ValidateArtifact(image.ManagedGraphDigest, image.OrderedObjectDigest,
                image.DerivedRequiredWorkstreams, image.WorkstreamEvidence,
                image.UnclassifiedRuntimeModules, image.WorkstreamDiscoveryDigest))
            return ([], "HCPUB1011: post-link workstream discovery evidence is absent, non-canonical or digest-invalid.");
        if (image.UnclassifiedRuntimeModules is { Count: > 0 })
            return ([], $"HCPUB1012: post-link runtime HCOs are not classified: {string.Join(", ", image.UnclassifiedRuntimeModules)}.");

        string[] derived = image.DerivedRequiredWorkstreams!.ToArray();
        string? validation = HybridCpuSdkPackContractV1.Validate(manifest, targetRid, hostRid, sdkVersion, derived);
        if (validation is not null)
            return (derived, validation);
        validation = HybridCpuSdkPackContractV1.ValidateDeclaredWorkstreams(declaredRequirements, derived);
        if (validation is not null) return (derived, validation);
        return (derived, null);
    }

    private static void DeleteRejectedImage(string output)
    {
        if (File.Exists(output)) File.Delete(output);
        if (File.Exists(output + ".json")) File.Delete(output + ".json");
        if (File.Exists(output + ".provenance.json")) File.Delete(output + ".provenance.json");
    }

    private static void WriteRejectedEvidence(
        string output,
        string evidenceOutput,
        string failure,
        IReadOnlyList<string> derivedRequirements,
        NativeAotRestrictedImageArtifactV1 image)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(evidenceOutput)!);
        File.Copy(output, evidenceOutput, overwrite: false);
        File.Copy(output + ".json", evidenceOutput + ".json", overwrite: false);
        File.WriteAllText(evidenceOutput + ".rejection.json", JsonSerializer.Serialize(new
        {
            schema = "hybridcpu.unqualified-loader-evidence/v1",
            publishQualified = false,
            publishFailure = failure,
            derivedRequiredWorkstreams = derivedRequirements,
            imageSha256 = image.PackageSha256,
            imageBytes = image.PackageLength
        }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static void AddProcessPair(ProcessStartInfo start, string name, string value)
    {
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(value);
    }

    private static string QuoteResponseArgument(string value) =>
        '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private static ManagedBodyWorldModuleV1[] ReadMetadataInputs()
    {
        string? json = Environment.GetEnvironmentVariable(MetadataInputsEnvironment);
        if (string.IsNullOrWhiteSpace(json)) return [];
        if (json.Length > 65536) throw new InvalidDataException("Input list exceeds its byte-independent bound.");
        MetadataInput[] inputs = JsonSerializer.Deserialize<MetadataInput[]>(json) ?? throw new InvalidDataException("Missing input list.");
        if (inputs.Length > 64 || inputs.Any(input => input is null || string.IsNullOrWhiteSpace(input.Path) ||
                string.IsNullOrWhiteSpace(input.Sha256)) ||
            inputs.Select(input => Path.GetFullPath(input.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != inputs.Length)
            throw new InvalidDataException("Malformed, duplicate or excessive metadata inputs.");
        long total = 0;
        List<ManagedBodyWorldModuleV1> modules = new List<ManagedBodyWorldModuleV1>();
        foreach (MetadataInput input in inputs)
        {
            total = checked(total + new FileInfo(input.Path).Length);
            if (total > 32 * 1024 * 1024) throw new InvalidDataException("Metadata PE budget exhausted.");
            byte[] bytes = File.ReadAllBytes(input.Path);
            string digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (digest != input.Sha256 || !IsManagedPe(bytes)) throw new InvalidDataException("Metadata PE/digest mismatch.");
            modules.Add(new(bytes, $"metadata-only:{digest}"));
        }
        return modules.ToArray();
    }

    private static bool IsManagedPe(byte[] bytes)
    {
        try
        {
            using MemoryStream stream = new MemoryStream(bytes, writable: false);
            using PEReader pe = new PEReader(stream);
            return pe.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static NativeAotBodyPresentationV1 CreateBodyPresentation(
        string assembly,
        string rootType,
        string rootMethod,
        string? presentedMethods)
    {
        using FileStream stream = File.OpenRead(assembly);
        using PEReader pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata) throw new ArgumentException("The application assembly has no managed metadata.");
        MetadataReader metadata = pe.GetMetadataReader();
        List<NativeAotPresentedBodyV1> bodies = new List<NativeAotPresentedBodyV1>();
        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
            string ns = metadata.GetString(type.Namespace);
            string typeName = string.IsNullOrEmpty(ns) ? metadata.GetString(type.Name) : $"{ns}.{metadata.GetString(type.Name)}";
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0) continue;
                bodies.Add(new(typeName, metadata.GetString(method.Name), MetadataTokens.GetToken(methodHandle)));
            }
        }
        NativeAotPresentedBodyV1[] roots = bodies.Where(body => body.TypeName == rootType && body.MethodName == rootMethod).ToArray();
        if (roots.Length != 1)
            throw new ArgumentException(roots.Length == 0
                ? "The configured root method has no exact managed body."
                : "The configured root method is overloaded; an exact token-bearing root contract is required.");
        foreach (string item in (presentedMethods ?? string.Empty).Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = item.Split("::", StringSplitOptions.None);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                throw new ArgumentException("Presented methods must use the exact semicolon-separated Type::Method syntax.");
            if (!bodies.Any(body => body.TypeName == parts[0] && body.MethodName == parts[1]))
                throw new ArgumentException($"Presented method '{item}' has no concrete body in the application assembly.");
        }
        NativeAotPresentedBodyV1[] ordered = bodies.Distinct()
            .OrderBy(static body => body.TypeName, StringComparer.Ordinal)
            .ThenBy(static body => body.MethodName, StringComparer.Ordinal)
            .ThenBy(static body => body.MetadataToken).ToArray();
        string assemblySha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))).ToLowerInvariant();
        NativeAotBodyPresentationV1 draft = new NativeAotBodyPresentationV1(NativeAotBodyPresentationV1.Schema,
            NativeAotBodyPresentationV1.Version, ScalarControlFlowV2ProfileContractV1.ProfileId,
            assemblySha, roots[0], ordered, string.Empty);
        return draft with { ContractDigest = ComputePresentationDigest(draft) };
    }

    private static string ComputePresentationDigest(NativeAotBodyPresentationV1 presentation) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('|',
            presentation.SchemaId, presentation.SchemaVersion, presentation.ProfileId,
            presentation.AssemblySha256,
            $"{presentation.Root.TypeName}::{presentation.Root.MethodName}:0x{presentation.Root.MetadataToken:x8}",
            string.Join(';', presentation.PresentedBodies.Select(static body =>
                $"{body.TypeName}::{body.MethodName}:0x{body.MetadataToken:x8}")))))).ToLowerInvariant();

    private static NativeAotSeamRequestV1? Parse(string[] args, out NativeAotSeamDiagnosticV1? error)
    {
        error = null;
        if (args.Length != 13 || args[0] is not ("compile" or "compile-object" or "compile-image"))
        {
            error = new("HCNAOT0001", "expected: compile|compile-object|compile-image --assembly A --type T --method M --out O --source-commit S --patch-digest P");
            return null;
        }

        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[index], args[index + 1]))
            {
                error = new("HCNAOT0002", "arguments must be unique named pairs");
                return null;
            }
        }

        string[] required = ["--assembly", "--type", "--method", "--out", "--source-commit", "--patch-digest"];
        if (values.Count != required.Length || required.Any(key => !values.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value)))
        {
            error = new("HCNAOT0003", "one or more required arguments are missing");
            return null;
        }

        NativeAotSeamOutputKindV1 outputKind = args[0] switch
        {
            "compile-object" => NativeAotSeamOutputKindV1.RelocatableObject,
            "compile-image" => NativeAotSeamOutputKindV1.RestrictedImage,
            _ => NativeAotSeamOutputKindV1.RawCode
        };
        return new(outputKind, values["--assembly"], values["--type"], values["--method"], values["--out"],
            values["--source-commit"], values["--patch-digest"]);
    }

}


