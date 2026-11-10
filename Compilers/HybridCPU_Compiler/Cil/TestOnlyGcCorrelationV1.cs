using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Managed;

namespace HybridCPU.Compiler.Cil;

/// <summary>
/// Opt-in test evidence only. This observer has no effect unless its explicit environment
/// variables are supplied, and it has no execution, publication, ISA or runtime authority.
/// </summary>
internal static class TestOnlyGcCorrelationV1
{
    internal const string OutputEnvironment = "HYBRIDCPU_TEST_GC_CORRELATION_PATH_V1";
    internal const string MethodEnvironment = "HYBRIDCPU_TEST_GC_CORRELATION_METHOD_V1";
    internal const string HashEnvironment = "HYBRIDCPU_TEST_GC_CORRELATION_HASH_V1";
    internal const string AllocationEnvironment = "HYBRIDCPU_TEST_GC_ALLOCATION_WITNESS_V1";

    internal static void ObserveImportedMethod(ManagedCompiledMethodV1 method)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OutputEnvironment)) &&
            method.Identity.StableIdentity.Contains("RenderEngine.InitTextures", StringComparison.Ordinal))
            Console.Error.WriteLine($"TEST-ONLY-GC-CORRELATION imported={method.Identity.StableIdentity}");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OutputEnvironment)) ||
            !string.Equals(Environment.GetEnvironmentVariable(MethodEnvironment), method.Identity.StableIdentity,
                StringComparison.Ordinal) || Environment.GetEnvironmentVariable(HashEnvironment)?.Length != 16)
            return;
        _ = ScalarControlFlowV2ObjectLinkerV1.CompileMethod(method, 0);
    }

    internal static void TryWrite(
        ManagedCompiledMethodV1 method,
        IrRegisterAllocationResultV1 allocation,
        HybridCpuManagedMetadataArtifactV1 metadata)
    {
        string? output = Environment.GetEnvironmentVariable(OutputEnvironment);
        if (string.IsNullOrWhiteSpace(output)) return;

        string? methodFilter = Environment.GetEnvironmentVariable(MethodEnvironment);
        string? hashFilter = Environment.GetEnvironmentVariable(HashEnvironment);
        if (string.IsNullOrWhiteSpace(methodFilter) || string.IsNullOrWhiteSpace(hashFilter) ||
            !string.Equals(method.Identity.StableIdentity, methodFilter, StringComparison.Ordinal) ||
            hashFilter.Length != 16 || allocation.Witness is null || allocation.OriginalSchedule is null ||
            allocation.FinalBundles is null)
            return;

        Dictionary<string, IrInstruction> instructions = allocation.OriginalSchedule.Program.Instructions
            .ToDictionary(static instruction => instruction.StableIdentity, StringComparer.Ordinal);
        Dictionary<string, int> codeOffsets = BuildCodeOffsets(allocation.FinalBundles);
        Dictionary<string, IrRegisterAssignmentV1> assignments = allocation.Witness.Assignments
            .ToDictionary(static assignment => assignment.ValueId, StringComparer.Ordinal);
        Dictionary<string, IrAllocatedSemanticValueV1> values = allocation.Witness.SemanticValues
            .ToDictionary(static value => value.ValueId, StringComparer.Ordinal);

        List<CorrelationRow> rows = new List<CorrelationRow>();
        foreach (HybridCpuSafepointRecordV1 safepoint in metadata.Safepoints)
        {
            IrInstruction? call = instructions.Values.SingleOrDefault(instruction =>
                instruction.Annotation.IsManagedGcSafepoint &&
                (instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call ||
                 instruction.SideEffects.ArchitecturalEffects.HasFlag(IrArchitecturalEffectKind.Call)) &&
                codeOffsets.TryGetValue(instruction.StableIdentity, out int offset) && offset == safepoint.CodeOffsetBytes);
            foreach (HybridCpuGcReferenceLocationV1 root in safepoint.LiveReferences)
            {
                string encodedHash = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(root.ValueIdentity)).AsSpan(0, 8));
                if (!string.Equals(encodedHash, hashFilter, StringComparison.OrdinalIgnoreCase)) continue;

                assignments.TryGetValue(root.ValueIdentity, out IrRegisterAssignmentV1? assignment);
                values.TryGetValue(root.ValueIdentity, out IrAllocatedSemanticValueV1? value);
                int[] useIndices = call?.Annotation.Uses.Select((operand, index) => (operand, index))
                    .Where(item => item.operand.Kind == IrOperandKind.VirtualValue &&
                        item.operand.Name == root.ValueIdentity).Select(static item => item.index).ToArray() ?? [];
                int[] definitionIndices = call?.Annotation.Defs.Select((operand, index) => (operand, index))
                    .Where(item => item.operand.Kind == IrOperandKind.VirtualValue &&
                        item.operand.Name == root.ValueIdentity).Select(static item => item.index).ToArray() ?? [];
                string role = useIndices.Length != 0 ? "call-use" : definitionIndices.Length != 0
                    ? "call-definition" : assignment?.LiveAcrossCall == true ? "live-across-call-nonoperand" : "live-at-call-nonoperand";
                rows.Add(new(safepoint.CodeOffsetBytes, root.ValueIdentity, encodedHash, ExtractCilOffset(root.ValueIdentity),
                    value?.ValueKind.Kind.ToString() ?? "Unavailable", value?.ValueKind.BitWidth,
                    value?.VirtualClass.ToString() ?? "Unavailable", root.LocationKind.ToString(),
                    root.RegisterId, root.StackOffsetBytes, assignment?.RegisterId,
                    assignment?.ScheduledStart, assignment?.ScheduledEndExclusive,
                    assignment?.LiveAcrossCall, call?.StableIdentity ?? "Unavailable",
                    call?.Opcode.ToString() ?? "Unavailable", role, useIndices, definitionIndices));
            }
        }

        CorrelationArtifact payload = new CorrelationArtifact(
            "hybridcpu.test-gc-correlation/v1", method.Identity.StableIdentity,
            method.Identity.AssemblyIdentity, method.Import.Provenance?.PeSha256 ?? "Unavailable",
            hashFilter.ToUpperInvariant(), rows.OrderBy(static row => row.NativeOffset).ThenBy(static row => row.ValueIdentity).ToArray(),
            "test-only;no-runtime-authority;no-execution-authority;no-publication-authority");
        var catalogue = values.Values.OrderBy(static value => value.ValueId, StringComparer.Ordinal).Select(value => new
        {
            value.ValueId,
            Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ValueId)).AsSpan(0, 8)),
            value.ValueKind,
            value.VirtualClass,
            Assignment = assignments.GetValueOrDefault(value.ValueId)
        }).ToArray();
        // A separate explicit opt-in keeps the existing/default diagnostic shape unchanged.
        object? allocationEvidence = null;
        if (Environment.GetEnvironmentVariable(AllocationEnvironment) == "1")
        {
            int bundleIndex = 0;
            List<object> bundles = new List<object>();
            foreach (IrBasicBlockBundlingResult? block in allocation.FinalBundles.BlockResults.OrderBy(static row => row.Block.StartInstructionIndex))
                foreach (IrMaterializedBundle? bundle in block.Bundles.OrderBy(static row => row.Cycle))
                    bundles.Add(new
                    {
                        NativeOffset = checked(bundleIndex++ * HybridCpuManagedMetadataContractV1.EncodedBundleBytes),
                        BlockId = block.Block.Id,
                        bundle.Cycle,
                        Slots = bundle.Slots.OrderBy(static slot => slot.SlotIndex).Select(slot => new
                        {
                            slot.SlotIndex,
                            Instruction = slot.Instruction is { } instruction ? new
                            {
                                instruction.Index, instruction.StableIdentity,
                                Opcode = instruction.Opcode.ToString(), instruction.Immediate,
                                instruction.Operands,
                                instruction.Annotation.Uses, instruction.Annotation.Defs,
                                instruction.Annotation.ControlFlowKind,
                                instruction.SideEffects
                            } : null
                        }).ToArray()
                    });
            allocationEvidence = new
            {
                Schema = "hybridcpu.test-final-allocation/v1",
                allocation.ResultDigest, allocation.Witness,
                OriginalInstructions = allocation.OriginalSchedule.Program.Instructions.Select(instruction => new
                {
                    instruction.Index, instruction.StableIdentity, Opcode = instruction.Opcode.ToString(),
                    instruction.Immediate, instruction.Operands,
                    instruction.Annotation.Uses, instruction.Annotation.Defs
                }).ToArray(),
                FinalBundles = bundles
            };
        }
        WriteAtomically(Path.GetFullPath(output), JsonSerializer.SerializeToUtf8Bytes(new { Correlation = payload, ValueCatalogue = catalogue,
            AllocationEvidence = allocationEvidence },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, int> BuildCodeOffsets(IrProgramBundlingResult bundles)
    {
        Dictionary<string, int> result = new Dictionary<string, int>(StringComparer.Ordinal);
        int bundleIndex = 0;
        foreach (IrBasicBlockBundlingResult block in bundles.BlockResults.OrderBy(static row => row.Block.StartInstructionIndex))
        {
            foreach (IrMaterializedBundle bundle in block.Bundles.OrderBy(static row => row.Cycle))
            {
                foreach (IrInstruction instruction in bundle.Slots.Where(static slot => slot.Instruction is not null)
                    .OrderBy(static slot => slot.SlotIndex).Select(static slot => slot.Instruction!))
                    result[instruction.StableIdentity] = checked(bundleIndex * HybridCpuManagedMetadataContractV1.EncodedBundleBytes);
                bundleIndex++;
            }
        }
        return result;
    }

    private static int? ExtractCilOffset(string identity)
    {
        int marker = identity.LastIndexOf(":il_", StringComparison.Ordinal);
        if (marker < 0) return null;
        int start = marker + 4;
        int end = identity.IndexOf(':', start);
        string value = end < 0 ? identity[start..] : identity[start..end];
        return int.TryParse(value, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out int offset) ? offset : null;
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record CorrelationArtifact(string Schema, string MethodIdentity, string AssemblyIdentity,
        string PeSha256, string RequestedIdentityHash, IReadOnlyList<CorrelationRow> Rows, string Authority);

    private sealed record CorrelationRow(int NativeOffset, string ValueIdentity, string EncodedIdentityHash, int? CilOffset,
        string CanonicalType, int? BitWidth, string VirtualClass, string LocationKind, int? MetadataRegisterId,
        int? StackOffsetBytes, int? AssignmentRegisterId, int? AssignmentStart, int? AssignmentEndExclusive,
        bool? LiveAcrossCall, string CallInstructionIdentity, string CallOpcode, string CallOperandRole,
        IReadOnlyList<int> CallUseIndices, IReadOnlyList<int> CallDefinitionIndices);
}
