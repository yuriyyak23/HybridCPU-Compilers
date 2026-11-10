using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    // Synchronous BodyWorld-owned resolver; never retained outside the child importer call.
    private Func<int, RestrictedCilProvenanceV1, CancellationToken, bool>? _externalArgumentHelperProof;
    private const string ProjectedLocalConstructorName = "newobj.projected-local-i8";
    private static DecodeResult ProjectParameterScalarReads(DecodeResult decoded, MethodSignature signature,
        RestrictedCilProvenanceV1 provenance, CancellationToken cancellationToken)
    {
        IReadOnlyList<DecodedInstruction> instructions = decoded.Instructions;
        HashSet<int> targets = instructions.SelectMany(ControlFlowTargets).ToHashSet();
        HashSet<long> mutated = instructions.Where(row => row.Encoding == 0x10).Select(row => row.Literal).ToHashSet();
        List<DecodedInstruction> result = new List<DecodedInstruction>(instructions.Count);
        for (int index = 0; index < instructions.Count; index++) {
            if ((index & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            DecodedInstruction row = instructions[index];
            if (row.Encoding != 0x0f) { result.Add(row); continue; }
            int argument = checked((int)row.Literal);
            if (signature.Receiver is not null || targets.Contains(instructions[index + 1].Offset) ||
                mutated.Contains(argument))
                return new([], Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1001",
                    "Parameter getter projection requires an immutable scalar argument and an untargeted exact getter pair.",
                    OffsetIdentity(provenance, row.Offset), provenance));
            result.Add(row with { Encoding = 0x0e, Name = "ldarg.projected-getter-i4", Size = row.Size + instructions[index + 1].Size });
            index++;
        }
        return new(result, null);
    }

    // Values returned through an exact source-owned I4/I8 carrier can
    // stay scalar when every address use is one exact pure field getter. Do not
    // turn an arbitrary byref, constructor loan or aggregate into a scalar.
    private IReadOnlyList<DecodedInstruction> ProjectLocalScalarReads(MetadataReader metadata, PEReader pe,
        IReadOnlyList<DecodedInstruction> instructions, LocalSignature locals, MethodSignature signature, RestrictedCilProvenanceV1 provenance,
        CancellationToken cancellationToken, out RestrictedCilImportResultV1? projectionFailure)
    {
        projectionFailure = null;
        if (locals.Aggregates is null || !locals.Types.Contains(RestrictedCilTypeV1.Aggregate)) return instructions;
        HashSet<int> targets = instructions.SelectMany(ControlFlowTargets).ToHashSet();
        V2Graph? graph = null;
        Dictionary<int, HashSet<int>>? dominators = null;
        int[]? instructionBlocks = null;
        HashSet<int> qualified = new HashSet<int>();
        Dictionary<int, HashSet<int>> copyDependencies = new Dictionary<int, HashSet<int>>();
        Dictionary<int, List<(int Address, int Call)>> constructorInitializers = new Dictionary<int, List<(int Address, int Call)>>();
        Dictionary<int, bool> pureArgumentCalls = new Dictionary<int, bool>();
        static int LoadLocal(DecodedInstruction row) => row.Encoding switch {
            >= 0x06 and <= 0x09 => row.Encoding - 0x06,
            0x11 => checked((int)row.Literal), _ => -1 };
        static int StoreLocal(DecodedInstruction row) => row.Encoding switch {
            >= 0x0a and <= 0x0d => row.Encoding - 0x0a,
            0x13 => checked((int)row.Literal), _ => -1 };
        for (int local = 0; local < locals.Types.Count; local++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (locals.Types[local] != RestrictedCilTypeV1.Aggregate || locals.Aggregates[local] is not { } identity) continue;
            RestrictedCilAllocationBindingV1[] bindings = _allocationBindings.Values.Where(binding => binding.IsScalarValueProjection &&
                identity == $"[{metadata.GetString(metadata.GetAssemblyDefinition().Name)}]{binding.TypeDescriptor.StableIdentity}" &&
                binding.TypeDescriptor.Kind == HybridCpuManagedTypeKindV1.ValueType &&
                IsExactScalarCarrierLayout(binding) &&
                binding.ScalarValueType is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.Int64).ToArray();
            bool external = _externalScalarValues.TryGetValue(identity, out ScalarValueDeclaration? declaration);
            if (!external && (bindings.Length == 0 || bindings.Select(b => b.ScalarValueType).Distinct().Count() != 1)) continue;
            RestrictedCilTypeV1 carrier = external ? declaration!.Carrier : bindings[0].ScalarValueType;
            List<int> stores = new List<int>();
            List<int> reads = new List<int>();
            HashSet<int> dependencies = new HashSet<int>();
            List<(int Address, int Call)> initializers = new List<(int Address, int Call)>();
            bool valid = true;
            for (int index = 0; index < instructions.Count; index++)
            {
                if ((index & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                DecodedInstruction row = instructions[index];
                int accessed = row.Encoding switch {
                    >= 0x06 and <= 0x09 => row.Encoding - 0x06,
                    >= 0x0a and <= 0x0d => row.Encoding - 0x0a,
                    0x11 or 0x12 or 0x13 => checked((int)row.Literal),
                    _ => -1 };
                if (accessed != local) continue;
                if (row.Encoding == 0x12 && carrier == RestrictedCilTypeV1.Int64 &&
                    TryInt64ConstructorArgument(metadata, pe, instructions, index, locals, signature, identity, pureArgumentCalls,
                        provenance, cancellationToken, out int initializer, out projectionFailure)) {
                    stores.Add(initializer); initializers.Add((index, initializer)); continue;
                }
                if (projectionFailure is not null) return instructions;
                if (row.Encoding is >= 0x0a and <= 0x0d or 0x13) {
                    // Preserve the nominal value identity before erasing its carrier.
                    // A primitive I4 or another projected struct is not this value.
                    if (index == 0 || targets.Contains(row.Offset))
                    { valid = false; break; }
                    DecodedInstruction producer = instructions[index - 1];
                    int source = LoadLocal(producer);
                    if (source >= 0 && source < locals.Types.Count && locals.Types[source] == RestrictedCilTypeV1.Aggregate &&
                        locals.Aggregates[source] == identity) dependencies.Add(source);
                    else if (producer.Encoding == 0x73 && carrier == RestrictedCilTypeV1.Int64 &&
                        IsExactScalarConstructorProducer(metadata, producer.Token, identity, carrier)) { }
                    else if (producer.Encoding != 0x28 || !_managedCallTargets.TryGetValue(producer.Token, out RestrictedCilManagedCallTargetV1? target) ||
                        target.ReturnType != carrier || target.AggregateReturn != identity)
                    { valid = false; break; }
                    stores.Add(index); continue;
                }
                if (LoadLocal(row) == local) {
                    int destination = index + 1 < instructions.Count ? StoreLocal(instructions[index + 1]) : -1;
                    if (destination < 0 || destination >= locals.Types.Count || targets.Contains(instructions[index + 1].Offset) ||
                        locals.Types[destination] != RestrictedCilTypeV1.Aggregate || locals.Aggregates[destination] != identity)
                    { valid = false; break; }
                    dependencies.Add(destination); reads.Add(index); continue;
                }
                if (row.Encoding != 0x12 || index + 1 >= instructions.Count ||
                    instructions[index + 1] is not { Encoding: 0x28 } call ||
                    targets.Contains(call.Offset) ||
                    !IsExactScalarGetter(metadata, pe, call.Token, identity, carrier))
                { valid = false; break; }
                reads.Add(index);
            }
            if (!valid || stores.Count == 0 || reads.Count == 0 || initializers.Count != 0 && dependencies.Count == 0) continue;
            if (graph is null)
            {
                V2GraphBuild built = BuildV2Graph(instructions, provenance);
                // Preserve the normal CFG rejection and diagnostic precedence.
                if (built.Failure is not null) return instructions;
                graph = built.Graph!;
                dominators = ComputeV2Dominators(graph.Blocks, cancellationToken);
                instructionBlocks = new int[instructions.Count];
                foreach (V2Block block in graph.Blocks)
                    for (int index = block.StartInstructionIndex; index <= block.EndInstructionIndex; index++)
                        instructionBlocks[index] = block.Id;
            }
            if (stores.Count == 1
                ? reads.All(read => dominators![instructionBlocks![read]].Contains(instructionBlocks[stores[0]]) &&
                    (instructionBlocks[read] != instructionBlocks[stores[0]] || stores[0] < read))
                : ScalarLocalDefinitelyInitialized(graph, instructionBlocks!, stores, reads, cancellationToken))
            {
                qualified.Add(local);
                copyDependencies.Add(local, dependencies);
                constructorInitializers.Add(local, initializers);
            }
        }
        // Copy chains must be qualified as a whole. Neither an escaping source nor
        // an unsupported destination can be erased through a neighboring local.
        bool removed;
        do {
            removed = false;
            foreach (int local in qualified.ToArray()) {
                cancellationToken.ThrowIfCancellationRequested();
                if (copyDependencies[local].Any(dependency => !qualified.Contains(dependency))) {
                    qualified.Remove(local); removed = true;
                }
            }
        } while (removed);
        if (qualified.Count == 0) return instructions;
        HashSet<int> constructorAddresses = qualified.SelectMany(local => constructorInitializers[local].Select(pair => pair.Address)).ToHashSet();
        Dictionary<int, int> constructorStores = qualified.SelectMany(local => constructorInitializers[local].Select(pair => (pair.Call, Local: local)))
            .ToDictionary(pair => pair.Call, pair => pair.Local);
        HashSet<int> erasedReceiverRanges = qualified.SelectMany(local => constructorInitializers[local])
            .SelectMany(pair => Enumerable.Range(pair.Address, pair.Call - pair.Address)).ToHashSet();
        List<DecodedInstruction> result = new List<DecodedInstruction>(instructions.Count);
        for (int index = 0; index < instructions.Count; index++)
        {
            if ((index & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            DecodedInstruction row = instructions[index] with { ErasedReceiverDepth = instructions[index].ErasedReceiverDepth + (erasedReceiverRanges.Contains(index) ? 1 : 0) };
            if (constructorAddresses.Contains(index))
                result.Add(row with { Encoding = 0x00, Name = "nop.projected-constructor-address", Literal = 0 });
            else if (constructorStores.TryGetValue(index, out int destination))
                result.Add(row with { Encoding = 0x13, Name = "stloc.projected-constructor-i8", Literal = destination, Token = 0 });
            else if (row.Encoding == 0x73 && index + 1 < instructions.Count && qualified.Contains(StoreLocal(instructions[index + 1])))
                result.Add(row with { Name = ProjectedLocalConstructorName });
            else if (row.Encoding == 0x12 && qualified.Contains(checked((int)row.Literal)))
            {
                result.Add(row with { Encoding = 0x11, Name = "ldloc.projected-getter-scalar", Size = row.Size + instructions[index + 1].Size });
                index++;
            }
            else result.Add(row);
        }
        return result;
    }

    private bool TryInt64ConstructorArgument(MetadataReader metadata, PEReader pe, IReadOnlyList<DecodedInstruction> instructions,
        int address, LocalSignature locals, MethodSignature signature, string identity,
        Dictionary<int, bool> pureArgumentCalls,
        RestrictedCilProvenanceV1 provenance, CancellationToken cancellationToken, out int constructor,
        out RestrictedCilImportResultV1? projectionFailure, bool nullableDefault = false, bool requirePure = false, bool deferCalls = false)
    {
        projectionFailure = null;
        constructor = -1;
        // A nullable U4 default retains its original uint input instructions in
        // the enclosing scalar argument region. Keep the existing U4 type fact;
        // return/consumer compatibility still requires explicit CIL widening.
        bool IsArgumentInput(RestrictedCilTypeV1 type) => IsPrimitiveConstructorInput(type) || type is RestrictedCilTypeV1.Boolean or RestrictedCilTypeV1.Int8 or RestrictedCilTypeV1.UInt8 or RestrictedCilTypeV1.Int16 or RestrictedCilTypeV1.UInt16 or RestrictedCilTypeV1.UInt32 or RestrictedCilTypeV1.UInt64;
        ushort NullableMember(int token, bool wide = false, bool unsigned = false, int narrowBits = 0, bool character = false, bool logical = false, string? enumIdentity = null) =>
            NullableInt32Member(metadata, token, wide, unsigned, narrowBits, character, logical, enumIdentity, locals.EnumDeclarations, signature.ExactMethodArguments ?? locals.ExactMethodArguments);
        List<DecodedInstruction> expression = new List<DecodedInstruction>();
        for (int index = address + 1; index < instructions.Count; index++) {
            if ((index & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            DecodedInstruction row = instructions[index];
            if (row.Encoding == 0x28 && (nullableDefault
                ? IsNullableLocal(identity) && NullableMember(row.Token, IsWideNullable(identity), IsUnsignedNullable(identity), NarrowNullableBits(identity), identity == NullableCharIdentity, identity == NullableBooleanIdentity, IsNullableEnum(identity) ? identity : null) == NullableInt32Default
                : IsExactScalarConstructorProducer(metadata, row.Token, identity, RestrictedCilTypeV1.Int64) ||
                  identity == NullableInt64Identity && NullableMember(row.Token, wide: true) == NullableInt32New ||
                  IsNullableEnum(identity) && NullableMember(row.Token, wide: IsWideNullableEnum(identity), unsigned: IsUnsignedNullableEnum(identity), narrowBits: NarrowNullableBits(identity), enumIdentity: identity) == NullableInt32New ||
                  identity == NullableInt32Identity && NullableMember(row.Token) == NullableInt32New ||
                  identity == NullableUInt32Identity && NullableMember(row.Token, unsigned: true) == NullableInt32New ||
                  identity == NullableUInt64Identity && NullableMember(row.Token, wide: true, unsigned: true) == NullableInt32New ||
                  IsNarrowNullable(identity) && !IsNullableEnum(identity) && NullableMember(row.Token, unsigned: IsUnsignedNullable(identity), narrowBits: NarrowNullableBits(identity), character: identity == NullableCharIdentity, logical: identity == NullableBooleanIdentity) == NullableInt32New)) {
                if (expression.Count == 0) return false;
                HashSet<int> regionOffsets = expression.Select(instruction => instruction.Offset).Append(row.Offset).ToHashSet();
                // Only internal forward edges may enter the isolated argument region.
                // An external edge would bypass the receiver-address initialization.
                foreach (DecodedInstruction source in instructions) {
                    foreach (int target in ControlFlowTargets(source)) {
                        bool inside = regionOffsets.Contains(source.Offset) && source.Offset != row.Offset;
                        if ((regionOffsets.Contains(target) && !inside) || (inside &&
                            (!regionOffsets.Contains(target) || target <= source.Offset))) return false;
                    }
                }
                // Discovery may erase only the exact borrowed constructor/default receiver.
                // Scalar calls remain original graph edges; production import must
                // repeat this proof with exact selected bodies and typed dataflow.
                if (deferCalls && expression.Any(instruction => instruction.Encoding == 0x28)) {
                    constructor = index;
                    return true;
                }
                if (!nullableDefault && !IsNullableEnum(identity) && identity is not (NullableInt32Identity or NullableUInt32Identity or NullableUInt64Identity or NullableUInt16Identity or NullableInt16Identity or NullableUInt8Identity or NullableInt8Identity or NullableCharIdentity or NullableBooleanIdentity) && expression.Count == 1 && IsExactInt64ConstructorArgument(expression[0], locals, signature)) {
                    constructor = index; return true;
                }
                // Reuse the existing typed stack semantics from an empty evaluation stack.
                // No expression may consume or duplicate the receiver underneath its arguments.
                expression.Add(row with { Encoding = 0x2a, Name = "ret.constructor-argument-proof", Token = 0 });
                V2GraphBuild built = BuildV2Graph(expression, provenance);
                if (built.Failure is not null) {
                    if (built.Failure.Status == RestrictedCilImportStatusV1.BudgetExhausted) projectionFailure = built.Failure;
                    return false;
                }
                MethodSignature argumentSignature = signature with { ReturnType = identity == NullableBooleanIdentity ? RestrictedCilTypeV1.Boolean : identity == NullableCharIdentity ? RestrictedCilTypeV1.UInt16 : identity == NullableInt8Identity || IsNullableEnumInt8(identity) ? RestrictedCilTypeV1.Int8 : identity == NullableUInt8Identity || IsNullableEnumUInt8(identity) ? RestrictedCilTypeV1.UInt8 : identity == NullableInt16Identity || IsNullableEnumInt16(identity) ? RestrictedCilTypeV1.Int16 : identity == NullableUInt16Identity || IsNullableEnumUInt16(identity) ? RestrictedCilTypeV1.UInt16 : identity == NullableUInt64Identity || IsNullableEnumUInt64(identity) ? RestrictedCilTypeV1.UInt64 : identity == NullableUInt32Identity || IsNullableEnumUInt32(identity) ? RestrictedCilTypeV1.UInt32 :
                    identity == NullableInt32Identity || IsNullableEnumInt32(identity) ? RestrictedCilTypeV1.Int32 : RestrictedCilTypeV1.Int64, AggregateReturn = null };
                V2State entry = new V2State([], locals.Types.Select((type, local) => (IsArgumentInput(type) ||
                    type == RestrictedCilTypeV1.ObjectReference && (locals.ConstructorReferences?.ElementAtOrDefault(local) ?? ConstructorReferenceKind.None) != ConstructorReferenceKind.None) &&
                    (locals.Aggregates?.ElementAtOrDefault(local) is null || IsNullableLocal(locals.Aggregates?.ElementAtOrDefault(local))) ? V2Value.Definition(StackType(type), $"constructor-proof-local:{local}") with {
                        ConstructorReference = locals.ConstructorReferences?.ElementAtOrDefault(local) ?? ConstructorReferenceKind.None,
                        AggregateIdentity = IsNullableLocal(locals.Aggregates?.ElementAtOrDefault(local)) ? locals.Aggregates![local] : null,
                        NullableHasValue = IsWideNullable(locals.Aggregates?.ElementAtOrDefault(local)) ? V2Value.Definition(RestrictedCilTypeV1.Int32, $"constructor-proof-presence:{local}") : null } : V2Value.Uninitialized),
                    Enumerable.Range(0, signature.Parameters.Count).Select(argument => AnalysisArgument(signature, argument, provenance.CanonicalMethodLocalIdentity) with {
                        ConstructorReference = signature.ConstructorReferenceParameters?.ElementAtOrDefault(argument) ?? ConstructorReferenceKind.None }));
                cancellationToken.ThrowIfCancellationRequested();
                V2DataflowBuild proof = RunV2Dataflow(metadata, built.Graph!, argumentSignature, locals, expression.Count,
                    provenance, new Dictionary<int, ManagedReceiverCallerStoragePlanV1>(),
                    new Dictionary<int, V2State> { [expression[0].Offset] = entry }, enforceStackBudget: false, enforceConstructorReferences: true);
                cancellationToken.ThrowIfCancellationRequested();
                if (proof.Failure is not null) {
                    if (proof.Failure.Status == RestrictedCilImportStatusV1.BudgetExhausted) projectionFailure = proof.Failure;
                    return false;
                }
                // The receiver address is erased before evaluation; the scalar store occurs
                // only after successful return. No byref/partially initialized value crosses
                // this exact checked helper's safepoint or exception transfer.
                if (proof.Dataflow!.Helpers.Values.Any(helper => !helper.IsManagedCall && (requirePure || !IsCheckedScalarArithmetic(helper) && !IsCheckedStringAccess(helper)))) return false;
                constructor = index; return true;
            }
            int argument = row.Encoding switch { >= 0x02 and <= 0x05 => row.Encoding - 0x02, 0x0e => checked((int)row.Literal), _ => -1 };
            int local = row.Encoding switch { >= 0x06 and <= 0x09 => row.Encoding - 0x06, 0x11 => checked((int)row.Literal), _ => -1 };
            int storedLocal = row.Encoding switch { >= 0x0a and <= 0x0d => row.Encoding - 0x0a, 0x13 => checked((int)row.Literal), _ => -1 };
            bool primitiveStore = storedLocal >= 0 && storedLocal < locals.Types.Count &&
                IsArgumentInput(locals.Types[storedLocal]) && locals.Aggregates?.ElementAtOrDefault(storedLocal) is null;
            bool referenceStore = storedLocal >= 0 && storedLocal < locals.Types.Count && locals.Types[storedLocal] == RestrictedCilTypeV1.ObjectReference &&
                locals.Aggregates?.ElementAtOrDefault(storedLocal) is null &&
                (locals.ConstructorReferences?.ElementAtOrDefault(storedLocal) ?? ConstructorReferenceKind.None) != ConstructorReferenceKind.None;
            int storedArgument = row.Encoding == 0x10 ? checked((int)row.Literal) : -1;
            bool primitiveArgumentStore = storedArgument >= 0 && storedArgument < signature.Parameters.Count &&
                !(storedArgument == 0 && signature.Receiver is not null) && IsArgumentInput(signature.Parameters[storedArgument]) &&
                signature.AggregateParameters?.ElementAtOrDefault(storedArgument) is null;
            bool referenceArgumentStore = storedArgument >= 0 && storedArgument < signature.Parameters.Count &&
                !(storedArgument == 0 && signature.HasThis) && signature.Parameters[storedArgument] == RestrictedCilTypeV1.ObjectReference &&
                signature.AggregateParameters?.ElementAtOrDefault(storedArgument) is null &&
                (signature.ConstructorReferenceParameters?.ElementAtOrDefault(storedArgument) ?? ConstructorReferenceKind.None) != ConstructorReferenceKind.None;
            bool primitiveLoad = argument >= 0 && argument < signature.Parameters.Count && IsArgumentInput(signature.Parameters[argument]) &&
                    signature.AggregateParameters?.ElementAtOrDefault(argument) is null ||
                local >= 0 && local < locals.Types.Count && IsArgumentInput(locals.Types[local]) && locals.Aggregates?.ElementAtOrDefault(local) is null;
            primitiveLoad |= local >= 0 && local < locals.Types.Count && locals.Aggregates?.ElementAtOrDefault(local) == NullableInt32Identity;
            ConstructorReferenceKind loadedReferenceKind = argument >= 0 && argument < signature.Parameters.Count &&
                signature.Parameters[argument] == RestrictedCilTypeV1.ObjectReference
                    ? signature.ConstructorReferenceParameters?.ElementAtOrDefault(argument) ?? ConstructorReferenceKind.None
                    : local >= 0 && local < locals.Types.Count && locals.Types[local] == RestrictedCilTypeV1.ObjectReference
                        ? locals.ConstructorReferences?.ElementAtOrDefault(local) ?? ConstructorReferenceKind.None : ConstructorReferenceKind.None;
            bool declaredReferenceLoad = loadedReferenceKind != ConstructorReferenceKind.None;
            bool scalarOperation = row.Encoding is NullableInt32New or NullableInt32Value or NullableInt32HasValue or NullableInt32Default or NullableDefaultStack or NullableCheckedValue or 0x00 or >= 0x14 and <= 0x21 or 0x25 or 0x26 or
                >= 0x2b and <= 0x44 or
                >= 0x58 and <= 0x6a or 0x6d or 0x6e or 0x8e or >= 0x90 and <= 0x96 or 0xd1 or 0xd2 or 0xd3 or >= 0xfe01 and <= 0xfe05;
            scalarOperation |= identity == NullableInt32Identity && signature.AggregateReturn == NullableInt32Identity &&
                row.Encoding is NullableInt32ParameterValue or NullableInt32ParameterPresence;
            bool pureCall = false;
            bool stringAccessCall = row.Encoding is 0x28 or 0x6f && IsExactConstructorStringAccess(metadata, row.Token);
            if (row.Encoding == 0x28 && !stringAccessCall) {
                if (deferCalls) { expression.Add(row); continue; }
                if (!pureArgumentCalls.TryGetValue(row.Token, out pureCall)) {
                    pureCall = IsPureLocalConstructorArgumentCall(metadata, pe, row.Token, provenance, cancellationToken);
                    pureArgumentCalls.Add(row.Token, pureCall);
                }
            }
            if (!primitiveLoad && !declaredReferenceLoad && !primitiveStore && !referenceStore && !primitiveArgumentStore && !referenceArgumentStore && !scalarOperation && !pureCall && !stringAccessCall) return false;
            expression.Add(row);
        }
        return false;
    }

    private static bool MatchesConstructorReferenceStore(V2Value value, ConstructorReferenceKind declaration) =>
        declaration is not (ConstructorReferenceKind.None or ConstructorReferenceKind.Null) &&
        (value.ConstructorReference == declaration || value.ConstructorReference == ConstructorReferenceKind.Null);

    private static bool MatchesConstructorArrayReference(ConstructorReferenceKind kind, ushort encoding) =>
        kind == ConstructorReferenceKind.Null && encoding is 0x8e or >= 0x90 and <= 0x96 || encoding switch {
        0x8e => kind is not (ConstructorReferenceKind.None or ConstructorReferenceKind.String),
        0x90 => kind == ConstructorReferenceKind.SByteArray,
        0x91 => kind is ConstructorReferenceKind.ByteArray or ConstructorReferenceKind.BooleanArray,
        0x92 => kind == ConstructorReferenceKind.Int16Array,
        0x93 => kind is ConstructorReferenceKind.UInt16Array or ConstructorReferenceKind.CharArray,
        0x94 => kind == ConstructorReferenceKind.Int32Array,
        0x95 => kind == ConstructorReferenceKind.UInt32Array,
        0x96 => kind is ConstructorReferenceKind.Int64Array or ConstructorReferenceKind.UInt64Array,
        _ => false };

    private static bool IsCheckedStringAccess(ResolvedHelper helper) => !helper.IsManagedCall && helper.Dispatch is null &&
        helper.Contract.StableIdentity == helper.Identity && helper.Contract.CallingConvention == "runtime-helper" &&
        (helper.Identity == "System.String.get_Length(System.Object):System.Int32" &&
            helper.Contract.CanonicalExpansion == "__hybridcpu_managed_string_length" && helper.Contract.ReturnType == RestrictedCilTypeV1.Int32 &&
            helper.Contract.ParameterTypes.SequenceEqual(new[] { RestrictedCilTypeV1.ObjectReference }) ||
         helper.Identity == "System.String.get_Chars(System.Object,System.Int32):System.UInt16" &&
            helper.Contract.CanonicalExpansion == "__hybridcpu_managed_string_char" && helper.Contract.ReturnType == RestrictedCilTypeV1.UInt16 &&
            helper.Contract.ParameterTypes.SequenceEqual(new[] { RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32 })) &&
        helper.Contract.MemoryEffects == HybridCPU.Compiler.Core.IR.IrMemoryEffectKind.Read &&
        helper.Contract.ArchitecturalEffects == HybridCPU.Compiler.Core.IR.IrArchitecturalEffectKind.Control;

    private bool IsExactConstructorStringAccess(MetadataReader metadata, int token)
        => IsExactStringAccessorDeclaration(metadata, token) && ResolveHelper(metadata, token).Helper is { } helper && IsCheckedStringAccess(helper);

    private static bool IsExactStringAccessorDeclaration(MetadataReader metadata, int token)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        string name;
        byte[] signature;
        string? assembly;
        if (handle.Kind == HandleKind.MemberReference) {
            MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)handle);
            if (member.Parent.Kind != HandleKind.TypeReference) return false;
            TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (type.ResolutionScope.Kind != HandleKind.AssemblyReference || metadata.GetString(type.Namespace) != "System" ||
                metadata.GetString(type.Name) != "String") return false;
            name = metadata.GetString(member.Name); signature = metadata.GetBlobBytes(member.Signature);
            assembly = ReferencedAssemblyName(metadata, (TypeReferenceHandle)member.Parent);
        } else if (handle.Kind == HandleKind.MethodDefinition) {
            MethodDefinition method = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
            if ((method.Attributes & MethodAttributes.Static) != 0 || FindDeclaringType(metadata, (MethodDefinitionHandle)handle) != "System.String") return false;
            name = metadata.GetString(method.Name); signature = metadata.GetBlobBytes(method.Signature);
            assembly = metadata.IsAssembly ? metadata.GetString(metadata.GetAssemblyDefinition().Name) : null;
        } else return false;
        if (!(name == "get_Length" && signature.AsSpan().SequenceEqual(new byte[] { 0x20, 0, 0x08 }) ||
              name == "get_Chars" && signature.AsSpan().SequenceEqual(new byte[] { 0x20, 1, 0x03, 0x08 }))) return false;
        return assembly is "System.Private.CoreLib" or "System.Runtime" or "mscorlib";
    }

    private bool IsPureLocalConstructorArgumentCall(MetadataReader metadata, PEReader pe, int token,
        RestrictedCilProvenanceV1 provenance, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_externalArgumentHelperProof is not null)
            return _externalArgumentHelperProof(token, provenance, cancellationToken);
        if (MetadataTokens.EntityHandle(token).Kind == HandleKind.MemberReference)
            return _externalArgumentHelperProof?.Invoke(token, provenance, cancellationToken) == true;
        if (MetadataTokens.EntityHandle(token).Kind != HandleKind.MethodDefinition ||
            !_managedCallTargets.TryGetValue(token, out RestrictedCilManagedCallTargetV1? target)) return false;
        return IsPureConstructorArgumentDefinition(metadata, pe, token, target, provenance, cancellationToken);
    }

    private bool IsPureConstructorArgumentDefinition(MetadataReader metadata, PEReader pe, int token,
        RestrictedCilManagedCallTargetV1 target, RestrictedCilProvenanceV1 provenance, CancellationToken cancellationToken,
        Func<int, bool>? dependencyProof = null, IReadOnlyList<RestrictedCilGenericArgumentV1>? methodArguments = null,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? typeArguments = null,
        Action<RestrictedCilImportResultV1>? budgetFailure = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        methodArguments ??= [];
        typeArguments ??= [];
        if (target.AggregateParameters?.Contains(NullableInt32Identity) == true) {
            MethodDefinition nullableMethod = metadata.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.EntityHandle(token));
            MethodSignature nullableSignature = ParseMethodSignature(metadata, nullableMethod.Signature, [], [], allowAggregates: true);
            return IsExactNullableInt32ParameterGetter(metadata, pe, (MethodDefinitionHandle)MetadataTokens.EntityHandle(token), nullableSignature, provenance, cancellationToken, out _, dependencyProof) &&
                nullableSignature.ReturnType == target.ReturnType && nullableSignature.Parameters.SequenceEqual(target.ParameterTypes) &&
                nullableSignature.AggregateParameters!.SequenceEqual(target.AggregateParameters) &&
                CreateManagedMethodIdentity(metadata, nullableMethod.GetDeclaringType(), (MethodDefinitionHandle)MetadataTokens.EntityHandle(token), nullableSignature).StableIdentity == target.StableIdentity;
        }
        if (target.Receiver is not null ||
            !IsPrimitiveConstructorInput(target.ReturnType) || target.AggregateReturn is not null ||
            target.ParameterTypes.Any(type => !IsPrimitiveConstructorInput(type)) ||
            target.AggregateParameters?.Any(identity => identity is not null) == true) return false;
        MethodDefinition method = metadata.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.EntityHandle(token));
        TypeDefinition owner = metadata.GetTypeDefinition(method.GetDeclaringType());
        if (!method.Attributes.HasFlag(MethodAttributes.Static) ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl | MethodAttributes.Virtual)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.Synchronized | MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) != 0 ||
            method.GetGenericParameters().Count != methodArguments.Count || owner.GetGenericParameters().Count != typeArguments.Count || method.RelativeVirtualAddress == 0 ||
            !(method.GetGenericParameters().All(handle => metadata.GetGenericParameter(handle).Attributes == GenericParameterAttributes.None &&
                metadata.GetGenericParameter(handle).GetConstraints().Count == 0) ||
                _exactStructOwnerIngress && IsExactStructScalarMethod(metadata, method, typeArguments, methodArguments)) ||
            !(owner.GetGenericParameters().All(handle => metadata.GetGenericParameter(handle).Attributes == GenericParameterAttributes.None &&
                metadata.GetGenericParameter(handle).GetConstraints().Count == 0) ||
                _exactStructOwnerIngress && IsExactSignedOwnerContext(metadata, owner, typeArguments, requireStructConstraint: true)) ||
            owner.GetMethods().Any(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == ".cctor")) return false;
        MethodSignature signature = ParseMethodSignature(metadata, method.Signature, typeArguments, methodArguments, allowAggregates: true);
        if (signature.Status != SignatureStatus.Success || signature.HasThis || signature.ReturnType != target.ReturnType ||
            !signature.Parameters.SequenceEqual(target.ParameterTypes) ||
            CreateManagedMethodIdentity(metadata, method.GetDeclaringType(), (MethodDefinitionHandle)MetadataTokens.EntityHandle(token), signature, typeArguments, methodArguments).StableIdentity != target.StableIdentity) return false;
        MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress);
        if (body.Size > _budgets.MaximumMethodBodyBytes || body.ExceptionRegions.Length != 0) return false;
        LocalSignature locals = ParseLocalSignature(metadata, body.LocalSignature, typeArguments, methodArguments, allowAggregates: true);
        if (locals.Status != SignatureStatus.Success || locals.Types.Count > _budgets.MaximumLocals ||
            locals.Types.Any(type => !IsPrimitiveConstructorInput(type) && type is not (RestrictedCilTypeV1.UInt32 or RestrictedCilTypeV1.UInt64))) return false;
        DecodeResult decoded = Decode(body.GetILBytes() ?? [], provenance, metadata, pe, allowProjection: false);
        if (decoded.Failure is not null) return false;
        if (_exactStructOwnerIngress && IsExactStructScalarMethod(metadata, method, typeArguments, methodArguments))
            decoded = new(ProjectExactGenericInt32LocalZero(metadata, decoded.Instructions, signature, locals,
                exactScalarMethod: true), null);
        // Reuse the exact bounded typed induction proof. A discovered cycle is
        // not purity: only its statically bounded backedge may pass this gate.
        HashSet<(int Source, int Target)>? countedEdges = NullableInt32CountedBackedges(decoded.Instructions,
            localTypes: locals.Types, immutableBoundParameters: signature.Parameters, allowConstantInduction: true);
        if (countedEdges is null) return false;
        if (locals.Types.Any(type => type is RestrictedCilTypeV1.UInt32 or RestrictedCilTypeV1.UInt64)) {
            if (countedEdges.Count == 0) return false;
            IReadOnlyList<DecodedInstruction>? counterRows = locals.Types.Contains(RestrictedCilTypeV1.UInt64) ? NormalizeWideInductionConstants(decoded.Instructions) : decoded.Instructions;
            if (counterRows is null) return false;
            HashSet<int> counters = countedEdges.Select(edge => counterRows.ToList().FindIndex(row => row.Offset == edge.Source))
                .Select(latch => NullableInt32LocalIndex(counterRows[latch - 2], false)).ToHashSet();
            if (locals.Types.Select((type, index) => (type, index)).Any(local => local.type is RestrictedCilTypeV1.UInt32 or RestrictedCilTypeV1.UInt64 && !counters.Contains(local.index))) return false;
        }
        foreach (DecodedInstruction row in decoded.Instructions) {
            if (row.Encoding == 0x28 && dependencyProof?.Invoke(row.Token) == true) continue;
            if (row.Encoding is not (>= 0x00 and <= 0x0e or 0x10 or >= 0x11 and <= 0x11 or >= 0x13 and <= 0x21 or
                0x25 or 0x26 or >= 0x2a and <= 0x44 or >= 0x58 and <= 0x6a or
                0x6d or 0x6e or 0xd1 or 0xd2 or 0xd3 or >= 0xfe01 and <= 0xfe05) ||
                ControlFlowTargets(row).Any(offset => offset <= row.Offset && !countedEdges.Contains((row.Offset, offset)))) return false;
        }
        V2GraphBuild graph = BuildV2Graph(decoded.Instructions, provenance);
        if (graph.Failure is not null) return false;
        if (AnalyzeV2Loops(graph.Graph!, provenance).Failure is not null) return false;
        V2DataflowBuild proof = RunV2Dataflow(metadata, graph.Graph!, signature, locals, body.MaxStack, provenance,
            new Dictionary<int, ManagedReceiverCallerStoragePlanV1>());
        if (proof.Failure?.Status == RestrictedCilImportStatusV1.BudgetExhausted) budgetFailure?.Invoke(proof.Failure);
        cancellationToken.ThrowIfCancellationRequested();
        // Pure helper admission may use only fault-free scalar operations and
        // recursively proven managed dependencies, never a checked runtime helper.
        return proof.Failure is null && proof.Dataflow!.Helpers.Values.All(helper => helper.IsManagedCall);
    }

    private static bool IsPrimitiveConstructorInput(RestrictedCilTypeV1 type) => type is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.Int64;

    private static bool IsExactInt64ConstructorArgument(DecodedInstruction row, LocalSignature locals, MethodSignature signature)
    {
        int argument = row.Encoding switch { >= 0x02 and <= 0x05 => row.Encoding - 0x02, 0x0e => checked((int)row.Literal), _ => -1 };
        if (argument >= 0) return argument < signature.Parameters.Count && signature.Parameters[argument] == RestrictedCilTypeV1.Int64 &&
            signature.AggregateParameters?.ElementAtOrDefault(argument) is null;
        int local = row.Encoding switch { >= 0x06 and <= 0x09 => row.Encoding - 0x06, 0x11 => checked((int)row.Literal), _ => -1 };
        return row.Encoding == 0x21 || local >= 0 && local < locals.Types.Count && locals.Types[local] == RestrictedCilTypeV1.Int64;
    }

    private bool IsExactScalarConstructorProducer(MetadataReader metadata, int token, string identity, RestrictedCilTypeV1 carrier)
    {
        AllocationResolution resolution = ResolveAllocation(metadata, token);
        if (resolution.Failure is not null || resolution.Allocation is not { IsScalarValueProjection: true } binding ||
            binding.ScalarValueType != carrier || binding.ScalarArgumentType != carrier || !IsExactScalarCarrierLayout(binding)) return false;
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        EntityHandle owner = handle.Kind == HandleKind.MethodDefinition
            ? metadata.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType()
            : handle.Kind == HandleKind.MemberReference ? metadata.GetMemberReference((MemberReferenceHandle)handle).Parent : default;
        return !owner.IsNil && AggregateIdentity(metadata, owner) == identity;
    }

    private static bool ScalarLocalDefinitelyInitialized(V2Graph graph, int[] instructionBlocks,
        IReadOnlyList<int> stores, IReadOnlyList<int> reads, CancellationToken cancellationToken)
    {
        Dictionary<int, int> firstStore = stores.GroupBy(index => instructionBlocks[index]).ToDictionary(group => group.Key, group => group.Min());
        // Must analysis: top=true, entry=false. Reachability is already established
        // by the existing CFG builder; intersections converge downwards, including cycles.
        bool[] entry = graph.Blocks.Select(block => block.Id != 0).ToArray();
        bool[] exit = graph.Blocks.Select(block => block.Id != 0 || firstStore.ContainsKey(0)).ToArray();
        bool changed;
        do {
            changed = false;
            foreach (V2Block block in graph.Blocks) {
                cancellationToken.ThrowIfCancellationRequested();
                bool initialized = block.Id != 0 && block.Predecessors.Count != 0 &&
                    block.Predecessors.All(predecessor => exit[predecessor]);
                bool output = initialized || firstStore.ContainsKey(block.Id);
                if (entry[block.Id] != initialized || exit[block.Id] != output) changed = true;
                entry[block.Id] = initialized; exit[block.Id] = output;
            }
        } while (changed);
        foreach (int read in reads) {
            cancellationToken.ThrowIfCancellationRequested();
            int block = instructionBlocks[read];
            if (!entry[block] && (!firstStore.TryGetValue(block, out int store) || store >= read)) return false;
        }
        return true;
    }

    private static bool IsExactLocalScalarGetter(MetadataReader metadata, PEReader pe, int token,
        string identity, RestrictedCilTypeV1 carrier)
    {
        if (MetadataTokens.EntityHandle(token).Kind != HandleKind.MethodDefinition) return false;
        MethodDefinition getter = metadata.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.EntityHandle(token));
        TypeDefinitionHandle owner = getter.GetDeclaringType();
        TypeDefinition type = metadata.GetTypeDefinition(owner);
        int width = carrier == RestrictedCilTypeV1.Int64 ? 8 : 4;
        if (AggregateIdentity(metadata, owner) != identity || type.GetGenericParameters().Count != 0 ||
            new InlineStorageResolver([]).Value(metadata, owner) is not { } layout || layout.Size != width || layout.Alignment != width ||
            type.GetMethods().Any(h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == ".cctor") ||
            (getter.Attributes & (MethodAttributes.Static | MethodAttributes.Virtual | MethodAttributes.PinvokeImpl | MethodAttributes.Abstract)) != 0 ||
            (getter.ImplAttributes & MethodImplAttributes.Synchronized) != 0 || getter.GetGenericParameters().Count != 0 ||
            getter.RelativeVirtualAddress == 0) return false;
        byte scalar = carrier switch { RestrictedCilTypeV1.Int32 => 0x08, RestrictedCilTypeV1.UInt32 => 0x09,
            RestrictedCilTypeV1.Int64 => 0x0a, _ => 0 };
        if (scalar == 0) return false;
        if (!metadata.GetBlobBytes(getter.Signature).SequenceEqual(new byte[] { 0x20, 0, scalar })) return false;
        FieldDefinitionHandle[] fields = type.GetFields().Where(h => !metadata.GetFieldDefinition(h).Attributes.HasFlag(FieldAttributes.Static)).ToArray();
        if (fields.Length != 1 || !metadata.GetBlobBytes(metadata.GetFieldDefinition(fields[0]).Signature).SequenceEqual(new byte[] { 0x06, scalar }))
            return false;
        MethodBodyBlock body = pe.GetMethodBody(getter.RelativeVirtualAddress);
        if (body.Size > 64 || body.ExceptionRegions.Length != 0 || !body.LocalSignature.IsNil) return false;
        byte[] il = body.GetILBytes() ?? [];
        return il.Length == 7 && il[0] == 0x02 && il[1] == 0x7b && il[6] == 0x2a &&
            BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(2, 4)) == MetadataTokens.GetToken(fields[0]);
    }

    private sealed record StaticScalarProjection(string OwnerType, int GetterToken, int InnerFieldToken, string BodyDigest);

    // Eliminate only an immediately consumed, nonescaping static byref whose complete
    // getter is a single I4 field read. This does not provide a general byref ABI.
    private static IReadOnlyList<DecodedInstruction> ProjectStaticScalarReads(MetadataReader metadata, PEReader pe,
        IReadOnlyList<DecodedInstruction> instructions)
    {
        HashSet<int> targets = instructions.SelectMany(ControlFlowTargets).ToHashSet();
        List<DecodedInstruction> result = new List<DecodedInstruction>();
        for (int index = 0; index < instructions.Count; index++)
        {
            DecodedInstruction address = instructions[index];
            if (address.Encoding == 0x7f && index + 1 < instructions.Count && instructions[index + 1] is { Encoding: 0x28 } call &&
                !targets.Contains(call.Offset) && TryStaticScalarProjection(metadata, pe, address.Token, call.Token) is { } proof)
            {
                result.Add(address with { Encoding = 0x7e, Name = "ldsfld.projected-i4", Size = address.Size + call.Size, Projection = proof });
                index++;
            }
            else result.Add(address);
        }
        return result;
    }

    private static StaticScalarProjection? TryStaticScalarProjection(MetadataReader metadata, PEReader pe, int fieldToken, int methodToken)
    {
        if (MetadataTokens.EntityHandle(fieldToken).Kind != HandleKind.FieldDefinition ||
            MetadataTokens.EntityHandle(methodToken).Kind != HandleKind.MethodDefinition) return null;
        FieldDefinitionHandle fieldHandle = (FieldDefinitionHandle)MetadataTokens.EntityHandle(fieldToken);
        MethodDefinitionHandle methodHandle = (MethodDefinitionHandle)MetadataTokens.EntityHandle(methodToken);
        FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
        if ((field.Attributes & (FieldAttributes.Static | FieldAttributes.InitOnly)) != (FieldAttributes.Static | FieldAttributes.InitOnly)) return null;
        BlobReader fieldSig = metadata.GetBlobReader(field.Signature);
        if (fieldSig.RemainingBytes < 3 || fieldSig.ReadByte() != 0x06 || fieldSig.ReadByte() != 0x11) return null;
        EntityHandle valueHandle = ReadTypeDefOrRefEncoded(ref fieldSig);
        if (fieldSig.RemainingBytes != 0 || valueHandle.Kind != HandleKind.TypeDefinition || valueHandle.IsNil) return null;
        TypeDefinitionHandle typeHandle = (TypeDefinitionHandle)valueHandle;
        TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
        if (!type.GetFields().Contains(fieldHandle) || !type.GetMethods().Contains(methodHandle) || type.GetGenericParameters().Count != 0 ||
            ExactTypeHandleName(metadata, type.BaseType) != "System.ValueType" || new InlineStorageResolver([]).Value(metadata, typeHandle) is not { Size: 4, Alignment: 4 }) return null;
        MethodDefinition getter = metadata.GetMethodDefinition(methodHandle);
        byte[] signature = metadata.GetBlobBytes(getter.Signature);
        if ((getter.Attributes & (MethodAttributes.Static | MethodAttributes.Virtual | MethodAttributes.PinvokeImpl | MethodAttributes.Abstract)) != 0 ||
            (getter.ImplAttributes & MethodImplAttributes.Synchronized) != 0 || getter.GetGenericParameters().Count != 0 ||
            getter.RelativeVirtualAddress == 0 || signature.Length != 3 || signature[0] != 0x20 || signature[1] != 0 || signature[2] is not (0x08 or 0x09)) return null;
        MethodBodyBlock body = pe.GetMethodBody(getter.RelativeVirtualAddress);
        if (body.Size > 64) return null;
        byte[] il = body.GetILBytes() ?? [];
        if (body.ExceptionRegions.Length != 0 || !body.LocalSignature.IsNil || il.Length != 7 || il[0] != 0x02 || il[1] != 0x7b || il[6] != 0x2a) return null;
        int innerToken = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(2, 4));
        if (MetadataTokens.EntityHandle(innerToken).Kind != HandleKind.FieldDefinition) return null;
        FieldDefinitionHandle[] instanceFields = type.GetFields().Where(handle => !metadata.GetFieldDefinition(handle).Attributes.HasFlag(FieldAttributes.Static)).ToArray();
        if (instanceFields.Length != 1 || MetadataTokens.GetToken(instanceFields[0]) != innerToken) return null;
        byte[] innerSignature = metadata.GetBlobBytes(metadata.GetFieldDefinition(instanceFields[0]).Signature);
        if (innerSignature.Length != 2 || innerSignature[0] != 0x06 || innerSignature[1] != signature[2]) return null;
        return new(FullTypeName(metadata, typeHandle), methodToken, innerToken, HashBytes(il));
    }
}
