using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Matrix;

namespace HybridCPU.Compiler.NativeAot;

/// <summary>
/// Conservative, non-authoritative PCL projection for restricted single-method and
/// adapter-presented managed-graph seams. It snapshots actual validated canonical IR and
/// derives only facts represented by that IR. Unsupported descriptor sidebands fail closed
/// instead of being omitted.
/// </summary>
internal static class SingNextPclCanonicalIrV1
{
    private const int MaximumSnapshotBytes = 16 * 1024 * 1024;
    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumExactRegions = 64;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    internal static bool TryMaterialize(
        SingNextPclEmissionContextV1? context,
        IrProgram program,
        out string? diagnostic)
    {
        diagnostic = null;
        if (context is null || !context.DeriveFromCanonicalIr) return true;
        try
        {
            ValidateProgram(program);

            byte[] snapshot = Snapshot(program);
            byte[] manifest = Manifest(program);
            if (snapshot.Length == 0 || snapshot.Length > MaximumSnapshotBytes)
                throw new InvalidDataException("canonical IR snapshot is empty or exceeds its bound");
            if (manifest.Length == 0 || manifest.Length > MaximumManifestBytes)
                throw new InvalidDataException("derived PCL fact manifest is empty or exceeds its bound");
            WriteAtomic(context.InputIrPath, snapshot);
            try { WriteAtomic(context.ManifestPath, manifest); }
            catch
            {
                File.Delete(context.InputIrPath);
                throw;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            InvalidDataException or JsonException or NotSupportedException or OverflowException or
            UnauthorizedAccessException)
        {
            DeleteBestEffort(context.InputIrPath);
            DeleteBestEffort(context.ManifestPath);
            diagnostic = $"HCNAOT4102: compiler-derived PCL inputs failed closed: {exception.Message}";
            return false;
        }
    }

    internal static bool TryMaterializeGraph(
        SingNextPclEmissionContextV1? context,
        IReadOnlyList<ManagedCompiledMethodV1> methods,
        string graphDigest,
        string rootIdentity,
        out string? diagnostic)
    {
        diagnostic = null;
        if (context is null || !context.DeriveFromCanonicalIr) return true;
        try
        {
            if (methods is null || methods.Count == 0 || methods.Count > 256 ||
                graphDigest.Length != 64 || graphDigest.Any(static value => !Uri.IsHexDigit(value)) ||
                string.IsNullOrWhiteSpace(rootIdentity))
                throw new InvalidDataException("managed graph PCL identity or method count is invalid");
            ManagedCompiledMethodV1[] ordered = methods
                .OrderBy(static method => method.Identity.StableIdentity, StringComparer.Ordinal).ToArray();
            if (ordered.Select(static method => method.Identity.StableIdentity)
                    .Distinct(StringComparer.Ordinal).Count() != ordered.Length ||
                !ordered.Any(method => string.Equals(method.Identity.StableIdentity,
                    rootIdentity, StringComparison.Ordinal)))
                throw new InvalidDataException("managed graph PCL methods are duplicate or omit the exact root");

            List<object> snapshots = new List<object>(ordered.Length);
            List<DerivedFootprint> footprints = new List<DerivedFootprint>();
            List<DerivedAlias> aliases = new List<DerivedAlias>();
            List<DerivedOrdering> ordering = new List<DerivedOrdering>();
            List<DerivedNumeric> numeric = new List<DerivedNumeric>();
            List<DerivedSafePoint> safePoints = new List<DerivedSafePoint>();
            ulong instructionCount = 0;
            ulong blockCount = 0;
            ulong maximumOperands = 0;
            foreach (ManagedCompiledMethodV1 method in ordered)
            {
                IrProgram program = method.Import.Program
                    ?? throw new InvalidDataException("managed graph method has no canonical IR");
                if (method.Import.Status != RestrictedCilImportStatusV1.Success)
                    throw new InvalidDataException("managed graph method import is not successful");
                ValidateProgram(program);
                byte[] snapshot = Snapshot(program);
                string prefix = "m-" + HashToken(method.Identity.StableIdentity) + "-";
                snapshots.Add(new
                {
                    MethodIdentity = method.Identity.StableIdentity,
                    CanonicalIrSha256 = Convert.ToHexStringLower(SHA256.HashData(snapshot)),
                    CanonicalIrBase64 = Convert.ToBase64String(snapshot),
                });
                DerivedManifest facts = JsonSerializer.Deserialize<DerivedManifest>(Manifest(program), Json)
                    ?? throw new InvalidDataException("managed graph method facts are absent");
                footprints.AddRange(facts.Footprints.Select(fact => fact with
                { Symbol = prefix + fact.Symbol }));
                aliases.AddRange(facts.AliasFacts.Select(fact => fact with
                { LeftSymbol = prefix + fact.LeftSymbol, RightSymbol = prefix + fact.RightSymbol }));
                ordering.AddRange(facts.OrderingFacts.Select(fact => fact with
                { ConstraintIdentity = prefix + fact.ConstraintIdentity }));
                numeric.AddRange(facts.NumericFacts.Select(fact => fact with
                { OperationClass = prefix + fact.OperationClass }));
                safePoints.AddRange(facts.SafePointMap.Select(fact => fact with
                { SafePointIdentity = prefix + fact.SafePointIdentity }));
                instructionCount = checked(instructionCount + (ulong)program.Instructions.Count);
                blockCount = checked(blockCount + (ulong)Math.Max(1, program.BasicBlocks.Count));
                maximumOperands = Math.Max(maximumOperands, checked((ulong)Math.Max(1,
                    program.Instructions.Max(static instruction =>
                        instruction.Annotation.Defs.Count + instruction.Annotation.Uses.Count))));
            }

            byte[] aggregateSnapshot = TerminatedJson(new
            {
                SchemaId = "hybridcpu.singnext-pcl-canonical-ir-graph/v1",
                GraphDigest = graphDigest.ToLowerInvariant(),
                RootIdentity = rootIdentity,
                Methods = snapshots,
            });
            DerivedManifest graphManifest = new DerivedManifest(
                "hybridcpu-nativeaot-canonical-ir-graph-v1", footprints, aliases,
                ordering, numeric, safePoints,
                [new("canonical-basic-block-count", blockCount, "blocks"),
                 new("canonical-instruction-count", instructionCount, "instructions"),
                 new("maximum-static-operands-per-instruction", maximumOperands, "operands")]);
            byte[] manifest = TerminatedJson(graphManifest);
            if (aggregateSnapshot.Length == 0 || aggregateSnapshot.Length > MaximumSnapshotBytes)
                throw new InvalidDataException("managed graph canonical IR snapshot is empty or exceeds its bound");
            if (manifest.Length == 0 || manifest.Length > MaximumManifestBytes)
                throw new InvalidDataException("managed graph PCL fact manifest is empty or exceeds its bound");
            WriteAtomic(context.InputIrPath, aggregateSnapshot);
            try { WriteAtomic(context.ManifestPath, manifest); }
            catch
            {
                File.Delete(context.InputIrPath);
                throw;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            InvalidDataException or JsonException or NotSupportedException or OverflowException or
            UnauthorizedAccessException)
        {
            DeleteBestEffort(context.InputIrPath);
            DeleteBestEffort(context.ManifestPath);
            diagnostic = $"HCNAOT4103: compiler-derived managed-graph PCL inputs failed closed: {exception.Message}";
            return false;
        }
    }

    private static void ValidateProgram(IrProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        IrFrontendAdapterResultV1 validation = CanonicalIrFrontendBoundaryV1.Validate(program);
        if (validation.Status != IrFrontendAdapterStatus.Success || validation.Program is null)
            throw new InvalidDataException("canonical IR validation failed before evidence derivation");
        if (program.Instructions.Count == 0 ||
            program.Instructions.Select(static instruction => instruction.Index).Distinct().Count() != program.Instructions.Count ||
            program.Instructions.Select(static instruction => instruction.StableIdentity).Distinct(StringComparer.Ordinal).Count() != program.Instructions.Count)
            throw new InvalidDataException("canonical IR instruction identities are empty or non-unique");
        foreach (IrInstruction instruction in program.Instructions)
        {
            ValidateTypedLoweringSideband(instruction);
            ValidateExternalDescriptorEvidence(instruction);
            if (instruction.ExternalDescriptorEvidence is null)
                ValidateExactMemoryEffect(instruction);
        }
    }

    private static byte[] TerminatedJson<T>(T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        byte[] result = new byte[checked(json.Length + 1)];
        json.CopyTo(result, 0);
        result[^1] = (byte)'\n';
        return result;
    }

    private static byte[] Snapshot(IrProgram program)
    {
        var projection = new
        {
            SchemaId = "hybridcpu.singnext-pcl-canonical-ir/v1",
            Program = new
            {
                program.VirtualThreadId,
                Contract = new
                {
                    program.Contract.SchemaId,
                    program.Contract.SchemaVersion,
                    RequiredCapabilities = program.Contract.RequiredCapabilities.Order(StringComparer.Ordinal).ToArray(),
                    ProgramMutation = program.Contract.DerivedFacts.ProgramMutation.Value,
                },
                Instructions = program.Instructions.OrderBy(static instruction => instruction.Index).Select(static instruction => new
                {
                    instruction.Index,
                    instruction.VirtualThreadId,
                    instruction.EncodedAddress,
                    Opcode = (ushort)instruction.Opcode,
                    DataType = (byte)instruction.DataType,
                    instruction.PredicateMask,
                    instruction.Immediate,
                    instruction.StreamLength,
                    instruction.Stride,
                    instruction.RowStride,
                    instruction.Indexed,
                    instruction.Is2D,
                    instruction.Reduction,
                    instruction.TailAgnostic,
                    instruction.MaskAgnostic,
                    Operands = instruction.Operands.Select(static operand => new
                    {
                        Kind = (byte)operand.Kind,
                        operand.Value,
                        operand.Name,
                    }).ToArray(),
                    Annotation = new
                    {
                        ResourceClass = (byte)instruction.Annotation.ResourceClass,
                        LatencyClass = (byte)instruction.Annotation.LatencyClass,
                        instruction.Annotation.MinimumLatencyCycles,
                        StructurallyAllowedSlots = (byte)instruction.Annotation.StructurallyAllowedSlots,
                        Serialization = (byte)instruction.Annotation.Serialization,
                        StructuralResources = (ushort)instruction.Annotation.StructuralResources,
                        ControlFlowKind = (byte)instruction.Annotation.ControlFlowKind,
                        instruction.Annotation.IsBarrierLike,
                        instruction.Annotation.MayTrap,
                        instruction.Annotation.EncodedBranchTarget,
                        instruction.Annotation.ResolvedBranchTargetInstructionIndex,
                        instruction.Annotation.MemoryReadRegion,
                        instruction.Annotation.MemoryWriteRegion,
                        Defs = instruction.Annotation.Defs.Select(static operand => new
                        {
                            Kind = (byte)operand.Kind, operand.Value, operand.Name,
                        }).ToArray(),
                        Uses = instruction.Annotation.Uses.Select(static operand => new
                        {
                            Kind = (byte)operand.Kind, operand.Value, operand.Name,
                        }).ToArray(),
                        RequiredSlotClass = (byte)instruction.Annotation.RequiredSlotClass,
                        BindingKind = (byte)instruction.Annotation.BindingKind,
                        instruction.Annotation.DomainTag,
                        instruction.Annotation.StealabilityHint,
                        instruction.Annotation.BranchTargetSymbolName,
                        instruction.Annotation.FixedFrameSlotIdentity,
                        instruction.Annotation.IsManagedGcSafepoint,
                    },
                    InstructionClass = (byte)instruction.InstructionClass,
                    SerializationClass = (byte)instruction.SerializationClass,
                    instruction.StableIdentity,
                    CanonicalType = instruction.CanonicalType,
                    Semantics = instruction.Semantics,
                    OriginChain = instruction.OriginChain,
                    SideEffects = instruction.SideEffects with { Memory = EffectiveMemoryEffect(instruction) },
                    TypedLoweringSideband = TypedLoweringSideband(instruction),
                    instruction.ExternalDescriptorEvidence,
                    instruction.SourceSpan,
                }).ToArray(),
                Blocks = program.BasicBlocks.OrderBy(static block => block.Id).Select(static block => new
                {
                    block.Id,
                    block.StartInstructionIndex,
                    block.EndInstructionIndex,
                    block.StartAddress,
                    block.EndAddress,
                    block.HasUnresolvedControlTransfer,
                    InstructionIndexes = block.Instructions.Select(static instruction => instruction.Index).ToArray(),
                    Predecessors = block.PredecessorBlockIds.Order().ToArray(),
                    Successors = block.SuccessorBlockIds.Order().ToArray(),
                    block.ExitBlock,
                    block.BarrierBoundary,
                    block.PrimaryLabel,
                    Labels = block.LabelNames.Order(StringComparer.Ordinal).ToArray(),
                    block.SectionName,
                    block.FunctionName,
                    block.SourceSpan,
                }).ToArray(),
                Edges = program.ControlFlowGraph.Edges
                    .OrderBy(static edge => edge.SourceBlockId)
                    .ThenBy(static edge => edge.TargetBlockId)
                    .ThenBy(static edge => edge.Kind)
                    .ToArray(),
                Labels = program.Labels.OrderBy(static row => row.Name, StringComparer.Ordinal).ToArray(),
                EntryPoints = program.EntryPoints.OrderBy(static row => row.Name, StringComparer.Ordinal).ToArray(),
                Sections = program.Sections.OrderBy(static row => row.Name, StringComparer.Ordinal).ToArray(),
                Functions = program.Functions.OrderBy(static row => row.Name, StringComparer.Ordinal).ToArray(),
                ValueFlow = new
                {
                    program.ValueFlow.SchemaId,
                    program.ValueFlow.SchemaVersion,
                    Values = program.ValueFlow.Values.OrderBy(static row => row.StableId, StringComparer.Ordinal).ToArray(),
                    Accesses = program.ValueFlow.Accesses.OrderBy(static row => row.InstructionIndex)
                        .ThenBy(static row => row.ValueId, StringComparer.Ordinal).ThenBy(static row => row.Kind).ToArray(),
                },
                FrontendEvidence = new
                {
                    program.FrontendEvidence.SchemaId,
                    program.FrontendEvidence.SchemaVersion,
                    Facts = program.FrontendEvidence.Facts.OrderBy(static row => row.StableIdentity, StringComparer.Ordinal)
                        .ThenBy(static row => row.Kind).ToArray(),
                    program.FrontendEvidence.Digest,
                },
            },
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(projection, Json);
        byte[] result = new byte[checked(json.Length + 1)];
        json.CopyTo(result, 0);
        result[^1] = (byte)'\n';
        return result;
    }

    private static byte[] Manifest(IrProgram program)
    {
        Dictionary<RegionKey, byte> regions = new Dictionary<RegionKey, byte>();
        foreach (IrInstruction instruction in program.Instructions.OrderBy(static row => row.Index))
        {
            if (instruction.ExternalDescriptorEvidence is { } descriptorEvidence)
            {
                foreach (IrMemoryRegion region in descriptorEvidence.ReadRegions)
                    AddRegion(IrAddressSpaceIdentity.Generic, region, 1);
                foreach (IrMemoryRegion region in descriptorEvidence.WriteRegions)
                    AddRegion(IrAddressSpaceIdentity.Generic, region, 2);
                continue;
            }
            IrCanonicalMemoryEffectV1 memory = EffectiveMemoryEffect(instruction);
            if (memory.Ordering == IrMemoryOrdering.Unknown)
                throw new InvalidDataException("canonical IR contains unknown memory ordering");
            AddRegion(memory.AddressSpace, memory.ReadRegion, 1);
            AddRegion(memory.AddressSpace, memory.WriteRegion, 2);
        }
        if (regions.Count > MaximumExactRegions)
            throw new InvalidDataException("exact memory-region fact count exceeds its bound");

        DerivedFootprint[] footprints = regions
            .OrderBy(static row => row.Key.AddressSpace).ThenBy(static row => row.Key.Address)
            .ThenBy(static row => row.Key.Length)
            .Select(static row => new DerivedFootprint(Symbol(row.Key), 0, row.Key.Length, row.Value))
            .ToArray();
        List<DerivedAlias> aliases = new List<DerivedAlias>();
        RegionKey[] keys = regions.Keys.OrderBy(static row => row.AddressSpace)
            .ThenBy(static row => row.Address).ThenBy(static row => row.Length).ToArray();
        for (int left = 0; left < keys.Length; left++)
        for (int right = left + 1; right < keys.Length; right++)
        {
            if (keys[left].AddressSpace != keys[right].AddressSpace) continue;
            ulong leftEnd = checked(keys[left].Address + keys[left].Length);
            ulong rightEnd = checked(keys[right].Address + keys[right].Length);
            if (leftEnd <= keys[right].Address || rightEnd <= keys[left].Address)
                aliases.Add(new(Symbol(keys[left]), Symbol(keys[right]), true));
        }
        DerivedOrdering[] ordering = program.Instructions
            .Where(static instruction => EffectiveMemoryEffect(instruction).Ordering is not IrMemoryOrdering.NotAtomic)
            .OrderBy(static instruction => instruction.Index)
            .Select(static instruction => new DerivedOrdering(
                "ir-" + HashToken(instruction.StableIdentity), true,
                EffectiveMemoryEffect(instruction).Kind.HasFlag(IrMemoryEffectKind.Fence) ||
                instruction.SideEffects.ArchitecturalEffects.HasFlag(IrArchitecturalEffectKind.Fence)))
            .ToArray();
        DerivedNumeric[] numeric = program.Instructions
            .Where(static instruction => instruction.CanonicalType.Kind == IrCanonicalValueKind.Integer)
            .OrderBy(static instruction => instruction.Index)
            .Select(static instruction => Numeric(instruction))
            .ToArray();
        DerivedSafePoint[] safePoints = program.Instructions
            .Where(static instruction => instruction.Annotation.IsManagedGcSafepoint)
            .OrderBy(static instruction => instruction.Index)
            .Select(static instruction => new DerivedSafePoint(
                "ir-" + HashToken(instruction.StableIdentity),
                instruction.EncodedAddress,
                LiveStateDigest(instruction)))
            .ToArray();
        DerivedResourceEstimate[] resourceEstimates =
        [
            new("canonical-basic-block-count", checked((ulong)Math.Max(1, program.BasicBlocks.Count)), "blocks"),
            new("canonical-instruction-count", checked((ulong)program.Instructions.Count), "instructions"),
            new("maximum-static-operands-per-instruction", checked((ulong)Math.Max(1,
                program.Instructions.Max(static instruction =>
                    instruction.Annotation.Defs.Count + instruction.Annotation.Uses.Count))), "operands"),
        ];
        DerivedManifest manifest = new DerivedManifest(
            "hybridcpu-nativeaot-canonical-ir-v1", footprints, aliases, ordering, numeric,
            safePoints, resourceEstimates);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        byte[] result = new byte[checked(json.Length + 1)];
        json.CopyTo(result, 0);
        result[^1] = (byte)'\n';
        return result;

        void AddRegion(IrAddressSpaceIdentity addressSpace, IrMemoryRegion? region, byte access)
        {
            if (region is null) return;
            if (region.Length == 0 || region.Address > ulong.MaxValue - region.Length)
                throw new InvalidDataException("canonical IR exact memory region is invalid");
            RegionKey key = new RegionKey(addressSpace, region.Address, region.Length);
            regions[key] = (byte)(regions.GetValueOrDefault(key) | access);
        }
    }

    private static void ValidateExactMemoryEffect(IrInstruction instruction)
    {
        IrCanonicalMemoryEffectV1 memory = EffectiveMemoryEffect(instruction);
        const IrMemoryEffectKind supported = IrMemoryEffectKind.Read | IrMemoryEffectKind.Write |
            IrMemoryEffectKind.Atomic | IrMemoryEffectKind.Volatile | IrMemoryEffectKind.Fence;
        if ((memory.Kind & ~supported) != 0 || memory.AddressSpace == IrAddressSpaceIdentity.Unknown ||
            memory.Ordering == IrMemoryOrdering.Unknown)
            throw new InvalidDataException("canonical IR contains an unsupported or unknown memory effect");

        bool reads = memory.Kind.HasFlag(IrMemoryEffectKind.Read);
        bool writes = memory.Kind.HasFlag(IrMemoryEffectKind.Write);
        if (reads != (memory.ReadRegion is not null) || writes != (memory.WriteRegion is not null))
            throw new InvalidDataException("canonical IR memory effects require complete exact read/write regions");
        if (memory.ReadRegion is { IsWrite: true } || memory.WriteRegion is { IsWrite: false })
            throw new InvalidDataException("canonical IR memory-region access direction is inconsistent");
        if (!Equals(instruction.Annotation.MemoryReadRegion, memory.ReadRegion) ||
            !Equals(instruction.Annotation.MemoryWriteRegion, memory.WriteRegion))
            throw new InvalidDataException("canonical IR annotation and side-effect memory regions disagree");
        if ((memory.Kind & (IrMemoryEffectKind.Atomic | IrMemoryEffectKind.Volatile | IrMemoryEffectKind.Fence)) != 0 &&
            memory.Ordering == IrMemoryOrdering.NotAtomic)
            throw new InvalidDataException("atomic, volatile, or fence effects require explicit ordering");
    }

    private static void ValidateTypedLoweringSideband(IrInstruction instruction)
    {
        if (instruction.MatrixTileEmission is not null && instruction.VectorTransferEmission is not null)
            throw new InvalidDataException("canonical IR instruction has multiple typed lowering sidebands");
        if (instruction.MatrixTileEmission is { } matrix)
        {
            matrix.Request.Descriptor.Validate(nameof(matrix.Request.Descriptor));
            if (matrix.Request.Opcode != instruction.Opcode ||
                matrix.RuntimeHandoffRow.Opcode != instruction.Opcode ||
                matrix.RuntimeProjection.Opcode != instruction.Opcode ||
                matrix.RuntimeMaterialization.Opcode != instruction.Opcode ||
                matrix.EncodedInstruction.OpCode != (uint)instruction.Opcode ||
                matrix.UsesFallbackPath || matrix.UsesAliasPromotion ||
                matrix.UsesScalarVectorDotOrBackendFallback)
                throw new InvalidDataException("matrix-tile typed lowering sideband is inconsistent or contains fallback");
            ValidateMatrixTileMemorySideband(instruction, matrix);
        }
        if (instruction.VectorTransferEmission is { } vector)
        {
            vector.Request.Shape.Validate(nameof(vector.Request.Shape));
            if (vector.Request.Opcode != instruction.Opcode ||
                vector.RuntimeOpcodeInfo.OpCode != (uint)instruction.Opcode ||
                vector.EncodedInstruction.OpCode != (uint)instruction.Opcode ||
                vector.RuntimeHandoffReference != CompilerVectorTransferPositiveEmissionAbiContract.RuntimeHandoffReference ||
                vector.UsesFallbackPath || vector.UsesAliasPromotion ||
                vector.UsesScalarVectorDotOrBackendFallback || vector.UsesBaseMemoryFallback ||
                vector.UsesBaseVectorFallback || vector.UsesScalarHelperFallback ||
                vector.UsesWideningFmaFallback || vector.UsesVectorTransposeOrSegmentFallback)
                throw new InvalidDataException("vector-transfer typed lowering sideband is inconsistent or contains fallback");
        }
    }

    private static void ValidateExternalDescriptorEvidence(IrInstruction instruction)
    {
        bool hasDsc = instruction.DmaStreamComputeDescriptor is not null;
        bool hasL7 = instruction.AcceleratorCommandDescriptor is not null;
        IrExternalDescriptorEvidenceV1? evidence = instruction.ExternalDescriptorEvidence;
        if (!hasDsc && !hasL7)
        {
            if (evidence is not null)
                throw new InvalidDataException("external descriptor evidence has no descriptor-bearing instruction");
            return;
        }
        if (hasDsc == hasL7 || evidence is null || evidence.DescriptorIdentityHash == 0 || !evidence.AllOrNone)
            throw new InvalidDataException("descriptor-bearing IR lacks one exact all-or-none compiler projection");
        string expectedSchema = hasDsc
            ? "hybridcpu.dsc-exact-footprint/v1"
            : "hybridcpu.l7-exact-footprint/v1";
        HybridCpuOpcode expectedOpcode = hasDsc ? HybridCpuOpcode.DmaStreamCompute : HybridCpuOpcode.ACCEL_SUBMIT;
        if (!string.Equals(evidence.SchemaId, expectedSchema, StringComparison.Ordinal) ||
            instruction.Opcode != expectedOpcode || evidence.ReadRegions.Count == 0 ||
            evidence.WriteRegions.Count == 0 || instruction.Annotation.MemoryReadRegion is not null ||
            instruction.Annotation.MemoryWriteRegion is not null)
            throw new InvalidDataException("external descriptor evidence disagrees with its canonical instruction");
        IrMemoryEffectKind kind = instruction.SideEffects.Memory.Kind;
        if ((kind & (IrMemoryEffectKind.Atomic | IrMemoryEffectKind.Volatile |
                     IrMemoryEffectKind.Fence | IrMemoryEffectKind.Unknown)) != 0)
            throw new InvalidDataException("external descriptor canonical memory classification is unsupported");
        foreach (IrMemoryRegion region in evidence.ReadRegions)
            ValidateDescriptorRegion(region, isWrite: false);
        foreach (IrMemoryRegion region in evidence.WriteRegions)
            ValidateDescriptorRegion(region, isWrite: true);
    }

    private static void ValidateDescriptorRegion(IrMemoryRegion region, bool isWrite)
    {
        if (region.IsWrite != isWrite || region.Length == 0 ||
            region.Address > ulong.MaxValue - region.Length)
            throw new InvalidDataException("external descriptor exact memory region is invalid");
    }

    private static IrCanonicalMemoryEffectV1 EffectiveMemoryEffect(IrInstruction instruction)
    {
        if (instruction.MatrixTileEmission is not { } matrix)
            return instruction.SideEffects.Memory;
        CompilerMatrixTilePositiveEmissionKind kind = matrix.Request.Kind;
        if ((instruction.SideEffects.Memory.Kind & (IrMemoryEffectKind.Atomic |
                IrMemoryEffectKind.Volatile | IrMemoryEffectKind.Fence | IrMemoryEffectKind.Unknown)) != 0)
            throw new InvalidDataException("matrix-tile memory lowering sideband cannot refine ordered or unknown memory effects");
        if (kind is CompilerMatrixTilePositiveEmissionKind.MtileMacc or
            CompilerMatrixTilePositiveEmissionKind.Mtranspose)
            return IrCanonicalMemoryEffectV1.None;
        bool writes = kind == CompilerMatrixTilePositiveEmissionKind.MtileStore;
        return new IrCanonicalMemoryEffectV1(
            writes ? IrMemoryEffectKind.Write : IrMemoryEffectKind.Read,
            IrAddressSpaceIdentity.Generic,
            IrMemoryOrdering.NotAtomic,
            writes ? null : instruction.Annotation.MemoryReadRegion,
            writes ? instruction.Annotation.MemoryWriteRegion : null);
    }

    private static void ValidateMatrixTileMemorySideband(
        IrInstruction instruction,
        CompilerMatrixTileEmissionPlan matrix)
    {
        CompilerMatrixTileEmissionRequest request = matrix.Request;
        bool reads = request.Kind == CompilerMatrixTilePositiveEmissionKind.MtileLoad;
        bool writes = request.Kind == CompilerMatrixTilePositiveEmissionKind.MtileStore;
        if (!reads && !writes)
        {
            if (instruction.Annotation.MemoryReadRegion is not null ||
                instruction.Annotation.MemoryWriteRegion is not null)
                throw new InvalidDataException("matrix-tile compute lowering sideband unexpectedly carries external memory regions");
            return;
        }

        MatrixTileMemoryShapeValidationResult validation = matrix.MemoryValidation
            ?? throw new InvalidDataException("matrix-tile memory lowering sideband is missing validated footprint");
        ulong length = validation.TotalByteFootprint == 0 ? 1UL : validation.TotalByteFootprint;
        if (length > uint.MaxValue || request.MemoryFaultAbi is not { } memoryFaultAbi)
            throw new InvalidDataException("matrix-tile memory lowering sideband has an unsupported footprint");
        IrMemoryRegion expected = new IrMemoryRegion(memoryFaultAbi.BaseAddress, (uint)length, writes);
        if ((reads && (!Equals(instruction.Annotation.MemoryReadRegion, expected) ||
                       instruction.Annotation.MemoryWriteRegion is not null)) ||
            (writes && (!Equals(instruction.Annotation.MemoryWriteRegion, expected) ||
                        instruction.Annotation.MemoryReadRegion is not null)))
            throw new InvalidDataException("matrix-tile memory lowering sideband disagrees with canonical exact memory regions");
    }

    private static object? TypedLoweringSideband(IrInstruction instruction)
    {
        if (instruction.MatrixTileEmission is { } matrix)
            return new
            {
                SchemaId = "hybridcpu.matrix-tile-typed-lowering/v1",
                Request = matrix.Request,
                matrix.RuntimeHandoffRow,
                EncodedInstruction = EncodedInstruction(matrix.EncodedInstruction),
                matrix.RuntimeProjection,
                matrix.RuntimeMaterialization,
                matrix.UsesFallbackPath,
                matrix.UsesAliasPromotion,
                matrix.UsesScalarVectorDotOrBackendFallback,
            };
        if (instruction.VectorTransferEmission is { } vector)
            return new
            {
                SchemaId = "hybridcpu.vector-transfer-typed-lowering/v1",
                Request = vector.Request,
                vector.RuntimeHandoffReference,
                vector.RuntimeOpcodeInfo,
                EncodedInstruction = EncodedInstruction(vector.EncodedInstruction),
                vector.UsesFallbackPath,
                vector.UsesAliasPromotion,
                vector.UsesScalarVectorDotOrBackendFallback,
                vector.UsesBaseMemoryFallback,
                vector.UsesBaseVectorFallback,
                vector.UsesScalarHelperFallback,
                vector.UsesWideningFmaFallback,
                vector.UsesVectorTransposeOrSegmentFallback,
            };
        return null;
    }

    private static object EncodedInstruction(HybridCpuInstructionWord instruction) => new
    {
        instruction.OpCode,
        DataType = (byte)instruction.DataTypeValue,
        instruction.PredicateMask,
        instruction.Immediate,
        instruction.DestSrc1Pointer,
        instruction.Src2Pointer,
        instruction.StreamLength,
        instruction.Stride,
        instruction.RowStride,
        instruction.VirtualThreadId,
        instruction.Indexed,
        instruction.Is2D,
        instruction.Reduction,
        instruction.TailAgnostic,
        instruction.MaskAgnostic,
    };

    private static DerivedNumeric Numeric(IrInstruction instruction)
    {
        int width = instruction.CanonicalType.BitWidth;
        if (width <= 0 || width > 4096 || (width & (width - 1)) != 0)
            throw new InvalidDataException("canonical integer width is not a bounded power of two");
        string overflow = instruction.Semantics.IntegerOverflow switch
        {
            IrIntegerOverflowSemantics.Wrap => "wrap",
            IrIntegerOverflowSemantics.Trap => "trap",
            IrIntegerOverflowSemantics.SourcePoison => "source-poison",
            IrIntegerOverflowSemantics.NotApplicable => "not-applicable",
            _ => throw new InvalidDataException("canonical integer overflow semantics are unknown"),
        };
        return new("ir-" + HashToken(instruction.StableIdentity), "not-applicable", overflow, checked((ushort)width));
    }

    private static string LiveStateDigest(IrInstruction instruction)
    {
        var projection = new
        {
            instruction.StableIdentity,
            instruction.Annotation.FixedFrameSlotIdentity,
            Defs = instruction.Annotation.Defs
                .OrderBy(static operand => operand.Kind).ThenBy(static operand => operand.Value)
                .ThenBy(static operand => operand.Name, StringComparer.Ordinal)
                .Select(static operand => new { Kind = (byte)operand.Kind, operand.Value, operand.Name })
                .ToArray(),
            Uses = instruction.Annotation.Uses
                .OrderBy(static operand => operand.Kind).ThenBy(static operand => operand.Value)
                .ThenBy(static operand => operand.Name, StringComparer.Ordinal)
                .Select(static operand => new { Kind = (byte)operand.Kind, operand.Value, operand.Name })
                .ToArray(),
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(projection, Json)));
    }

    private static string Symbol(RegionKey key) =>
        $"memory-{(byte)key.AddressSpace}-{key.Address:x16}-{key.Length}";

    private static string HashToken(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(value)));

    private static void WriteAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("derived PCL path has no parent directory");
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }

    private static void DeleteBestEffort(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private readonly record struct RegionKey(
        IrAddressSpaceIdentity AddressSpace,
        ulong Address,
        uint Length);

    private sealed record DerivedManifest(
        string CompilerContractVersion,
        IReadOnlyList<DerivedFootprint> Footprints,
        IReadOnlyList<DerivedAlias> AliasFacts,
        IReadOnlyList<DerivedOrdering> OrderingFacts,
        IReadOnlyList<DerivedNumeric> NumericFacts,
        IReadOnlyList<DerivedSafePoint> SafePointMap,
        IReadOnlyList<DerivedResourceEstimate> StaticResourceEstimates);

    private readonly record struct DerivedFootprint(
        string Symbol, ulong Offset, ulong Length, byte Access);
    private readonly record struct DerivedAlias(
        string LeftSymbol, string RightSymbol, bool Disjoint);
    private readonly record struct DerivedOrdering(
        string ConstraintIdentity, bool Preserved, bool FenceEmitted);
    private readonly record struct DerivedNumeric(
        string OperationClass, string RoundingMode, string OverflowMode, ushort VectorWidthBits);
    private readonly record struct DerivedSafePoint(
        string SafePointIdentity, ulong EncodedAddress, string LiveStateDigest);
    private readonly record struct DerivedResourceEstimate(
        string ResourceIdentity, ulong UpperBound, string Unit);
}
