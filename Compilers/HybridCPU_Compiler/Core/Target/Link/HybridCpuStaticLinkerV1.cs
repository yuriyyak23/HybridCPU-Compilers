using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;

namespace HybridCPU.Compiler.Core.Target.Link;

public sealed class HybridCpuStaticLinkerV1
{
    public HybridCpuStaticLinkArtifactV1 Link(
        IReadOnlyList<HybridCpuLinkInputV1> inputs,
        HybridCpuStaticLinkOptionsV1? options = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        options ??= HybridCpuStaticLinkOptionsV1.Production;
        if (!Equals(options, HybridCpuStaticLinkOptionsV1.Production))
            return Failure(HybridCpuLinkStatusV1.VersionSkew, "HCLINK1001", "Only the exact production link options are qualified.", options);
        if (inputs.Count is 0 || inputs.Count > options.MaximumInputs || inputs.Any(static input => input is null || input.ObjectBytes is null))
            return Failure(HybridCpuLinkStatusV1.Invalid, "HCLINK0001", "Link input count or object bytes are invalid.", options);

        HybridCpuLinkInputV1[] orderedInputs = inputs.OrderBy(static input => input.Identity, StringComparer.Ordinal).ToArray();
        if (orderedInputs.Any(static input => string.IsNullOrWhiteSpace(input.Identity) || input.Identity.IndexOf('\0') >= 0 ||
                Encoding.UTF8.GetByteCount(input.Identity) > 128) ||
            orderedInputs.Select(static input => input.Identity).Distinct(StringComparer.Ordinal).Count() != orderedInputs.Length)
            return Failure(HybridCpuLinkStatusV1.Invalid, "HCLINK0002", "Module identities are malformed or duplicated.", options);

        HybridCpuObjectWriterV1 objectWriter = new HybridCpuObjectWriterV1();
        List<Module> modules = new List<Module>(orderedInputs.Length);
        foreach (HybridCpuLinkInputV1 input in orderedInputs)
        {
            HybridCpuObjectArtifactV1 artifact = objectWriter.Inspect(input.ObjectBytes);
            if (artifact.Status != HybridCpuObjectStatusV1.Success)
            {
                HybridCpuLinkStatusV1 status = artifact.Status == HybridCpuObjectStatusV1.VersionSkew
                    ? HybridCpuLinkStatusV1.VersionSkew
                    : HybridCpuLinkStatusV1.CorruptInput;
                string detail = artifact.Diagnostics.Count == 0 ? artifact.Status.ToString() : artifact.Diagnostics[0].Code;
                return Failure(status, "HCLINK2001", $"Module '{input.Identity}' is not a canonical HCO v1 object ({detail}).", options);
            }
            modules.Add(new(input.Identity, artifact));
        }

        Contribution[] contributions = modules
            .SelectMany(static module => module.Artifact.Sections.Select(section => new Contribution(module, section)))
            .OrderBy(static contribution => contribution.Section.Kind)
            .ThenBy(static contribution => contribution.Module.Identity, StringComparer.Ordinal)
            .ThenBy(static contribution => contribution.Section.Name, StringComparer.Ordinal)
            .ToArray();
        Dictionary<(string Module, string Section, string Target), CallThunk[]> callThunks = new Dictionary<(string Module, string Section, string Target), CallThunk[]>();
        foreach (Contribution contribution in contributions)
        {
            string[] targets = contribution.Module.Artifact.Relocations
                .Where(relocation => relocation.SectionName == contribution.Section.Name &&
                    relocation.Kind == HybridCpuRelocationKind.ManagedCallRelativeSigned16)
                .Select(static relocation => relocation.TargetSymbol)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (targets.Length == 0) continue;
            ulong prefixSize = checked((ulong)targets.Length * CallThunk.SizeBytes);
            contribution.OriginalDataOffset = Align(prefixSize, HybridCpuBundleSerializer.BundleSizeBytes);
            ulong suffixOffset = Align(checked(contribution.OriginalDataOffset + contribution.Section.VirtualSize),
                HybridCpuBundleSerializer.BundleSizeBytes);
            ulong extendedSize = checked(suffixOffset + (ulong)targets.Length * CallThunk.SizeBytes);
            byte[] extendedData = new byte[checked((int)extendedSize)];
            contribution.Section.Data.CopyTo(extendedData, checked((int)contribution.OriginalDataOffset));
            contribution.Section = contribution.Section with { Data = extendedData, VirtualSize = extendedSize };
            for (int index = 0; index < targets.Length; index++)
            {
                string basis = $"__hclink_call_thunk:{contribution.Module.Identity}:{contribution.Section.Name}:{index}:{targets[index]}";
                callThunks.Add((contribution.Module.Identity, contribution.Section.Name, targets[index]),
                [
                    new(contribution, targets[index], checked((ulong)index * CallThunk.SizeBytes), basis + ":prefix"),
                    new(contribution, targets[index], checked(suffixOffset + (ulong)index * CallThunk.SizeBytes), basis + ":suffix")
                ]);
            }
        }
        ulong cursor = options.ImageBase;
        HybridCpuObjectSectionKind? priorKind = null;
        foreach (Contribution contribution in contributions)
        {
            if (priorKind is not null && priorKind != contribution.Section.Kind)
                cursor = Align(cursor, options.PageAlignmentBytes);
            cursor = Align(cursor, contribution.Section.AlignmentBytes);
            contribution.Address = cursor;
            cursor = checked(cursor + contribution.Section.VirtualSize);
            priorKind = contribution.Section.Kind;
        }
        ulong imageLength = checked(cursor - options.ImageBase);
        if (imageLength > (ulong)options.MaximumImageBytes)
            return Failure(HybridCpuLinkStatusV1.Invalid, "HCLINK0003", "Linked image exceeds the production byte budget.", options);
        byte[] image = new byte[checked((int)imageLength)];
        foreach (Contribution contribution in contributions)
        {
            if (contribution.Section.Data.Length != 0)
                contribution.Section.Data.CopyTo(image, checked((int)(contribution.Address - options.ImageBase)));
        }

        Dictionary<(string Identity, string Name), Contribution> contributionMap = contributions.ToDictionary(
            static contribution => (contribution.Module.Identity, contribution.Section.Name),
            static contribution => contribution);
        Dictionary<string, SymbolAddress> globalDefinitions = new Dictionary<string, SymbolAddress>(StringComparer.Ordinal);
        Dictionary<(string Module, string Name), SymbolAddress> localDefinitions = new Dictionary<(string Module, string Name), SymbolAddress>();
        foreach (Module module in modules)
        {
            foreach (HybridCpuObjectSymbolV1 symbol in module.Artifact.Symbols.Where(static symbol => symbol.IsDefinition))
            {
                Contribution contribution = contributionMap[(module.Identity, symbol.SectionName!)];
                SymbolAddress address = new SymbolAddress(module.Identity, symbol,
                    checked(contribution.Address + contribution.OriginalDataOffset + symbol.Offset));
                if (symbol.Binding == HybridCpuSymbolBinding.Local)
                {
                    localDefinitions.Add((module.Identity, symbol.Name), address);
                }
                else if (!globalDefinitions.TryAdd(symbol.Name, address))
                {
                    return Failure(HybridCpuLinkStatusV1.Invalid, "HCLINK1002",
                        $"Duplicate global definition '{symbol.Name}' is rejected; COMDAT/weak coalescing is unsupported.", options);
                }
            }
        }
        foreach (Module module in modules)
        {
            foreach (HybridCpuObjectSymbolV1 symbol in module.Artifact.Symbols.Where(static symbol => !symbol.IsDefinition))
            {
                if (!globalDefinitions.ContainsKey(symbol.Name))
                    return Failure(HybridCpuLinkStatusV1.Invalid, "HCLINK1003", $"Undefined global symbol '{symbol.Name}'.", options);
            }
        }

        foreach (CallThunk thunk in callThunks.Values.SelectMany(static thunks => thunks)
                     .OrderBy(static thunk => thunk.Name, StringComparer.Ordinal))
        {
            HybridCpuObjectSymbolV1 declaredTarget = thunk.Source.Module.Artifact.Symbols.Single(symbol =>
                string.Equals(symbol.Name, thunk.TargetSymbol, StringComparison.Ordinal));
            SymbolAddress? target;
            bool resolved = declaredTarget.Binding == HybridCpuSymbolBinding.Local
                ? localDefinitions.TryGetValue((thunk.Source.Module.Identity, declaredTarget.Name), out target)
                : globalDefinitions.TryGetValue(declaredTarget.Name, out target);
            if (!resolved || target is null)
                return Failure(HybridCpuLinkStatusV1.Invalid,
                    declaredTarget.Binding == HybridCpuSymbolBinding.Local ? "HCLINK1004" : "HCLINK1003",
                    $"Undefined {declaredTarget.Binding.ToString().ToLowerInvariant()} symbol '{thunk.TargetSymbol}' " +
                    $"required by call thunk '{thunk.Name}'.", options);
            thunk.Address = checked(thunk.Source.Address + thunk.Offset);
            long displacement = checked((long)target.Address - (long)thunk.Address);
            long high = checked((displacement + 0x800L) >> 12);
            long low = checked(displacement - (high << 12));
            if (high is < short.MinValue or > short.MaxValue || low is < short.MinValue or > short.MaxValue)
                return Failure(HybridCpuLinkStatusV1.Invalid, "HCLINK1006",
                    $"Long managed-call thunk '{thunk.Name}' in module '{thunk.Source.Module.Identity}' cannot reach " +
                    $"symbol '{thunk.TargetSymbol}': source=0x{thunk.Address:x}, target=0x{target.Address:x}, displacement={displacement}.", options);
            HybridCpuInstructionWord Word(HybridCpuOpcode opcode, byte rd, byte rs1,
                short immediate) => new()
            {
                OpCode = (uint)opcode, DataTypeValue = HybridCpuDataType.INT64,
                PredicateMask = byte.MaxValue,
                Word1 = HybridCpuInstructionWord.PackArchRegs(rd, rs1, HybridCpuInstructionWord.NoArchReg),
                Immediate = unchecked((ushort)immediate)
            };
            HybridCpuInstructionBundle[] bundles =
            [
                Bundle(Word(HybridCpuOpcode.AUIPC, 5, HybridCpuInstructionWord.NoArchReg, checked((short)high))),
                Bundle(Word(HybridCpuOpcode.JALR, 0, 5, checked((short)low)))
            ];
            byte[] thunkBytes = new HybridCpuBundleSerializer().SerializeProgram(bundles);
            thunkBytes.CopyTo(image, checked((int)(thunk.Address - options.ImageBase)));

            static HybridCpuInstructionBundle Bundle(HybridCpuInstructionWord word)
            {
                HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
                bundle.SetInstruction(0, word);
                return bundle;
            }
        }

        List<HybridCpuAppliedRelocationV1> applied = new List<HybridCpuAppliedRelocationV1>();
        foreach (Module module in modules)
        {
            foreach (HybridCpuObjectRelocationV1 relocation in module.Artifact.Relocations)
            {
                HybridCpuObjectSymbolV1 declaredTarget = module.Artifact.Symbols.Single(symbol =>
                    string.Equals(symbol.Name, relocation.TargetSymbol, StringComparison.Ordinal));
                SymbolAddress target;
                if (declaredTarget.Binding == HybridCpuSymbolBinding.Local)
                {
                    if (!localDefinitions.TryGetValue((module.Identity, declaredTarget.Name), out target!))
                        return Failure(HybridCpuLinkStatusV1.Invalid, "HCLINK1004", $"Local symbol '{declaredTarget.Name}' is not defined in its module.", options);
                }
                else if (!globalDefinitions.TryGetValue(declaredTarget.Name, out target!))
                {
                    return Failure(HybridCpuLinkStatusV1.Invalid, "HCLINK1003", $"Undefined global symbol '{declaredTarget.Name}'.", options);
                }

                Contribution source = contributionMap[(module.Identity, relocation.SectionName)];
                ulong placeAddress = checked(source.Address + source.OriginalDataOffset + relocation.Offset);
                HybridCpuRelocationEvaluationV1 evaluation = HybridCpuObjectFormatContractV1.EvaluateRelocation(
                    relocation.Kind, target.Address, placeAddress, relocation.Addend);
                CallThunk? resolvedThunk = null;
                if (evaluation.Status != HybridCpuObjectStatusV1.Success &&
                    evaluation.DiagnosticCode == "HCOBJ1013" &&
                    relocation.Kind == HybridCpuRelocationKind.ManagedCallRelativeSigned16 &&
                    callThunks.TryGetValue((module.Identity, relocation.SectionName, relocation.TargetSymbol), out CallThunk[]? thunks))
                {
                    foreach (CallThunk thunk in thunks.OrderBy(thunk => Math.Abs(checked((long)thunk.Address - (long)placeAddress)))
                                 .ThenBy(static thunk => thunk.Name, StringComparer.Ordinal))
                    {
                        HybridCpuRelocationEvaluationV1 thunkEvaluation = HybridCpuObjectFormatContractV1.EvaluateRelocation(
                            relocation.Kind, thunk.Address, placeAddress, relocation.Addend);
                        if (thunkEvaluation.Status != HybridCpuObjectStatusV1.Success) continue;
                        evaluation = thunkEvaluation;
                        resolvedThunk = thunk;
                        break;
                    }
                }
                if (evaluation.Status != HybridCpuObjectStatusV1.Success)
                    return Failure(HybridCpuLinkStatusV1.Invalid, "HCLINK1005",
                        $"Relocation overflow or unsupported relocation ({evaluation.DiagnosticCode}): " +
                        $"module='{module.Identity}', section='{relocation.SectionName}', offset=0x{relocation.Offset:x}, " +
                        $"kind={relocation.Kind}, symbol='{relocation.TargetSymbol}', place=0x{placeAddress:x}, target=0x{target.Address:x}.", options);
                int patchOffset = checked((int)(placeAddress - options.ImageBase));
                if (evaluation.WidthBits == 64)
                    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(patchOffset), evaluation.EncodedValue);
                else if (evaluation.WidthBits == 16)
                    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(patchOffset), checked((ushort)evaluation.EncodedValue));
                else
                    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(patchOffset), checked((uint)evaluation.EncodedValue));
                applied.Add(new(module.Identity, relocation.SectionName, relocation.Offset, relocation.Kind,
                    relocation.TargetSymbol, relocation.Addend, placeAddress, target.Address,
                    evaluation.EncodedValue, evaluation.WidthBits, resolvedThunk?.Name));
            }
        }

        HybridCpuLinkedSectionV1[] linkedSections = contributions.Select(static contribution => new HybridCpuLinkedSectionV1(
            contribution.Module.Identity, contribution.Section.Name, contribution.Section.Kind, contribution.Address,
            contribution.Section.VirtualSize, contribution.Section.AlignmentBytes)).ToArray();
        HybridCpuLinkedSymbolV1[] linkedSymbols = localDefinitions.Values.Concat(globalDefinitions.Values)
            .OrderBy(static symbol => symbol.Address).ThenBy(static symbol => symbol.Symbol.Name, StringComparer.Ordinal)
            .ThenBy(static symbol => symbol.ModuleIdentity, StringComparer.Ordinal)
            .Select(static symbol => new HybridCpuLinkedSymbolV1(symbol.Symbol.Name, symbol.ModuleIdentity,
                symbol.Symbol.Binding, symbol.Symbol.Visibility, symbol.Address, symbol.Symbol.Size))
            .Concat(callThunks.Values.SelectMany(static thunks => thunks).Select(static thunk => new HybridCpuLinkedSymbolV1(
                thunk.Name, thunk.Source.Module.Identity, HybridCpuSymbolBinding.Local,
                HybridCpuSymbolVisibility.Hidden, thunk.Address, CallThunk.SizeBytes)))
            .OrderBy(static symbol => symbol.Address).ThenBy(static symbol => symbol.Name, StringComparer.Ordinal)
            .ThenBy(static symbol => symbol.ModuleIdentity, StringComparer.Ordinal).ToArray();
        HybridCpuAppliedRelocationV1[] appliedRows = applied
            .OrderBy(static relocation => relocation.PlaceAddress).ThenBy(static relocation => relocation.ModuleIdentity, StringComparer.Ordinal)
            .ToArray();
        string linkMapDigest = DigestLinkMap(modules, linkedSections, linkedSymbols, appliedRows, options);
        string imageDigest = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        return new(HybridCpuLinkStatusV1.Success, options.ImageBase, image, imageDigest, linkMapDigest,
            options.OptionsDigest, linkedSections, linkedSymbols, appliedRows, Array.Empty<HybridCpuLinkDiagnosticV1>());
    }

    private static string DigestLinkMap(
        IEnumerable<Module> modules,
        IEnumerable<HybridCpuLinkedSectionV1> sections,
        IEnumerable<HybridCpuLinkedSymbolV1> symbols,
        IEnumerable<HybridCpuAppliedRelocationV1> relocations,
        HybridCpuStaticLinkOptionsV1 options)
    {
        StringBuilder builder = new StringBuilder(options.SchemaId).Append('|').Append(options.OptionsDigest);
        foreach (Module module in modules)
            builder.Append("|i:").Append(module.Identity).Append(':').Append(module.Artifact.ObjectSha256);
        foreach (HybridCpuLinkedSectionV1 section in sections)
            builder.Append("|s:").Append(section.Kind).Append(':').Append(section.ModuleIdentity).Append(':')
                .Append(section.Name).Append(':').Append(section.Address).Append(':').Append(section.Size).Append(':').Append(section.AlignmentBytes);
        foreach (HybridCpuLinkedSymbolV1 symbol in symbols)
            builder.Append("|y:").Append(symbol.Name).Append(':').Append(symbol.ModuleIdentity).Append(':')
                .Append(symbol.Binding).Append(':').Append(symbol.Visibility).Append(':').Append(symbol.Address).Append(':').Append(symbol.Size);
        foreach (HybridCpuAppliedRelocationV1 relocation in relocations)
            builder.Append("|r:").Append(relocation.ModuleIdentity).Append(':').Append(relocation.SectionName).Append(':')
                .Append(relocation.Offset).Append(':').Append(relocation.Kind).Append(':').Append(relocation.TargetSymbol)
                .Append(':').Append(relocation.Addend).Append(':').Append(relocation.PlaceAddress).Append(':')
                .Append(relocation.TargetAddress).Append(':').Append(relocation.EncodedValue).Append(':')
                .Append(relocation.ResolvedViaThunkSymbol);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static HybridCpuStaticLinkArtifactV1 Failure(
        HybridCpuLinkStatusV1 status,
        string code,
        string message,
        HybridCpuStaticLinkOptionsV1 options) =>
        new(status, options.ImageBase, Array.Empty<byte>(), string.Empty, string.Empty, options.OptionsDigest,
            Array.Empty<HybridCpuLinkedSectionV1>(), Array.Empty<HybridCpuLinkedSymbolV1>(),
            Array.Empty<HybridCpuAppliedRelocationV1>(), [new(code, message)]);

    private static ulong Align(ulong value, int alignment) => checked((value + (ulong)alignment - 1) & ~((ulong)alignment - 1));

    private sealed record Module(string Identity, HybridCpuObjectArtifactV1 Artifact);

    private sealed class Contribution(Module module, HybridCpuObjectSectionV1 section)
    {
        public Module Module { get; } = module;
        public HybridCpuObjectSectionV1 Section { get; set; } = section;
        public ulong OriginalDataOffset { get; set; }
        public ulong Address { get; set; }
    }

    private sealed class CallThunk(Contribution source, string targetSymbol, ulong offset, string name)
    {
        public const ulong SizeBytes = 2 * HybridCpuBundleSerializer.BundleSizeBytes;
        public Contribution Source { get; } = source;
        public string TargetSymbol { get; } = targetSymbol;
        public ulong Offset { get; } = offset;
        public string Name { get; } = name;
        public ulong Address { get; set; }
    }

    private sealed record SymbolAddress(string ModuleIdentity, HybridCpuObjectSymbolV1 Symbol, ulong Address);
}
