using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    // Set only on a synchronous primary BodyWorld child which is not an entry root.
    private bool _nullableInt32ParameterIngress;
    private bool _nullableInt32ReturnIngress;
    private bool _nullableInt32ReturnCalls;

    private static bool IsExactNullableInt32Return(MetadataReader metadata,
        System.Reflection.PortableExecutable.PEReader pe, MethodDefinitionHandle handle, MethodSignature signature,
        IReadOnlyList<RestrictedCilGenericArgumentV1> typeArguments, IReadOnlyList<RestrictedCilGenericArgumentV1> methodArguments)
    {
        if (signature.Status != SignatureStatus.Success || signature.HasThis ||
            signature.ReturnType != RestrictedCilTypeV1.Int64 || signature.AggregateReturn != NullableInt32Identity ||
            signature.Parameters.Count < 1 || signature.Parameters.Count > HybridCpuNativeAbiContractV2.Default.ArgumentRegisters.Count ||
            signature.Parameters.Any(type => type is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.Int64)) ||
            signature.AggregateParameters?.Select((identity, index) => identity is null || index == 0 && identity == NullableInt32Identity)
                .Any(allowed => !allowed) == true) return false;
        MethodDefinition method = metadata.GetMethodDefinition(handle);
        TypeDefinition owner = metadata.GetTypeDefinition(method.GetDeclaringType());
        bool ExactContext(GenericParameterHandleCollection parameters, IReadOnlyList<RestrictedCilGenericArgumentV1> arguments, bool methodContext = false) =>
            parameters.Count == arguments.Count && arguments.Count <= 1 &&
            arguments.All(argument => argument == new RestrictedCilGenericArgumentV1("System.Int32", RestrictedCilTypeV1.Int32) ||
                argument == new RestrictedCilGenericArgumentV1("System.Int64", RestrictedCilTypeV1.Int64)) &&
            (parameters.All(parameter => metadata.GetGenericParameter(parameter).Attributes == System.Reflection.GenericParameterAttributes.None &&
                metadata.GetGenericParameter(parameter).GetConstraints().Count == 0) ||
                methodContext && IsExactNullableInt32GenericCopyConstraint(metadata, method, typeArguments, methodArguments));
        return (method.Attributes & System.Reflection.MethodAttributes.MemberAccessMask) == System.Reflection.MethodAttributes.Private &&
            method.Attributes.HasFlag(System.Reflection.MethodAttributes.Static) && method.RelativeVirtualAddress != 0 &&
            (method.ImplAttributes & (System.Reflection.MethodImplAttributes.Synchronized | System.Reflection.MethodImplAttributes.InternalCall |
                System.Reflection.MethodImplAttributes.Runtime | System.Reflection.MethodImplAttributes.ManagedMask | System.Reflection.MethodImplAttributes.CodeTypeMask)) == 0 &&
            ExactContext(method.GetGenericParameters(), methodArguments, methodContext: true) && IsExactSignedOwnerContext(metadata, owner, typeArguments) &&
            !owner.GetMethods().Any(h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == ".cctor") &&
            pe.GetMethodBody(method.RelativeVirtualAddress).ExceptionRegions.Length == 0;
    }

    private bool IsPureNullableInt32ReturnBody(IReadOnlyList<DecodedInstruction> instructions,
        MetadataReader metadata, System.Reflection.PortableExecutable.PEReader pe,
        MethodSignature signature, LocalSignature locals,
        RestrictedCilProvenanceV1 provenance, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A packed nominal value is not a scalar induction counter or bound.
        // Only the existing exact primitive induction proof admits backedges;
        // production CFG/SSA still validates the original signature and rows.
        RestrictedCilTypeV1[] counterTypes = locals.Types.Select((type, index) =>
            locals.Aggregates?.ElementAtOrDefault(index) is null ? type : RestrictedCilTypeV1.Aggregate).ToArray();
        RestrictedCilTypeV1[] bounds = signature.Parameters.Select((type, index) =>
            signature.AggregateParameters?.ElementAtOrDefault(index) is null ? type : RestrictedCilTypeV1.Aggregate).ToArray();
        HashSet<(int Source, int Target)>? countedEdges = NullableInt32CountedBackedges(instructions,
            localTypes: counterTypes, immutableBoundParameters: bounds, allowConstantInduction: true);
        cancellationToken.ThrowIfCancellationRequested();
        return countedEdges is not null && instructions.All(row => row.Encoding == 0x28
            ? IsPureLocalConstructorArgumentCall(metadata, pe, row.Token, provenance, cancellationToken)
            : (row.Encoding is (>= 0x00 and <= 0x0e or 0x10 or 0x11 or 0x13 or >= 0x15 and <= 0x21 or
            0x25 or 0x26 or >= 0x2a and <= 0x45 or 0x58 or 0x59 or 0x5a or >= 0x5f and <= 0x6a or
            0x6d or 0x6e or >= 0xfe01 and <= 0xfe05 or NullableInt32New or NullableInt32Zero or NullableInt32Initialize or NullableInitializeStack or
            NullableInt32Value or NullableInt32HasValue or NullableInt32Default or NullableDefaultStack) ||
            _nullableInt32ParameterIngress && row.Encoding is NullableInt32ParameterValue or NullableInt32ParameterPresence) &&
            !row.NullableWide && !row.NullableUnsigned && row.NullableEnumIdentity is null && !row.NullableBoolean && !row.NullableChar && row.NullableNarrowBits == 0 &&
            ControlFlowTargets(row).All(target => target > row.Offset || countedEdges.Contains((row.Offset, target))));
    }
    private const ushort NullableInt32ParameterValue = 0xff19, NullableInt32ParameterPresence = 0xff1a;

    private bool IsExactNullableInt32ParameterGetter(MetadataReader metadata,
        System.Reflection.PortableExecutable.PEReader pe, MethodDefinitionHandle handle, MethodSignature signature,
        RestrictedCilProvenanceV1 provenance, CancellationToken cancellationToken, out RestrictedCilImportResultV1? qualificationFailure, Func<int, bool>? dependencyProof = null)
    {
        qualificationFailure = null;
        if (signature.Status != SignatureStatus.Success || signature.HasThis || signature.Parameters.Count is not (1 or 2) ||
            signature.Parameters[0] != RestrictedCilTypeV1.Int64 || signature.AggregateParameters?.ElementAtOrDefault(0) != NullableInt32Identity ||
            signature.AggregateReturn is not null || signature.ReturnType is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.Int64)) return false;
        bool dynamicDefault = signature.Parameters.Count == 2;
        if (dynamicDefault && !IsNullableInt32FallbackParameter(signature)) return false;
        MethodDefinition method = metadata.GetMethodDefinition(handle);
        TypeDefinition owner = metadata.GetTypeDefinition(method.GetDeclaringType());
        if ((method.Attributes & System.Reflection.MethodAttributes.MemberAccessMask) != System.Reflection.MethodAttributes.Private ||
            !method.Attributes.HasFlag(System.Reflection.MethodAttributes.Static) || method.RelativeVirtualAddress == 0 ||
            method.GetGenericParameters().Count != 0 || owner.GetGenericParameters().Count != 0 ||
            owner.GetMethods().Any(h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == ".cctor")) return false;
        MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress);
        byte[] il = body.GetILBytes() ?? [];
        if (il.Length > _budgets.MaximumMethodBodyBytes) return false;
        bool wide = signature.ReturnType == RestrictedCilTypeV1.Int64;
        if (body.ExceptionRegions.Length == 0 && IsNullableInt32ComputedDefaultSignature(signature) &&
            (!body.LocalSignature.IsNil || il.Length >= 9 && il[0] == 0x0f && il[1] == 0 && il[2] == 0x28 &&
             NullableInt32Member(metadata, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(3, 4))) == NullableInt32HasValue && il[7] is 0x2c or 0x2d or 0x39 or 0x3a))
            return IsExactNullableInt32Coalescing(metadata, pe, body, signature, provenance, cancellationToken,
                out qualificationFailure, dependencyProof);
        if (body.ExceptionRegions.Length != 0 || !body.LocalSignature.IsNil || il.Length < 8 ||
            il[0] != 0x0f || il[1] != 0) return false;
        if (TryNullableInt32ParameterDefault(metadata, il, 2, signature, out int defaultCall))
            return il.AsSpan(defaultCall + 5).SequenceEqual(wide ? new byte[] { 0x6a, 0x2a } : new byte[] { 0x2a });
        if (dynamicDefault || il[2] != 0x28) {
            DecodeResult decoded = Decode(il, provenance, metadata, pe, allowProjection: false, scalarSignature: signature, allowNullableParameterAddress: true);
            if (decoded.Failure is not null) {
                if (decoded.Failure.Status == RestrictedCilImportStatusV1.BudgetExhausted) qualificationFailure = decoded.Failure;
                return false;
            }
            bool valid = TryNullableInt32ParameterComputedDefault(metadata, pe, decoded.Instructions,
                signature, provenance, cancellationToken, out _, out RestrictedCilImportResultV1? failure, dependencyProof: dependencyProof);
            if (failure?.Status == RestrictedCilImportStatusV1.BudgetExhausted) qualificationFailure = failure;
            return valid;
        }
        if (il[2] != 0x28) return false;
        ushort member = NullableInt32Member(metadata, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(3, 4)));
        return member == NullableInt32Value && il.Length == (wide ? 9 : 8) && il[^1] == 0x2a && (!wide || il[^2] == 0x6a) ||
            member == NullableInt32HasValue && wide && il.AsSpan(7).SequenceEqual(new byte[] { 0x16, 0xfe, 0x03, 0x6a, 0x2a });
    }

    private bool IsExactNullableInt32Coalescing(MetadataReader metadata, System.Reflection.PortableExecutable.PEReader pe,
        System.Reflection.Metadata.MethodBodyBlock body, MethodSignature signature, RestrictedCilProvenanceV1 provenance,
        CancellationToken cancellationToken, out RestrictedCilImportResultV1? failure, Func<int, bool>? dependencyProof)
    {
        failure = null;
        LocalSignature locals = ParseLocalSignature(metadata, body.LocalSignature, [], [], allowAggregates: true);
        bool parameterReads = locals.Status == SignatureStatus.Success &&
            locals.Types.All(type => type is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.Int64) && locals.Aggregates?.Any(value => value is not null) != true;
        if (!parameterReads && (locals.Status != SignatureStatus.Success || locals.Types.Count != 1 ||
            locals.Types[0] != RestrictedCilTypeV1.Int64 || locals.Aggregates?.SingleOrDefault() != NullableInt32Identity)) return false;
        DecodeResult decoded = Decode(body.GetILBytes() ?? [], provenance, metadata, pe, allowProjection: true,
            scalarSignature: signature, projectionLocals: locals);
        if (decoded.Failure is not null) {
            if (decoded.Failure.Status == RestrictedCilImportStatusV1.BudgetExhausted) failure = decoded.Failure;
            return false;
        }
        IReadOnlyList<DecodedInstruction> rows = decoded.Instructions;
        bool wide = signature.ReturnType == RestrictedCilTypeV1.Int64;
        // Roslyn either copies the immutable packed argument for coalescing or
        // reads it directly for explicit presence/payload conditionals. Both
        // shapes test presence and evaluate fallback only on the absent edge.
        // Retain both real branches and offsets; eager-default projection cannot
        // represent this language contract.
        bool presentFirst = parameterReads && rows.Count > 1 && rows[1].Encoding is 0x2c or 0x39;
        int expressionStart = parameterReads ? 2 : 4;
        if (rows.Count < expressionStart + (wide ? 5 : 4)) return false;
        int payload = presentFirst ? 2 : parameterReads ? rows.ToList().FindIndex(row => row.Offset == rows[1].BranchTarget) : rows.Count - (wide ? 3 : 2);
        if (payload < 2 || payload >= rows.Count - (wide ? 2 : 1)) return false;
        if (presentFirst) expressionStart = rows.ToList().FindIndex(row => row.Offset == rows[1].BranchTarget);
        if (expressionStart <= payload && presentFirst) return false;
        int fallbackEnd = presentFirst ? rows.Count - 1 : payload - 1;
        bool earlyReturn = parameterReads && rows[fallbackEnd].Encoding == 0x2a;
        int expressionEnd = fallbackEnd - (earlyReturn && wide ? 1 : 0);
        int payloadReturn = presentFirst ? expressionStart - 1 : rows.Count - 1;
        int payloadEnd = payloadReturn - (wide && rows[payloadReturn - 1].Encoding == 0x6a ? 1 : 0);
        bool exactPresence = parameterReads ?
            rows[0].Encoding == NullableInt32ParameterPresence && rows[0].Literal == 0 :
            rows[0].Encoding == 0x02 && rows[1].Encoding == 0x0a &&
            rows[2].Encoding == NullableInt32HasValue && rows[2].Literal == 0;
        if (!exactPresence ||
            (presentFirst ? rows[1].BranchTarget != rows[expressionStart].Offset || rows[expressionStart - 1].Encoding != 0x2a :
                rows[expressionStart - 1].Encoding is not (0x2d or 0x3a) || rows[expressionStart - 1].BranchTarget != rows[payload].Offset) ||
            (earlyReturn ? wide && rows[expressionEnd].Encoding != 0x6a :
                rows[fallbackEnd].Encoding is not (0x2b or 0x38) || rows[fallbackEnd].BranchTarget != rows[payloadEnd].Offset) ||
            rows[payload].Encoding != (parameterReads ? NullableInt32ParameterValue : NullableInt32Value) || rows[payload].Literal != 0 ||
            wide && !parameterReads && rows[payloadEnd].Encoding != 0x6a || rows[^1].Encoding != 0x2a ||
            !parameterReads && payloadEnd != payload + 1) return false;
        if (parameterReads && !ProveNullableInt32Expression(metadata, pe, body, signature, provenance, cancellationToken,
            rows.Skip(payload).Take(payloadEnd - payload).ToList(), rows[payloadEnd], -1, true, out failure, dependencyProof)) return false;
        List<DecodedInstruction> expression = rows.Skip(expressionStart).Take(expressionEnd - expressionStart).ToList();
        return ProveNullableInt32Expression(metadata, pe, body, signature, provenance, cancellationToken,
            expression, rows[expressionEnd], earlyReturn ? rows[expressionEnd].Offset : rows[payloadEnd].Offset,
            false, out failure, dependencyProof);
    }

    private bool ProveNullableInt32Expression(MetadataReader metadata, System.Reflection.PortableExecutable.PEReader pe,
        System.Reflection.Metadata.MethodBodyBlock body, MethodSignature signature, RestrictedCilProvenanceV1 provenance,
        CancellationToken cancellationToken, List<DecodedInstruction> expression, DecodedInstruction exit, int commonExit,
        bool presentPayload, out RestrictedCilImportResultV1? failure, Func<int, bool>? dependencyProof)
    {
        failure = null;
        LocalSignature declaredLocals = ParseLocalSignature(metadata, body.LocalSignature, [], [], allowAggregates: true);
        bool primitiveLocals = declaredLocals.Status == SignatureStatus.Success &&
            declaredLocals.Types.All(type => type is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.Int64) && declaredLocals.Aggregates?.Any(value => value is not null) != true;
        LocalSignature proofLocals = presentPayload || primitiveLocals ? declaredLocals : new LocalSignature(SignatureStatus.Success, [], "");
        if (proofLocals.Status != SignatureStatus.Success || proofLocals.Types.Any(type => type is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.Int64)) ||
            proofLocals.Aggregates?.Any(value => value is not null) == true) return false;
        if (proofLocals.Types.Count > _budgets.MaximumLocals) {
            failure = Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2008", "The local-variable budget was exhausted.", provenance.MethodIdentity, provenance);
            return false;
        }
        HashSet<(int Source, int Target)>? countedEdges = presentPayload ? NullableInt32CountedBackedges(expression) : [];
        if (countedEdges is null) return false;
        if (presentPayload && proofLocals.Types.Contains(RestrictedCilTypeV1.Int64) && countedEdges.Count == 0) return false;
        if (countedEdges.Count != 0) {
            int latch = expression.FindIndex(row => row.Offset == countedEdges.Single().Source);
            int counter = NullableInt32LocalIndex(expression[latch - 2], false);
            if (counter < 0 || counter >= proofLocals.Types.Count || proofLocals.Types[counter] != RestrictedCilTypeV1.Int32) return false;
        }
        bool widePresent = presentPayload && countedEdges.Count != 0 && signature.ReturnType == RestrictedCilTypeV1.Int64;
        if (presentPayload && signature.ReturnType == RestrictedCilTypeV1.Int64 && !widePresent && exit.Encoding != 0x6a) return false;
        HashSet<int> boundaries = expression.Select(row => row.Offset).Append(exit.Offset).ToHashSet();
        HashSet<int> expressionTargets = expression.SelectMany(ControlFlowTargets).ToHashSet();
        HashSet<int> proven = new HashSet<int>();
        for (int index = 0; index < expression.Count; index++) {
            DecodedInstruction row = expression[index];
            cancellationToken.ThrowIfCancellationRequested();
            if (ControlFlowTargets(row).Any(offset => offset <= row.Offset && !countedEdges.Contains((row.Offset, offset)) || offset != commonExit && !boundaries.Contains(offset))) return false;
            // Roslyn may send either conditional arm straight to the common
            // conversion/return, bypassing the last absent-edge jump. Redirect
            // that exact exit only in the expression proof; retain real edges
            // for production CFG, source maps, budgets and lowering.
            if (commonExit >= 0 && row.BranchTarget == commonExit)
                expression[index] = row with { BranchTarget = exit.Offset };
            // Every counted-body return is checked at its actual scalar width.
            // Ret interiors remain untargeted; no widening is erased in this proof.
            if (presentPayload && countedEdges.Count != 0 && row.Encoding == 0x2a) {
                if (expressionTargets.Contains(row.Offset)) return false;
                continue;
            }
            if (presentPayload && row.Encoding == NullableInt32ParameterValue && row.Literal == 0) continue;
            if (proofLocals.Types.Count != 0 && row.Encoding is >= 0x06 and <= 0x0d or 0x11 or 0x13) continue;
            if (row.Encoding == 0x28) {
                // Counted wide expressions may call bounded signed-scalar helpers,
                // including an I4 selector with two I8 values. Definition purity
                // and typed argument/return checking remain separate admission.
                // Keep eager/absent defaults and I4-return regions at their old
                // widths; the definition proof below still checks nominal
                // signatures, selected bodies, effects and the original budgets.
                if (!_managedCallTargets.TryGetValue(row.Token, out RestrictedCilManagedCallTargetV1? target) || target.Receiver is not null ||
                    target.AggregateReturn is not null ||
                    !(target.ReturnType == RestrictedCilTypeV1.Int32 && target.ParameterTypes.Count is 1 or 2 &&
                        target.ParameterTypes.All(type => type == RestrictedCilTypeV1.Int32) ||
                      widePresent && target.ReturnType == RestrictedCilTypeV1.Int64 &&
                        target.ParameterTypes.Count is >= 1 and <= 3 && target.ParameterTypes.All(IsPrimitiveConstructorInput)) ||
                    target.AggregateParameters?.Any(value => value is not null) == true) return false;
                // Discovery is not admission. Each exact selected definition must
                // pass the existing bounded call-owned purity/identity proof.
                if (proven.Add(row.Token) && !(dependencyProof?.Invoke(row.Token) ??
                    IsPureLocalConstructorArgumentCall(metadata, pe, row.Token, provenance, cancellationToken))) return false;
                continue;
            }
            // Primitive locals use the existing typed initialization/merge proof
            // above. No opaque receivers, argument stores, memory or escaping loans.
            if (!((row.Encoding == 0x03 || row.Encoding == 0x0e && row.Literal == 1) && IsNullableInt32FallbackParameter(signature) ||
                row.Encoding is 0x00 or >= 0x15 and <= 0x21 or 0x25 or 0x26 or >= 0x2b and <= 0x44 or
                    >= 0x58 and <= 0x6a or 0x6d or 0x6e or 0xd1 or 0xd2 or 0xd3 or >= 0xfe01 and <= 0xfe05)) return false;
        }
        // Counted present expressions retain their actual I8 conversion/return.
        // Absent/default proofs still require an I4 result before exact widening.
        // Production offsets/instructions and whole-method budgets are unchanged.
        if (widePresent) {
            if (exit.Encoding is not (0x6a or 0x2a)) return false;
            expression.Add(exit);
            if (exit.Encoding == 0x6a) expression.Add(exit with { Offset = checked(exit.Offset + exit.Size), Size = 1,
                Encoding = 0x2a, Name = "nullable-expression-proof.ret", BranchTarget = -1 });
        } else expression.Add(exit with { Encoding = 0x2a, Name = "nullable-expression-proof.ret", BranchTarget = -1 });
        V2GraphBuild graph = BuildV2Graph(expression, provenance);
        if (graph.Failure is not null) {
            if (graph.Failure.Status == RestrictedCilImportStatusV1.BudgetExhausted) failure = graph.Failure;
            return false;
        }
        V2LoopBuild loops = AnalyzeV2Loops(graph.Graph!, provenance);
        if (loops.Failure is not null) {
            if (loops.Failure.Status == RestrictedCilImportStatusV1.BudgetExhausted) failure = loops.Failure;
            return false;
        }
        V2DataflowBuild proof = RunV2Dataflow(metadata, graph.Graph!, signature with { ReturnType = widePresent ? RestrictedCilTypeV1.Int64 : RestrictedCilTypeV1.Int32, AggregateReturn = null },
            proofLocals, body.MaxStack, provenance, new Dictionary<int, ManagedReceiverCallerStoragePlanV1>());
        cancellationToken.ThrowIfCancellationRequested();
        if (proof.Failure?.Status == RestrictedCilImportStatusV1.BudgetExhausted) failure = proof.Failure;
        return proof.Failure is null && proof.Dataflow!.Helpers.Values.All(helper => helper.IsManagedCall);
    }

    private static bool IsNullableInt32FallbackParameter(MethodSignature signature) =>
        signature.Parameters.Count == 2 && signature.Parameters[1] == RestrictedCilTypeV1.Int32 &&
        signature.AggregateParameters?.ElementAtOrDefault(1) is null;

    private static bool IsNullableInt32MapDefaultInput(MethodSignature signature, DecodedInstruction input)
    {
        if (signature.AggregateReturn != NullableInt32Identity ||
            signature.AggregateParameters?.ElementAtOrDefault(0) != NullableInt32Identity) return false;
        if (input.Encoding is >= 0x15 and <= 0x20) return true;
        int parameter = input.Encoding is >= 0x02 and <= 0x05 ? input.Encoding - 0x02 :
            input.Encoding == 0x0e ? checked((int)input.Literal) : -1;
        return parameter > 0 && parameter < signature.Parameters.Count &&
            signature.Parameters[parameter] == RestrictedCilTypeV1.Int32 &&
            signature.AggregateParameters?.ElementAtOrDefault(parameter) is null;
    }

    private static int NullableInt32LocalIndex(DecodedInstruction row, bool store) => store ?
        row.Encoding is >= 0x0a and <= 0x0d ? row.Encoding - 0x0a : row.Encoding == 0x13 ? checked((int)row.Literal) : -1 :
        row.Encoding is >= 0x06 and <= 0x09 ? row.Encoding - 0x06 : row.Encoding == 0x11 ? checked((int)row.Literal) : -1;

    private static HashSet<(int Source, int Target)>? NullableInt32CountedBackedges(IReadOnlyList<DecodedInstruction> rows,
        MetadataReader? payloadMetadata = null, IReadOnlyList<RestrictedCilTypeV1>? localTypes = null,
        IReadOnlyList<RestrictedCilTypeV1>? immutableBoundParameters = null, bool allowConstantInduction = false)
    {
        (int Source, int Target)[] backward = rows.SelectMany(row => ControlFlowTargets(row).Where(target => target <= row.Offset)
            .Select(target => (Source: row.Offset, Target: target))).ToArray();
        if (backward.Length == 0) return [];
        if (backward.Length != 1) return allowConstantInduction ? CompositeCountedBackedges(rows, localTypes, immutableBoundParameters) : null;
        (int Source, int Target) edge = backward[0];
        if (allowConstantInduction && localTypes is not null) {
            int rawLatch = rows.ToList().FindIndex(row => row.Offset == edge.Source);
            int boundStart = rawLatch - 1;
            if (boundStart >= 0 && rows[boundStart].Encoding is 0x6a or 0x6e) boundStart--;
            int rawCounter = boundStart > 0 ? NullableInt32LocalIndex(rows[boundStart - 1], false) : -1;
            if (rawCounter >= 0 && rawCounter < localTypes.Count && localTypes[rawCounter] is RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64) {
                IReadOnlyList<DecodedInstruction>? normalized = NormalizeWideInductionConstants(rows);
                if (normalized is null) return null;
                rows = normalized;
            }
        }
        int latch = rows.ToList().FindIndex(row => row.Offset == edge.Source);
        int body = rows.ToList().FindIndex(row => row.Offset == edge.Target);
        if (body < 3 || latch < body + 6) return null;
        int counter = NullableInt32LocalIndex(rows[latch - 2], false);
        bool wideUnsigned = allowConstantInduction && counter >= 0 && localTypes is not null &&
            counter < localTypes.Count && localTypes[counter] == RestrictedCilTypeV1.UInt64;
        bool wideSigned = allowConstantInduction && counter >= 0 && localTypes is not null &&
            counter < localTypes.Count && localTypes[counter] == RestrictedCilTypeV1.Int64;
        bool wide = wideUnsigned || wideSigned;
        bool unsigned = wideUnsigned || allowConstantInduction && counter >= 0 && localTypes is not null &&
            counter < localTypes.Count && localTypes[counter] == RestrictedCilTypeV1.UInt32;
        bool inclusive = allowConstantInduction && (unsigned ? rows[latch].Encoding is 0x34 or 0x41 or 0x36 or 0x43 : rows[latch].Encoding is 0x2f or 0x3c or 0x31 or 0x3e);
        bool descending = allowConstantInduction && (unsigned ? rows[latch].Encoding is 0x35 or 0x42 or 0x34 or 0x41 : rows[latch].Encoding is 0x30 or 0x3d or 0x2f or 0x3c);
        if (!descending && !inclusive && !(unsigned ? rows[latch].Encoding is 0x37 or 0x44 : rows[latch].Encoding is 0x32 or 0x3f)) return null;
        if (!TryInductionConstant(rows[body - 3], unsigned, wide, out System.Numerics.BigInteger seed) || seed != 0 && !allowConstantInduction) return null;
        if (!TryInductionConstant(rows[latch - 5], unsigned, wide, out System.Numerics.BigInteger stride)) return null;
        if (stride <= 0 ||
            stride != 1 && !allowConstantInduction) return null;
        if (TryInductionConstant(rows[latch - 1], unsigned, wide, out System.Numerics.BigInteger bound)) {
            if (bound < 0 && !allowConstantInduction) return null;
            // Exact proof arithmetic includes zero-trip and the full U8 range.
            // Both directions must reach their bound before a carrier wrap.
            System.Numerics.BigInteger distance = System.Numerics.BigInteger.Max(0, (descending ? seed - bound : bound - seed) + (inclusive ? 1 : 0));
            System.Numerics.BigInteger change = (distance + stride - 1) / stride * stride;
            System.Numerics.BigInteger final = descending ? seed - change : seed + change;
            System.Numerics.BigInteger maximum = unsigned ? (wide ? ulong.MaxValue : uint.MaxValue) : wide ? long.MaxValue : int.MaxValue;
            if (final < (unsigned ? 0L : wide ? long.MinValue : int.MinValue) || final > maximum) return null;
        } else {
            // An unconstrained inclusive parameter bound may require a final
            // update beyond Int32.MinValue/MaxValue. Keep that separate gate.
            if (stride != 1 || inclusive || unsigned) return null;
            DecodedInstruction limit = rows[latch - 1];
            int argument = limit.Encoding is >= 0x02 and <= 0x05 ? limit.Encoding - 0x02 :
                limit.Encoding == 0x0e ? checked((int)limit.Literal) : -1;
            if (immutableBoundParameters is null || argument < 0 || argument >= immutableBoundParameters.Count ||
                immutableBoundParameters[argument] != (wideSigned ? RestrictedCilTypeV1.Int64 : RestrictedCilTypeV1.Int32) ||
                rows.Any(row => row.Encoding is 0x0f or 0x10 && row.Literal == argument)) return null;
        }
        if (counter < 0 || localTypes is not null && (counter >= localTypes.Count || localTypes[counter] != (wideUnsigned ? RestrictedCilTypeV1.UInt64 : wideSigned ? RestrictedCilTypeV1.Int64 : unsigned ? RestrictedCilTypeV1.UInt32 : RestrictedCilTypeV1.Int32)) ||
            NullableInt32LocalIndex(rows[latch - 3], true) != counter || rows[latch - 4].Encoding != (descending ? 0x59 : 0x58) ||
            NullableInt32LocalIndex(rows[latch - 6], false) != counter ||
            rows[body - 1].Encoding is not (0x2b or 0x38) || rows[body - 1].BranchTarget != rows[latch - 2].Offset ||
            NullableInt32LocalIndex(rows[body - 2], true) != counter) return null;
        // Exactly two counter stores, no address escape/backedge in the body,
        // and only the canonical preheader may enter the condition. This proves
        // a terminating signed counter with an I4-representable final
        // increment (unit stride for parameter bounds, rounded constant otherwise);
        // no work is proportional to the trip count and no unrolling is performed.
        if (rows.Where(row => NullableInt32LocalIndex(row, true) == counter).Count() != 2) return null;
        if (latch + 1 >= rows.Count) return null;
        int bodyOffset = rows[body].Offset, incrementOffset = rows[latch - 6].Offset,
            exitOffset = rows[latch + 1].Offset;
        for (int index = body; index < latch - 6; index++) {
            DecodedInstruction row = rows[index];
            // Forward body CFG may merge at another body instruction or at the
            // start of the complete induction update, or the exact loop exit.
            // A break only reduces the statically bounded iteration count.
            if (ControlFlowTargets(row).Any(target => target <= row.Offset || target > incrementOffset && target != exitOffset) ||
                row.Encoding == 0x12) return null;
            // Raw guarded discovery may see the same exact parameter/getter pair
            // on each iteration. It remains a nonescaping loan; the whole region
            // must still pass the dominating-presence/untargeted-pair checks below.
            // The typed expression proof sees only projected I4 payload reads.
            if (row.Encoding == 0x0f && (payloadMetadata is null || row.Literal != 0 ||
                index + 1 >= latch - 6 || rows[index + 1].Encoding != 0x28 ||
                NullableInt32Member(payloadMetadata, rows[index + 1].Token) is not (NullableCheckedValue or NullableInt32Value))) return null;
        }
        foreach (DecodedInstruction row in rows) foreach (int target in ControlFlowTargets(row)) {
            if (row.Offset == edge.Source && target == edge.Target) continue;
            if (row.Offset >= bodyOffset && row.Offset < incrementOffset &&
                target > row.Offset && (target <= incrementOffset || target == exitOffset)) continue;
            if (target >= rows[body - 2].Offset && target <= rows[latch].Offset &&
                !(row.Offset == rows[body - 1].Offset && target == rows[latch - 2].Offset)) return null;
        }
        return [edge];
    }

    private static HashSet<(int Source, int Target)>? CompositeCountedBackedges(IReadOnlyList<DecodedInstruction> rows,
        IReadOnlyList<RestrictedCilTypeV1>? localTypes, IReadOnlyList<RestrictedCilTypeV1>? parameters)
    {
        if (localTypes?.Any(type => type is RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64) == true) {
            IReadOnlyList<DecodedInstruction>? normalized = NormalizeWideInductionConstants(rows);
            if (normalized is null) return null;
            rows = normalized;
        }
        (int Source, int Target)[] edges = rows.SelectMany(row => ControlFlowTargets(row).Where(target => target <= row.Offset)
            .Select(target => (Source: row.Offset, Target: target))).OrderBy(edge => edge.Target).ToArray();
        // Collapse only exact, untargeted signed ancestor scale/offset expressions in
        // this termination abstraction. Full decode/typed SSA still sees every
        // operand and operation, including its original stack/call budgets.
        Dictionary<int, (System.Numerics.BigInteger Scale, System.Numerics.BigInteger Adjustment)> boundExpressions = [];
        HashSet<int> targets = rows.SelectMany(ControlFlowTargets).ToHashSet();
        List<DecodedInstruction> boundRows = rows.ToList();
        foreach (var edge in edges) {
            int latch = boundRows.FindIndex(row => row.Offset == edge.Source);
            if (latch < 3 || boundRows[latch - 1].Encoding is not (0x58 or 0x59 or 0x5a)) continue;
            bool offset = boundRows[latch - 1].Encoding is 0x58 or 0x59;
            int expressionEnd = offset ? latch - 2 : latch;
            if (expressionEnd < 1) return null;
            bool multiply = boundRows[expressionEnd - 1].Encoding == 0x5a;
            int start = multiply ? expressionEnd - 3 : expressionEnd - 1;
            if (start < 0 || !multiply && !offset) return null;
            DecodedInstruction local = boundRows[start];
            DecodedInstruction? factor = null;
            if (multiply) {
                factor = boundRows[start + 1];
                // Both source operand orders retain the same exact semantics.
                if (NullableInt32LocalIndex(local, false) < 0) (local, factor) = (factor, local);
            }
            int boundLocal = NullableInt32LocalIndex(local, false);
            if (localTypes is null || boundLocal < 0 || boundLocal >= localTypes.Count ||
                localTypes[boundLocal] is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.Int64) ||
                boundRows.Skip(start + 1).Take(latch - start - 1).Any(row => targets.Contains(row.Offset))) return null;
            bool wideBound = localTypes[boundLocal] == RestrictedCilTypeV1.Int64;
            System.Numerics.BigInteger scale = 1, adjustment = 0;
            if (factor is not null && !TryInductionConstant(factor, false, wideBound, out scale) ||
                offset && !TryInductionConstant(boundRows[latch - 2], false, wideBound, out adjustment)) return null;
            if (offset && boundRows[latch - 1].Encoding == 0x59) adjustment = -adjustment;
            int originalOffset = boundRows[start].Offset;
            boundExpressions.Add(originalOffset, (scale, adjustment));
            boundRows[start] = local with { Offset = originalOffset,
                Size = checked(boundRows.Skip(start).Take(latch - start).Sum(row => row.Size)) };
            boundRows.RemoveRange(start + 1, latch - start - 1);
        }
        rows = boundRows;
        List<(int Start, int Body, int End, int Increment, int Exit, int StartIndex, int Latch, int Target)> regions = [];
        foreach ((int source, int target) in edges) {
            int body = rows.ToList().FindIndex(row => row.Offset == target);
            int latch = rows.ToList().FindIndex(row => row.Offset == source);
            if (body < 3 || latch < body + 6 || latch + 1 >= rows.Count) return null;
            int start = body - 3;
            regions.Add((rows[start].Offset, target, source, rows[latch - 6].Offset, rows[latch + 1].Offset, start, latch, target));
        }
        // A loop forest may contain disjoint phases or strictly nested children.
        // A child's complete seed through latch must lie inside its parent's body,
        // before the complete parent induction update; crossing cycles reject.
        for (int first = 0; first < regions.Count; first++) for (int second = first + 1; second < regions.Count; second++) {
            var outer = regions[first]; var inner = regions[second];
            if (outer.Start > inner.Start) (outer, inner) = (inner, outer);
            if (outer.End < inner.Start) continue;
            if (outer.Start == inner.Start || inner.End >= outer.End ||
                inner.Start < outer.Body || inner.End >= outer.Increment) return null;
        }
        HashSet<(int Source, int Target)> proven = [];
        Dictionary<int, (int Counter, System.Numerics.BigInteger Minimum, System.Numerics.BigInteger Maximum)> domains = [];
        foreach (var region in regions) {
            Dictionary<int, int> childExits = regions.Where(child => child.Start > region.Start && child.End < region.End)
                .ToDictionary(child => child.End, child => child.Exit);
            // Termination abstraction only: a proven child's backedge goes to
            // its exact exit for the parent's induction check. All child stores
            // remain, so an inner write to the parent's counter still rejects.
            // Whole-method typed CFG/SSA sees the original instructions/edges.
            DecodedInstruction[] slice = rows.Skip(region.StartIndex).Take(region.Latch + 2 - region.StartIndex)
                .Select(row => childExits.TryGetValue(row.Offset, out int exit) ? row with { BranchTarget = exit } : row).ToArray();
            int counter = NullableInt32LocalIndex(rows[region.Latch - 2], false);
            if (localTypes is null || counter < 0 || counter >= localTypes.Count) return null;
            bool wide = localTypes[counter] is RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64;
            bool unsigned = localTypes[counter] is RestrictedCilTypeV1.UInt32 or RestrictedCilTypeV1.UInt64;
            bool descending = unsigned ? rows[region.Latch].Encoding is 0x35 or 0x42 or 0x34 or 0x41 : rows[region.Latch].Encoding is 0x30 or 0x3d or 0x2f or 0x3c;
            int boundLocal = NullableInt32LocalIndex(rows[region.Latch - 1], false);
            if (boundLocal >= 0) {
                // Only a proven enclosing induction variable supplies a local
                // bound. It is invariant throughout this child invocation.
                var ancestors = regions.Where(parent => parent.Start < region.Start && parent.End > region.End &&
                    domains.TryGetValue(parent.End, out var domain) && domain.Counter == boundLocal).ToArray();
                if (ancestors.Length != 1 || localTypes[boundLocal] != localTypes[counter] ||
                    slice.Any(row => NullableInt32LocalIndex(row, true) == boundLocal) ||
                    rows.Any(row => row.Encoding == 0x12 && row.Literal == boundLocal)) return null;
                var domain = domains[ancestors[0].End];
                var expression = boundExpressions.TryGetValue(rows[region.Latch - 1].Offset, out var boundExpression) ?
                    boundExpression : (Scale: System.Numerics.BigInteger.One, Adjustment: System.Numerics.BigInteger.Zero);
                var first = domain.Minimum * expression.Scale;
                var last = domain.Maximum * expression.Scale;
                var minimum = System.Numerics.BigInteger.Min(first, last);
                var maximum = System.Numerics.BigInteger.Max(first, last);
                System.Numerics.BigInteger maximumCarrier = unsigned ? (wide ? ulong.MaxValue : uint.MaxValue) : wide ? long.MaxValue : int.MaxValue;
                // Even unchecked source arithmetic must not wrap: a wrapped
                // bound is not an affine monotone function of the ancestor.
                if (minimum < (unsigned ? 0L : wide ? long.MinValue : int.MinValue) ||
                    maximum > maximumCarrier) return null;
                // Check multiplication before addition: a cancelling offset
                // must never hide overflow in an intermediate IL operation.
                minimum += expression.Adjustment;
                maximum += expression.Adjustment;
                if (minimum < (unsigned ? 0L : wide ? long.MinValue : int.MinValue) ||
                    maximum > maximumCarrier) return null;
                System.Numerics.BigInteger limit = descending ? minimum : maximum;
                int boundIndex = slice.Length - 3;
                // Substitute the extremal bound only in termination proof;
                // actual evaluation/CFG/SSA retains the original local read.
                slice[boundIndex] = slice[boundIndex] with { Encoding = wide ? (ushort)0x21 : (ushort)0x20,
                    Literal = unsigned ? unchecked((long)(ulong)limit) : (long)limit };
            }
            if (slice.SelectMany(row => ControlFlowTargets(row).Where(offset => offset <= row.Offset)).Count() != 1) return null;
            HashSet<(int Source, int Target)>? proof = NullableInt32CountedBackedges(slice,
                localTypes: localTypes, immutableBoundParameters: parameters, allowConstantInduction: true);
            if (proof is null || proof.Count != 1 || !proof.Contains((region.End, region.Target))) return null;
            if (TryInductionConstant(slice[0], unsigned, wide, out var seed) &&
                TryInductionConstant(slice[^3], unsigned, wide, out var limitValue) &&
                TryInductionConstant(slice[^7], unsigned, wide, out var stride) && stride > 0) {
                bool inclusive = unsigned ? rows[region.Latch].Encoding is 0x34 or 0x41 or 0x36 or 0x43 : rows[region.Latch].Encoding is 0x2f or 0x3c or 0x31 or 0x3e;
                var distance = System.Numerics.BigInteger.Max(0, (descending ? seed - limitValue : limitValue - seed) + (inclusive ? 1 : 0));
                var change = (distance + stride - 1) / stride * stride;
                var final = descending ? seed - change : seed + change;
                domains.Add(region.End, (counter, System.Numerics.BigInteger.Min(seed, final), System.Numerics.BigInteger.Max(seed, final)));
            }
            DecodedInstruction bound = rows[region.Latch - 1];
            int argument = bound.Encoding is >= 0x02 and <= 0x05 ? bound.Encoding - 0x02 : bound.Encoding == 0x0e ? checked((int)bound.Literal) : -1;
            if (argument >= 0 && rows.Any(row => row.Encoding is 0x0f or 0x10 && row.Literal == argument)) return null;
            proven.Add((region.End, region.Target));
        }
        foreach (var region in regions) foreach (DecodedInstruction row in rows) {
            if (row.Offset >= region.Start && row.Offset <= region.End) continue;
            // Every iteration can enter a child only at its complete seed.
            // Neither parent nor sibling edges may bypass child initialization.
            if (ControlFlowTargets(row).Any(target => target > region.Start && target <= region.End)) return null;
        }
        return proven;
    }

    private static bool TryInductionConstant(DecodedInstruction row, bool unsigned, bool wide, out System.Numerics.BigInteger value)
    {
        value = 0;
        if (wide) {
            if (row.Encoding != 0x21) return false;
            value = unsigned ? unchecked((ulong)row.Literal) : row.Literal;
            return true;
        }
        if (!TryGetInt32Constant(row, out int raw)) return false;
        value = unsigned ? unchecked((uint)raw) : raw;
        return true;
    }

    // Proof-only folding of exact untargeted ldc.i4/conv.i8 or conv.u8 pairs.
    // Production decode, stack budgets, CFG and source offsets remain original.
    private static IReadOnlyList<DecodedInstruction>? NormalizeWideInductionConstants(IReadOnlyList<DecodedInstruction> rows)
    {
        HashSet<int> targets = rows.SelectMany(ControlFlowTargets).ToHashSet();
        List<DecodedInstruction> result = [];
        for (int index = 0; index < rows.Count; index++) {
            DecodedInstruction row = rows[index];
            if (TryGetInt32Constant(row, out int raw) && index + 1 < rows.Count && rows[index + 1].Encoding is 0x6a or 0x6e) {
                DecodedInstruction conversion = rows[++index];
                if (targets.Contains(conversion.Offset)) return null;
                row = row with { Encoding = 0x21, Literal = conversion.Encoding == 0x6a ? raw : unchecked((uint)raw), Size = checked(row.Size + conversion.Size) };
            }
            result.Add(row);
        }
        return result;
    }

    private static bool IsNullableInt32ComputedDefaultSignature(MethodSignature signature) =>
        signature.AggregateParameters?.ElementAtOrDefault(0) == NullableInt32Identity &&
        (signature.Parameters.Count == 1 || IsNullableInt32FallbackParameter(signature));

    private static IReadOnlyList<DecodedInstruction> ProjectExactGenericInt32LocalZero(MetadataReader metadata,
        IReadOnlyList<DecodedInstruction> instructions, MethodSignature signature, LocalSignature? locals, bool exactScalarMethod = false)
    {
        if (locals is null ||
            signature.ExactMethodArguments is not { Count: 1 } arguments ||
            !(arguments[0] == new RestrictedCilGenericArgumentV1("System.Int32", RestrictedCilTypeV1.Int32) ||
                exactScalarMethod && arguments[0] == new RestrictedCilGenericArgumentV1("System.Int64", RestrictedCilTypeV1.Int64)) ||
            !(signature.AggregateReturn == NullableInt32Identity || exactScalarMethod &&
                signature.AggregateReturn is null && signature.ReturnType == arguments[0].CarrierType)) return instructions;
        RestrictedCilTypeV1 carrier = arguments[0].CarrierType;
        HashSet<int> targets = instructions.SelectMany(ControlFlowTargets).ToHashSet();
        List<DecodedInstruction> result = instructions.ToList();
        for (int index = 0; index + 2 < instructions.Count; index++) {
            DecodedInstruction address = instructions[index], zero = instructions[index + 1], load = instructions[index + 2];
            if (address.Encoding != 0x12) continue;
            int local = checked((int)address.Literal);
            int loaded = load.Encoding switch { >= 0x06 and <= 0x09 => load.Encoding - 0x06, 0x11 => checked((int)load.Literal), _ => -1 };
            if (address.Encoding != 0x12 || local < 0 || local >= locals.Types.Count || loaded != local ||
                locals.Types[local] != carrier || locals.Aggregates?.ElementAtOrDefault(local) is not null ||
                zero.Encoding != 0xfe15 || targets.Contains(zero.Offset) || targets.Contains(load.Offset)) continue;
            EntityHandle type = MetadataTokens.EntityHandle(zero.Token);
            if (type.Kind != HandleKind.TypeSpecification ||
                !metadata.GetBlobBytes(metadata.GetTypeSpecification((TypeSpecificationHandle)type).Signature).AsSpan().SequenceEqual(new byte[] { 0x1e, 0 })) continue;
            // Preserve the local write, row count, IL offsets and original budgets.
            // Only this immediately consumed exact signed scalar loan becomes zero/store.
            result[index] = address with { Encoding = carrier == RestrictedCilTypeV1.Int64 ? (ushort)0x21 : (ushort)0x16,
                Name = "ldc.scalar.0.generic-default", Literal = 0, Token = 0 };
            result[index + 1] = zero with { Encoding = 0x13, Name = "stloc.generic-default", Literal = local, Token = 0 };
            index++;
        }
        return result;
    }

    private static bool TryNullableInt32ParameterDefault(MetadataReader metadata, ReadOnlySpan<byte> il,
        int constantOffset, MethodSignature signature, out int callOffset)
    {
        callOffset = constantOffset;
        if (constantOffset < 0 || constantOffset >= il.Length) return false;
        int size = IsNullableInt32FallbackParameter(signature) ? il[constantOffset] == 0x03 ? 1 : 0 :
            signature.Parameters.Count == 1 ? il[constantOffset] switch { >= 0x15 and <= 0x1e => 1, 0x1f => 2, 0x20 => 5, _ => 0 } : 0;
        if (size == 0 || il.Length - constantOffset < size + 5) return false;
        callOffset += size;
        return il[callOffset] == 0x28 && NullableInt32Member(metadata,
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(il.Slice(callOffset + 1, 4)), methodArguments: signature.ExactMethodArguments) == NullableInt32Default;
    }

    private bool TryNullableInt32ParameterComputedDefault(MetadataReader metadata, System.Reflection.PortableExecutable.PEReader pe,
        IReadOnlyList<DecodedInstruction> instructions, MethodSignature signature, RestrictedCilProvenanceV1 provenance,
        CancellationToken cancellationToken, out int getter, out RestrictedCilImportResultV1? failure,
        bool deferCalls = false, Func<int, bool>? dependencyProof = null)
    {
        getter = -1; failure = null;
        if (!IsNullableInt32ComputedDefaultSignature(signature) || instructions.Count < 4 || instructions[0].Encoding != 0x0f || instructions[0].Literal != 0) return false;
        int call = 1;
        Dictionary<int, bool> pureCalls = new Dictionary<int, bool>();
        bool hasCalls = false;
        for (; call < instructions.Count; call++) {
            cancellationToken.ThrowIfCancellationRequested();
            DecodedInstruction row = instructions[call];
            if (row.Encoding == 0x28 && NullableInt32Member(metadata, row.Token) == NullableInt32Default) break;
            if (row.Encoding == 0x28 && MetadataTokens.EntityHandle(row.Token).Kind is HandleKind.MethodDefinition or HandleKind.MemberReference or HandleKind.MethodSpecification) {
                EntityHandle handle = MetadataTokens.EntityHandle(row.Token);
                IReadOnlyList<RestrictedCilGenericArgumentV1> typeArguments = [];
                IReadOnlyList<RestrictedCilGenericArgumentV1> methodArguments = [];
                if (handle.Kind == HandleKind.MethodSpecification) {
                    MethodSpecificationHandle specification = (MethodSpecificationHandle)handle;
                    if (!TryParseMethodSpecificationArguments(metadata, specification, [], [], out methodArguments, out _) ||
                        methodArguments.Count != 1 || methodArguments[0] != new RestrictedCilGenericArgumentV1("System.Int32", RestrictedCilTypeV1.Int32)) return false;
                    handle = metadata.GetMethodSpecification(specification).Method;
                    // Primary generic definitions remain a separate admission gate.
                    if (handle.Kind != HandleKind.MemberReference) return false;
                }
                BlobHandle signatureBlob;
                if (handle.Kind == HandleKind.MethodDefinition) {
                    MethodDefinition method = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
                    if ((method.Attributes & System.Reflection.MethodAttributes.MemberAccessMask) != System.Reflection.MethodAttributes.Private) return false;
                    signatureBlob = method.Signature;
                } else {
                    MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)handle);
                    if (member.GetKind() != MemberReferenceKind.Method) return false;
                    EntityHandle parent = member.Parent;
                    if (parent.Kind == HandleKind.TypeSpecification) {
                        if (!TryParseConstructedTypeSpecification(metadata, (TypeSpecificationHandle)parent,
                            [], [], out _, out parent, out typeArguments, out _) || typeArguments.Count != 1 ||
                            typeArguments[0] != new RestrictedCilGenericArgumentV1("System.Int32", RestrictedCilTypeV1.Int32)) return false;
                    }
                    if (parent.Kind != HandleKind.TypeReference ||
                        ReferencedAssemblyName(metadata, (TypeReferenceHandle)parent) is not { } assembly ||
                        assembly == metadata.GetString(metadata.GetAssemblyDefinition().Name)) return false;
                    signatureBlob = member.Signature;
                }
                // Signature decoding preserves discovery only. Final admission still
                // requires the call-owned, budgeted exact selected-definition proof.
                MethodSignature helperSignature = ParseMethodSignature(metadata, signatureBlob, typeArguments, methodArguments, allowAggregates: true);
                if (helperSignature.Status != SignatureStatus.Success || helperSignature.HasThis || helperSignature.ReturnType != RestrictedCilTypeV1.Int32 ||
                    helperSignature.AggregateReturn is not null || helperSignature.Parameters.Count is not (1 or 2) ||
                    helperSignature.Parameters.Any(parameter => parameter != RestrictedCilTypeV1.Int32) || helperSignature.AggregateParameters?.Any(value => value is not null) == true) return false;
                hasCalls = true;
                if (!deferCalls && !pureCalls.ContainsKey(row.Token)) {
                    bool valid = dependencyProof?.Invoke(row.Token) ?? IsPureLocalConstructorArgumentCall(metadata, pe, row.Token, provenance, cancellationToken);
                    if (!valid) return false;
                    pureCalls.Add(row.Token, true);
                }
                continue;
            }
            // Borrowed receiver is not an expression value. No stores or memory;
            // typed CFG proof below owns stack/types, forward edges and fault freedom.
            if (!((row.Encoding == 0x03 || row.Encoding == 0x0e && row.Literal == 1) && IsNullableInt32FallbackParameter(signature) ||
                row.Encoding is 0x00 or >= 0x15 and <= 0x21 or 0x25 or 0x26 or >= 0x2b and <= 0x44 or
                    >= 0x58 and <= 0x6a or 0x6d or 0x6e or 0xd1 or 0xd2 or 0xd3 or >= 0xfe01 and <= 0xfe05)) return false;
        }
        bool wide = signature.ReturnType == RestrictedCilTypeV1.Int64;
        if (call >= instructions.Count || instructions.Count != call + (wide ? 3 : 2) ||
            instructions[^1].Encoding != 0x2a || wide && instructions[call + 1].Encoding != 0x6a) return false;
        // Discovery retains call edges; it cannot grant final body admission.
        if (deferCalls && hasCalls) { getter = call; return true; }
        return TryInt64ConstructorArgument(metadata, pe, instructions, 0, new(SignatureStatus.Success, [], ""), signature,
            NullableInt32Identity, pureCalls, provenance, cancellationToken, out getter, out failure, nullableDefault: true, requirePure: true);
    }

    private static bool IsGuardedNullableInt32ParameterPayload(MetadataReader metadata,
        IReadOnlyList<DecodedInstruction> instructions, int payload, MethodSignature signature, LocalSignature? locals = null)
    {
        // A nullable return uses an I8 packed carrier, but its guarded payload
        // remains I4 and is wrapped by the exact nullable constructor.
        bool wide = signature.ReturnType == RestrictedCilTypeV1.Int64 && signature.AggregateReturn != NullableInt32Identity;
        if (instructions.Count < 8 || payload < 3 ||
            instructions[0].Encoding != 0x0f || instructions[0].Literal != 0 ||
            instructions[1].Encoding != 0x28 || NullableInt32Member(metadata, instructions[1].Token) != NullableInt32HasValue ||
            instructions[payload].Literal != 0 ||
            instructions[^1].Encoding != 0x2a) return false;
        bool presentFirst = instructions[2].Encoding is 0x2c or 0x39;
        int requestedPayload = payload;
        payload = presentFirst ? 3 : instructions.ToList().FindIndex(row => row.Offset == instructions[2].BranchTarget);
        if (payload < 3) return false;
        int expressionStart = presentFirst ? instructions.ToList().FindIndex(row => row.Offset == instructions[2].BranchTarget) : 3;
        int expressionEnd = presentFirst ? instructions.Count : payload;
        if (expressionStart < 0 || presentFirst && expressionStart <= payload + 2 || expressionStart >= expressionEnd || payload + (wide ? 3 : 2) >= instructions.Count) return false;
        bool commonMerge = false;
        if (presentFirst) {
            if (payload != 3 || instructions[2].BranchTarget != instructions[expressionStart].Offset ||
                instructions[expressionStart - 1].Encoding != 0x2a ||
                wide && instructions[^2].Encoding != 0x6a) return false;
        } else {
            if (payload < 5 ||
                instructions[2].Encoding is not (0x2d or 0x3a) || instructions[2].BranchTarget != instructions[payload].Offset) return false;
            bool earlyReturn = instructions[payload - 1].Encoding == 0x2a &&
                (!wide || instructions[payload - 2].Encoding == 0x6a);
            commonMerge = instructions[payload - 1].Encoding is 0x2b or 0x38 &&
                instructions[payload - 1].BranchTarget == instructions[instructions.Count - (wide ? 2 : 1)].Offset;
            if (!earlyReturn && !commonMerge) return false;
        }
        int presentEnd = presentFirst ? expressionStart : instructions.Count;
        if (requestedPayload < payload || requestedPayload >= presentEnd) return false;
        HashSet<int> presentOffsets = instructions.Skip(payload).Take(presentEnd - payload).Select(row => row.Offset).ToHashSet();
        DecodedInstruction[] presentRows = instructions.Skip(payload).Take(presentEnd - payload).ToArray();
        HashSet<(int Source, int Target)>? countedEdges = signature.AggregateReturn == NullableInt32Identity && locals is not null
            ? NullableInt32CountedBackedges(presentRows, metadata,
                localTypes: locals.Types.Select((type, index) => locals.Aggregates?.ElementAtOrDefault(index) is null ? type : RestrictedCilTypeV1.Aggregate).ToArray(),
                immutableBoundParameters: signature.Parameters.Select((type, index) => signature.AggregateParameters?.ElementAtOrDefault(index) is null ? type : RestrictedCilTypeV1.Aggregate).ToArray(),
                allowConstantInduction: true)
            : NullableInt32CountedBackedges(presentRows, metadata);
        if (countedEdges is null) return false;
        if (wide && countedEdges.Count == 0 && instructions[presentEnd - 2].Encoding != 0x6a) return false;
        HashSet<int> targets = instructions.SelectMany(ControlFlowTargets).ToHashSet();
        for (int index = payload; index < presentEnd; index++) {
            DecodedInstruction row = instructions[index];
            if (ControlFlowTargets(row).Any(target => target <= row.Offset && !countedEdges.Contains((row.Offset, target)) || !presentOffsets.Contains(target))) return false;
            if (row.Encoding != 0x0f) continue;
            // Each repeated loan must be the same immutable nominal parameter
            // and end immediately at an exact getter. A branch may target the
            // pair's address load, never its borrowed-receiver call interior.
            if (row.Literal != 0 || index + 1 >= presentEnd || instructions[index + 1].Encoding != 0x28 ||
                targets.Contains(instructions[index + 1].Offset) ||
                NullableInt32Member(metadata, instructions[index + 1].Token) is not (NullableCheckedValue or NullableInt32Value)) return false;
            index++;
        }
        // Only the true presence edge may enter this exact payload read. The
        // absent region cannot loop, re-enter the guard, or reach the payload;
        // its typed/fault-free expression and selected calls are proved later
        // by the shared lazy qualifier. Parameter mutation is checked below.
        IEnumerable<DecodedInstruction> expression = instructions.Skip(expressionStart).Take(expressionEnd - expressionStart);
        HashSet<int> exits = expression.Select(row => row.Offset).ToHashSet();
        if (commonMerge) exits.Add(instructions[instructions.Count - (wide ? 2 : 1)].Offset);
        return expression.All(row =>
            ControlFlowTargets(row).All(target => target > row.Offset && exits.Contains(target)));
    }

    private DecodeResult ProjectNullableInt32ParameterReads(MetadataReader metadata, System.Reflection.PortableExecutable.PEReader pe, IReadOnlyList<DecodedInstruction> instructions,
        MethodSignature signature, RestrictedCilProvenanceV1 provenance, bool deferCalls = false, LocalSignature? locals = null)
    {
        HashSet<int> targets = instructions.SelectMany(ControlFlowTargets).ToHashSet();
        HashSet<long> mutated = instructions.Where(row => row.Encoding == 0x10).Select(row => row.Literal).ToHashSet();
        bool nullableMap = signature.AggregateReturn == NullableInt32Identity;
        HashSet<int> initializerLoans = new HashSet<int>();
        bool constructorAfter = false;
        if (nullableMap) for (int part = instructions.Count - 1; part >= 0; part--) {
            DecodedInstruction candidate = instructions[part];
            if (candidate.Encoding == 0x0f && candidate.Literal == 0 && part + 1 < instructions.Count &&
                instructions[part + 1].Encoding == 0xfe15 && IsNullableInt32Type(metadata, MetadataTokens.EntityHandle(instructions[part + 1].Token), methodArguments: signature.ExactMethodArguments)) mutated.Add(0);
            if (candidate.Encoding == 0x28 && NullableInt32Member(metadata, candidate.Token, methodArguments: signature.ExactMethodArguments) == NullableInt32New) constructorAfter = true;
            if (constructorAfter && candidate.Encoding == 0x0f && candidate.Literal == 0 &&
                signature.AggregateParameters?.ElementAtOrDefault(0) == NullableInt32Identity &&
                !(part + 1 < instructions.Count && instructions[part + 1].Encoding == 0x28 &&
                  NullableInt32Member(metadata, instructions[part + 1].Token, methodArguments: signature.ExactMethodArguments) is NullableInt32Value or NullableInt32HasValue or NullableCheckedValue)) {
                initializerLoans.Add(candidate.Offset);
                mutated.Add(0);
            }
        }
        List<DecodedInstruction> result = new List<DecodedInstruction>(instructions.Count);
        for (int index = 0; index < instructions.Count; index++) {
            DecodedInstruction row = instructions[index];
            if (row.Encoding != 0x0f || signature.AggregateParameters?.ElementAtOrDefault((int)row.Literal) != NullableInt32Identity) { result.Add(row); continue; }
            if (nullableMap && row.Literal == 0 && index + 1 < instructions.Count && instructions[index + 1].Encoding == 0xfe15 &&
                IsNullableInt32Type(metadata, MetadataTokens.EntityHandle(instructions[index + 1].Token), methodArguments: signature.ExactMethodArguments) && !targets.Contains(instructions[index + 1].Offset)) {
                result.Add(row with { Encoding = NullableInt32Zero, Name = "nullable-map.zero-argument", NullableParameter = true,
                    Size = row.Size + instructions[++index].Size, ErasedReceiverDepth = row.ErasedReceiverDepth + 1 });
                continue;
            }
            if (index + 2 < instructions.Count && (signature.Parameters.Count == 1 && instructions[index + 1].Encoding is >= 0x15 and <= 0x20 ||
                IsNullableInt32FallbackParameter(signature) && instructions[index + 1].Encoding == 0x03 ||
                IsNullableInt32MapDefaultInput(signature, instructions[index + 1])) &&
                instructions[index + 2].Encoding == 0x28 && NullableInt32Member(metadata, instructions[index + 2].Token, methodArguments: signature.ExactMethodArguments) == NullableInt32Default) {
                if (targets.Contains(instructions[index + 1].Offset) || targets.Contains(instructions[index + 2].Offset) ||
                    !nullableMap && (mutated.Contains(row.Literal) || instructions[index + 1].Encoding == 0x03 && mutated.Contains(1)))
                    return new([], Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1847", "Nullable default ingress must be immutable and untargeted.", OffsetIdentity(provenance, row.Offset), provenance));
                DecodedInstruction input = instructions[index + 1]; DecodedInstruction call = instructions[index + 2];
                result.Add(row with { Encoding = NullableInt32Default, Name = "nullable-int32.parameter-default",
                    NullableParameter = true, NullableDefault = input, Size = row.Size + input.Size + call.Size, ErasedReceiverDepth = 1 });
                index += 2;
                continue;
            }
            ushort member = index + 1 < instructions.Count && instructions[index + 1].Encoding == 0x28 ?
                NullableInt32Member(metadata, instructions[index + 1].Token, methodArguments: signature.ExactMethodArguments) : (ushort)0;
            bool guardedPayload = member == NullableCheckedValue &&
                IsGuardedNullableInt32ParameterPayload(metadata, instructions, index, signature, locals);
            bool exactGetter = member is NullableInt32Value or NullableInt32HasValue || guardedPayload;
            if (!exactGetter && initializerLoans.Contains(row.Offset)) { result.Add(row); continue; }
            if (nullableMap && !exactGetter && locals is not null) {
                if (TryInt64ConstructorArgument(metadata, pe, instructions, index, locals, signature, NullableInt32Identity,
                    new Dictionary<int, bool>(), provenance, default, out int defaultEnd, out RestrictedCilImportResultV1? defaultFailure,
                    nullableDefault: true, requirePure: true, deferCalls: deferCalls)) {
                    result.Add(row with { Encoding = 0x00, Name = "nullable-map.erased-default-address", ErasedReceiverDepth = row.ErasedReceiverDepth + 1 });
                    for (int part = index + 1; part < defaultEnd; part++) result.Add(instructions[part] with { ErasedReceiverDepth = instructions[part].ErasedReceiverDepth + 1 });
                    result.Add(instructions[defaultEnd] with { Encoding = NullableDefaultStack, Name = "nullable-map.default-stack", Literal = row.Literal, NullableParameter = true });
                    index = defaultEnd;
                    continue;
                }
                if (defaultFailure is not null) return new([], defaultFailure);
            }
            if (index == 0 && !exactGetter && (IsNullableInt32FallbackParameter(signature) || signature.Parameters.Count == 1 && instructions.Count > 1 && instructions[1].Encoding != 0x28)) {
                bool computed = TryNullableInt32ParameterComputedDefault(metadata, pe, instructions, signature, provenance, default, out int getter, out RestrictedCilImportResultV1? failure, deferCalls);
                if (failure is not null) return new([], failure);
                if (!computed) return new([], Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1847", "Nullable computed default requires a fault-free immutable primitive argument region.", OffsetIdentity(provenance, row.Offset), provenance));
                result.Add(row with { Encoding = 0x00, Name = "nullable-parameter.erased-address", ErasedReceiverDepth = 1 });
                for (int part = 1; part < getter; part++) result.Add(instructions[part] with { ErasedReceiverDepth = 1 });
                result.Add(instructions[getter] with { Encoding = NullableDefaultStack, Name = "nullable-parameter.default-stack", Literal = 0, NullableParameter = true });
                for (int part = getter + 1; part < instructions.Count; part++) result.Add(instructions[part]);
                return new(result, null);
            }
            if (!exactGetter || targets.Contains(instructions[index + 1].Offset) ||
                mutated.Contains(row.Literal) && (!nullableMap || member == NullableCheckedValue))
                return new([], Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1847", "Nullable getter ingress must be immutable and untargeted.", OffsetIdentity(provenance, row.Offset), provenance));
            bool presence = NullableInt32Member(metadata, instructions[index + 1].Token, methodArguments: signature.ExactMethodArguments) == NullableInt32HasValue;
            result.Add(row with { Encoding = presence ? NullableInt32ParameterPresence : NullableInt32ParameterValue,
                Name = presence ? "nullable-int32.parameter-presence" : "nullable-int32.parameter-value", Size = row.Size + instructions[++index].Size });
        }
        if (initializerLoans.Count != 0 && locals is not null) {
            for (int part = result.Count - 1; part >= 0; part--) {
                DecodedInstruction address = result[part];
                if (!initializerLoans.Contains(address.Offset)) continue;
                if (!TryInt64ConstructorArgument(metadata, pe, result, part, locals, signature, NullableInt32Identity,
                    new Dictionary<int, bool>(), provenance, default, out int end, out RestrictedCilImportResultV1? failure, requirePure: true, deferCalls: deferCalls)) {
                    if (failure is not null) return new([], failure);
                    continue;
                }
                result[part] = address with { Encoding = 0x00, Name = "nullable-map.erased-argument-address", ErasedReceiverDepth = address.ErasedReceiverDepth + 1 };
                for (int inner = part + 1; inner < end; inner++) result[inner] = result[inner] with { ErasedReceiverDepth = result[inner].ErasedReceiverDepth + 1 };
                result[end] = result[end] with { Encoding = NullableInitializeStack, Name = "nullable-map.initialize-argument", Literal = 0, NullableParameter = true };
            }
        }
        return new(result, null);
    }
    // Private local representation only: ((uint)value << 1) | hasValue.
    // Exact primary private I4 getter ingress reuses this carrier; no general Nullable<T> ABI.
    private const string NullableInt32Identity = "compiler-local:corelib-nullable-int32/v1";
    private const string NullableInt64Identity = "compiler-local:corelib-nullable-int64/v1";
    private const string NullableUInt32Identity = "compiler-local:corelib-nullable-uint32/v1";
    private const string NullableUInt64Identity = "compiler-local:corelib-nullable-uint64/v1";
    private const string NullableUInt16Identity = "compiler-local:corelib-nullable-uint16/v1";
    private const string NullableUInt8Identity = "compiler-local:corelib-nullable-uint8/v1";
    private const string NullableInt8Identity = "compiler-local:corelib-nullable-int8/v1";
    private const string NullableBooleanIdentity = "compiler-local:corelib-nullable-boolean/v1";
    private const string NullableCharIdentity = "compiler-local:corelib-nullable-char/v1";
    private const string NullableInt16Identity = "compiler-local:corelib-nullable-int16/v1";
    private const string NullableEnumInt32Prefix = "compiler-local:corelib-nullable-enum-int32/v1:";
    private const string NullableEnumInt64Prefix = "compiler-local:corelib-nullable-enum-int64/v1:";
    private const string NullableEnumUInt64Prefix = "compiler-local:corelib-nullable-enum-uint64/v1:";
    private const string NullableEnumUInt32Prefix = "compiler-local:corelib-nullable-enum-uint32/v1:";
    private const string NullableEnumUInt16Prefix = "compiler-local:corelib-nullable-enum-uint16/v1:";
    private const string NullableEnumInt16Prefix = "compiler-local:corelib-nullable-enum-int16/v1:";
    private const string NullableEnumUInt8Prefix = "compiler-local:corelib-nullable-enum-uint8/v1:";
    private const string NullableEnumInt8Prefix = "compiler-local:corelib-nullable-enum-int8/v1:";
    private static bool IsNullableEnumInt8(string? identity) => identity?.StartsWith(NullableEnumInt8Prefix, StringComparison.Ordinal) == true;
    private static bool IsNullableEnumUInt8(string? identity) => identity?.StartsWith(NullableEnumUInt8Prefix, StringComparison.Ordinal) == true;
    private static bool IsNullableEnumInt16(string? identity) => identity?.StartsWith(NullableEnumInt16Prefix, StringComparison.Ordinal) == true;
    private static bool IsNullableEnumUInt16(string? identity) => identity?.StartsWith(NullableEnumUInt16Prefix, StringComparison.Ordinal) == true;
    private static bool IsNullableEnumUInt32(string? identity) => identity?.StartsWith(NullableEnumUInt32Prefix, StringComparison.Ordinal) == true;
    private static bool IsUnsignedNullableEnum(string? identity) => IsNullableEnumUInt8(identity) || IsNullableEnumUInt16(identity) || IsNullableEnumUInt32(identity) || IsNullableEnumUInt64(identity);
    private static bool IsNullableEnumUInt64(string? identity) => identity?.StartsWith(NullableEnumUInt64Prefix, StringComparison.Ordinal) == true;
    private static bool IsWideNullableEnum(string? identity) => IsNullableEnumInt64(identity) || IsNullableEnumUInt64(identity);
    private static bool IsNullableEnumInt64(string? identity) => identity?.StartsWith(NullableEnumInt64Prefix, StringComparison.Ordinal) == true;
    private static bool IsNullableEnum(string? identity) => IsNullableEnumInt8(identity) || IsNullableEnumUInt8(identity) || IsNullableEnumInt16(identity) || IsNullableEnumUInt16(identity) || IsNullableEnumInt32(identity) || IsNullableEnumUInt32(identity) || IsWideNullableEnum(identity);
    private static bool IsNullableEnumInt32(string? identity) => identity?.StartsWith(NullableEnumInt32Prefix, StringComparison.Ordinal) == true;
    private static bool IsNullableLocal(string? identity) => IsNullableEnum(identity) || identity is NullableInt32Identity or NullableInt64Identity or NullableUInt32Identity or NullableUInt64Identity or NullableUInt16Identity or NullableInt16Identity or NullableUInt8Identity or NullableInt8Identity or NullableCharIdentity or NullableBooleanIdentity;
    private static int NarrowNullableBits(string? identity) => IsNullableEnumInt8(identity) || IsNullableEnumUInt8(identity) ? 8 : IsNullableEnumInt16(identity) || IsNullableEnumUInt16(identity) ? 16 : identity is NullableUInt8Identity or NullableInt8Identity or NullableBooleanIdentity ? 8 : identity is NullableUInt16Identity or NullableInt16Identity or NullableCharIdentity ? 16 : 0;
    private static bool IsNarrowNullable(string? identity) => NarrowNullableBits(identity) != 0;
    private static bool IsWideNullable(string? identity) => IsWideNullableEnum(identity) || identity is NullableInt64Identity or NullableUInt64Identity;
    private static bool IsUnsignedNullable(string? identity) => IsUnsignedNullableEnum(identity) || identity is NullableUInt32Identity or NullableUInt64Identity or NullableUInt16Identity or NullableUInt8Identity or NullableCharIdentity or NullableBooleanIdentity;
    private static string NullableIdentity(DecodedInstruction row) => row.NullableEnumIdentity ?? (row.NullableBoolean ? NullableBooleanIdentity : row.NullableChar ? NullableCharIdentity : row.NullableNarrowBits == 8 ? row.NullableUnsigned ? NullableUInt8Identity : NullableInt8Identity : row.NullableNarrowBits == 16 ? row.NullableUnsigned ? NullableUInt16Identity : NullableInt16Identity : row.NullableWide ? row.NullableUnsigned ? NullableUInt64Identity : NullableInt64Identity : row.NullableUnsigned ? NullableUInt32Identity : NullableInt32Identity);
    private static bool ValidNullableInput(V2Value value, bool wide) => value.Initialized && value.AggregateIdentity is null &&
        (wide ? value.Type is RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 : value.Type is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32);
    private const ushort NullableInt32New = 0xff10, NullableInt32Zero = 0xff11,
        NullableInt32Value = 0xff12, NullableInt32HasValue = 0xff13, NullableInt32Default = 0xff14, NullableInt32Initialize = 0xff15,
        NullableInitializeStack = 0xff16, NullableCheckedValue = 0xff17, NullableDefaultStack = 0xff18;

    // This is a selected closed Nullable<Int32> signature, not general constraint solving.
    private static bool IsExactNullableInt32GenericCopyConstraint(MetadataReader metadata, MethodDefinition method,
        IReadOnlyList<RestrictedCilGenericArgumentV1> typeArguments, IReadOnlyList<RestrictedCilGenericArgumentV1> methodArguments)
    {
        // Validate the definition's owner independently of the method's !!0.
        // A selected closed owner does not relax constraints or admit open/unsigned contexts.
        if (!IsExactSignedOwnerContext(metadata, metadata.GetTypeDefinition(method.GetDeclaringType()), typeArguments) || methodArguments.Count != 1 ||
            methodArguments[0] != new RestrictedCilGenericArgumentV1("System.Int32", RestrictedCilTypeV1.Int32) ||
            method.GetGenericParameters().Count != 1 ||
            (method.Attributes & (System.Reflection.MethodAttributes.MemberAccessMask | System.Reflection.MethodAttributes.Static)) !=
                (System.Reflection.MethodAttributes.Private | System.Reflection.MethodAttributes.Static)) return false;
        if (!IsExactStructConstraint(metadata, method.GetGenericParameters().Single())) return false;
        BlobReader reader = metadata.GetBlobReader(method.Signature);
        if (reader.RemainingBytes < 3 || reader.ReadByte() != 0x10 || reader.ReadCompressedInteger() != 1) return false;
        int count = reader.ReadCompressedInteger();
        if (count is not (1 or 2) ||
            !ReadNullableInt32(metadata, ref reader, methodArguments: methodArguments, requireGeneric: true) ||
            !ReadNullableInt32(metadata, ref reader, methodArguments: methodArguments, requireGeneric: true)) return false;
        // The optional second register carrier is exactly the same selected Int32
        // method parameter, never a second nullable/aggregate or arbitrary generic ABI.
        if (count == 2 && (reader.RemainingBytes < 2 || reader.ReadByte() != 0x1e || reader.ReadCompressedInteger() != 0)) return false;
        return reader.RemainingBytes == 0;
    }

    private static bool ReadNullableInt32(MetadataReader metadata, ref BlobReader reader, bool wide = false, bool unsigned = false, int narrowBits = 0, bool character = false, bool logical = false,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? methodArguments = null, bool requireGeneric = false)
    {
        if (narrowBits is not (0 or 8 or 16) || character && (wide || !unsigned || narrowBits != 16) ||
            logical && (character || wide || !unsigned || narrowBits != 8)) return false;
        BlobReader probe = reader;
        if (probe.RemainingBytes < 4 || probe.ReadByte() != 0x15 || probe.ReadByte() != 0x11) return false;
        EntityHandle definition = ReadTypeDefOrRefEncoded(ref probe);
        if (definition.Kind != HandleKind.TypeReference) return false;
        TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)definition);
        if (metadata.GetString(type.Namespace) != "System" || metadata.GetString(type.Name) != "Nullable`1" ||
            type.ResolutionScope.Kind != HandleKind.AssemblyReference ||
            ReferencedAssemblyName(metadata, (TypeReferenceHandle)definition) is not ("System.Private.CoreLib" or "System.Runtime" or "mscorlib") ||
            probe.ReadCompressedInteger() != 1 || probe.RemainingBytes < 1) return false;
        byte payload = probe.ReadByte();
        if (payload == 0x1e && !wide && !unsigned && narrowBits == 0 && !character && !logical) {
            if (probe.RemainingBytes == 0 || probe.ReadCompressedInteger() != 0 || methodArguments?.Count != 1 ||
                methodArguments[0] != new RestrictedCilGenericArgumentV1("System.Int32", RestrictedCilTypeV1.Int32)) return false;
        } else if (requireGeneric || payload != (logical ? 2 : character ? 3 : narrowBits == 8 ? unsigned ? 5 : 4 : narrowBits == 16 ? unsigned ? 7 : 6 : wide ? unsigned ? 0x0b : 0x0a : unsigned ? 9 : 8)) return false;
        reader = probe;
        return true;
    }

    private static bool IsNullableInt32Type(MetadataReader metadata, EntityHandle handle, bool wide = false, bool unsigned = false, int narrowBits = 0, bool character = false, bool logical = false, string? enumIdentity = null, IReadOnlyDictionary<string, ExternalEnumCarrier>? externalEnums = null,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? methodArguments = null)
    {
        if (handle.Kind != HandleKind.TypeSpecification) return false;
        BlobReader reader = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
        if (enumIdentity is not null) return wide == IsWideNullableEnum(enumIdentity) && unsigned == IsUnsignedNullableEnum(enumIdentity) && narrowBits == NarrowNullableBits(enumIdentity) && !character && !logical &&
            ReadNullableEnum(metadata, ref reader, out string? actual, externalEnums) && actual == enumIdentity && reader.RemainingBytes == 0;
        return ReadNullableInt32(metadata, ref reader, wide, unsigned, narrowBits, character, logical, methodArguments) && reader.RemainingBytes == 0;
    }

    private static ushort NullableInt32Member(MetadataReader metadata, int token, bool wide = false, bool unsigned = false, int narrowBits = 0, bool character = false, bool logical = false, string? enumIdentity = null, IReadOnlyDictionary<string, ExternalEnumCarrier>? externalEnums = null,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? methodArguments = null)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind != HandleKind.MemberReference) return 0;
        MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)handle);
        if (!IsNullableInt32Type(metadata, member.Parent, wide, unsigned, narrowBits, character, logical, enumIdentity, externalEnums, methodArguments)) return 0;
        byte[] signature = metadata.GetBlobBytes(member.Signature);
        return metadata.GetString(member.Name) switch {
            ".ctor" when signature.AsSpan().SequenceEqual(new byte[] { 0x20, 1, 1, 0x13, 0 }) => NullableInt32New,
            "GetValueOrDefault" when signature.AsSpan().SequenceEqual(new byte[] { 0x20, 0, 0x13, 0 }) => NullableInt32Value,
            "GetValueOrDefault" when signature.AsSpan().SequenceEqual(new byte[] { 0x20, 1, 0x13, 0, 0x13, 0 }) => NullableInt32Default,
            "get_HasValue" when signature.AsSpan().SequenceEqual(new byte[] { 0x20, 0, 2 }) => NullableInt32HasValue,
            "get_Value" when signature.AsSpan().SequenceEqual(new byte[] { 0x20, 0, 0x13, 0 }) => NullableCheckedValue,
            _ => 0 };
    }

    private static bool ReadNullableEnum(MetadataReader metadata, ref BlobReader reader, out string? identity,
        IReadOnlyDictionary<string, ExternalEnumCarrier>? externalEnums = null)
    {
        identity = null;
        BlobReader probe = reader;
        if (probe.RemainingBytes < 6 || probe.ReadByte() != 0x15 || probe.ReadByte() != 0x11) return false;
        EntityHandle nullable = ReadTypeDefOrRefEncoded(ref probe);
        if (nullable.Kind != HandleKind.TypeReference) return false;
        TypeReference wrapper = metadata.GetTypeReference((TypeReferenceHandle)nullable);
        if (metadata.GetString(wrapper.Namespace) != "System" || metadata.GetString(wrapper.Name) != "Nullable`1" ||
            wrapper.ResolutionScope.Kind != HandleKind.AssemblyReference ||
            ReferencedAssemblyName(metadata, (TypeReferenceHandle)nullable) is not ("System.Private.CoreLib" or "System.Runtime" or "mscorlib") ||
            probe.ReadCompressedInteger() != 1 || probe.RemainingBytes < 2 || probe.ReadByte() != 0x11) return false;
        EntityHandle handle = ReadTypeDefOrRefEncoded(ref probe);
        if (handle.Kind == HandleKind.TypeReference) {
            string? key = AggregateIdentity(metadata, handle);
            if (key is null || externalEnums is null || !externalEnums.TryGetValue(key, out ExternalEnumCarrier? declaration) ||
                declaration.Carrier is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or RestrictedCilTypeV1.UInt32 or RestrictedCilTypeV1.UInt16 or RestrictedCilTypeV1.Int16 or RestrictedCilTypeV1.UInt8 or RestrictedCilTypeV1.Int8) || declaration.NullableLocalIdentity is null) return false;
            identity = declaration.NullableLocalIdentity;
        } else identity = NullableEnumDefinitionIdentity(metadata, handle);
        if (identity is null) return false;
        reader = probe;
        return true;
    }

    private static string? NullableEnumDefinitionIdentity(MetadataReader metadata, EntityHandle handle)
    {
        if (handle.Kind != HandleKind.TypeDefinition) return null;
        RestrictedCilTypeV1 carrier = EnumCarrier(metadata, handle);
        if (carrier is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or RestrictedCilTypeV1.UInt32 or RestrictedCilTypeV1.UInt16 or RestrictedCilTypeV1.Int16 or RestrictedCilTypeV1.UInt8 or RestrictedCilTypeV1.Int8)) return null;
        TypeDefinition definition = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
        if (definition.BaseType.Kind != HandleKind.TypeReference ||
            ReferencedAssemblyName(metadata, (TypeReferenceHandle)definition.BaseType) is not ("System.Private.CoreLib" or "System.Runtime" or "mscorlib") ||
            (definition.Attributes & System.Reflection.TypeAttributes.Sealed) == 0 ||
            definition.GetGenericParameters().Count != 0 || definition.GetMethods().Count != 0) return null;
        int instanceFields = 0;
        foreach (FieldDefinitionHandle fieldHandle in definition.GetFields()) {
            FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
            if ((field.Attributes & System.Reflection.FieldAttributes.Static) != 0) {
                if ((field.Attributes & System.Reflection.FieldAttributes.Literal) == 0) return null;
                continue;
            }
            instanceFields++;
            const System.Reflection.FieldAttributes required = System.Reflection.FieldAttributes.SpecialName | System.Reflection.FieldAttributes.RTSpecialName;
            if (metadata.GetString(field.Name) != "value__" || (field.Attributes & required) != required) return null;
        }
        if (instanceFields != 1) return null;
        return (carrier == RestrictedCilTypeV1.Int8 ? NullableEnumInt8Prefix : carrier == RestrictedCilTypeV1.UInt8 ? NullableEnumUInt8Prefix : carrier == RestrictedCilTypeV1.Int16 ? NullableEnumInt16Prefix : carrier == RestrictedCilTypeV1.UInt16 ? NullableEnumUInt16Prefix : carrier == RestrictedCilTypeV1.UInt32 ? NullableEnumUInt32Prefix : carrier == RestrictedCilTypeV1.UInt64 ? NullableEnumUInt64Prefix : carrier == RestrictedCilTypeV1.Int64 ? NullableEnumInt64Prefix : NullableEnumInt32Prefix) + metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("N") + ":" +
            MetadataTokens.GetToken(handle).ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + ":" + AggregateIdentity(metadata, handle);
    }

    private static string? NullableEnumMemberIdentity(MetadataReader metadata, int token, IReadOnlyDictionary<string, ExternalEnumCarrier>? externalEnums)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind != HandleKind.MemberReference) return null;
        MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)handle);
        if (member.Parent.Kind != HandleKind.TypeSpecification) return null;
        BlobReader reader = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)member.Parent).Signature);
        return ReadNullableEnum(metadata, ref reader, out string? identity, externalEnums) && reader.RemainingBytes == 0 ? identity : null;
    }

    private IReadOnlyList<DecodedInstruction> ProjectNullableInt32(MetadataReader metadata, System.Reflection.PortableExecutable.PEReader pe,
        IReadOnlyList<DecodedInstruction> instructions, LocalSignature? locals, MethodSignature? signature, RestrictedCilProvenanceV1 provenance,
        out RestrictedCilImportResultV1? projectionFailure)
    {
        projectionFailure = null;
        if (locals is null) return instructions;
        if (locals.Aggregates is null) locals = locals with { Aggregates = new string?[locals.Types.Count] };
        if (locals.Aggregates is not { } declarations) return instructions;
        if (!declarations.Any(IsNullableLocal) &&
            !instructions.Any(row => row.Encoding == 0x73 && NullableInt32Member(metadata, row.Token, methodArguments: locals.ExactMethodArguments) == NullableInt32New)) return instructions;
        ushort Member(int token, bool wide = false, bool unsigned = false, int narrowBits = 0, bool character = false, bool logical = false, string? enumIdentity = null) =>
            NullableInt32Member(metadata, token, wide, unsigned, narrowBits, character, logical, enumIdentity, locals.EnumDeclarations, locals.ExactMethodArguments);
        HashSet<int> targets = instructions.SelectMany(ControlFlowTargets).ToHashSet();
        // Project leaf accessors before proving an enclosing default expression.
        // This is one bounded pass over the same decoder rows, not recursive
        // helper discovery. No branch may enter the consumed call instruction.
        List<DecodedInstruction> leaves = new List<DecodedInstruction>();
        for (int leaf = 0; leaf < instructions.Count; leaf++) {
            DecodedInstruction address = instructions[leaf];
            if (address.Encoding == 0x12 && address.Literal >= 0 && address.Literal < locals.Types.Count &&
                IsNullableLocal(declarations[(int)address.Literal]) && leaf + 1 < instructions.Count &&
                !targets.Contains(instructions[leaf + 1].Offset)) {
                bool leafWide = IsWideNullable(declarations[(int)address.Literal]);
                bool leafUnsigned = IsUnsignedNullable(declarations[(int)address.Literal]);
                string? leafEnum = IsNullableEnum(declarations[(int)address.Literal]) ? declarations[(int)address.Literal] : null;
                bool leafBoolean = declarations[(int)address.Literal] == NullableBooleanIdentity;
                bool leafChar = declarations[(int)address.Literal] == NullableCharIdentity;
                int leafNarrow = NarrowNullableBits(declarations[(int)address.Literal]);
                DecodedInstruction call = instructions[leaf + 1];
                ushort accessor = call.Encoding == 0x28 ? Member(call.Token, leafWide, leafUnsigned, leafNarrow, leafChar, leafBoolean, leafEnum) : (ushort)0;
                if (accessor is NullableInt32Value or NullableInt32HasValue or NullableCheckedValue) {
                    leaves.Add(address with { Encoding = accessor, Name = "nullable.local-accessor", Size = address.Size + call.Size, NullableWide = leafWide, NullableUnsigned = leafUnsigned, NullableNarrowBits = leafNarrow, NullableChar = leafChar, NullableBoolean = leafBoolean, NullableEnumIdentity = leafEnum });
                    leaf++;
                    continue;
                }
            }
            leaves.Add(address);
        }
        List<DecodedInstruction> normalized = leaves;
        instructions = normalized;
        void Replace(int first, int last, IReadOnlyList<DecodedInstruction> rows) {
            normalized.RemoveRange(first, last - first + 1);
            normalized.InsertRange(first, rows);
        }
        // Normalize inner expressions before their enclosing local loans. Each
        // original address is visited once; there is no recursive/fixpoint decoder.
        for (int index = instructions.Count - 1; index >= 0; index--) {
            DecodedInstruction row = instructions[index];
            string? enumIdentity = NullableEnumMemberIdentity(metadata, row.Token, locals.EnumDeclarations);
            bool logical = Member(row.Token, unsigned: true, narrowBits: 8, logical: true) == NullableInt32New;
            bool character = Member(row.Token, unsigned: true, narrowBits: 16, character: true) == NullableInt32New;
            bool unsignedNarrow = Member(row.Token, unsigned: true, narrowBits: 16) == NullableInt32New;
            bool unsignedByte = Member(row.Token, unsigned: true, narrowBits: 8) == NullableInt32New;
            bool enumNarrow = (IsNullableEnumInt16(enumIdentity) || IsNullableEnumUInt16(enumIdentity)) && Member(row.Token, unsigned: IsUnsignedNullableEnum(enumIdentity), narrowBits: 16, enumIdentity: enumIdentity) == NullableInt32New;
            bool enumByte = (IsNullableEnumInt8(enumIdentity) || IsNullableEnumUInt8(enumIdentity)) && Member(row.Token, unsigned: IsUnsignedNullableEnum(enumIdentity), narrowBits: 8, enumIdentity: enumIdentity) == NullableInt32New;
            int narrowBits = enumByte || logical || unsignedByte || Member(row.Token, narrowBits: 8) == NullableInt32New ? 8 :
                enumNarrow || character || unsignedNarrow || Member(row.Token, narrowBits: 16) == NullableInt32New ? 16 : 0;
            bool unsignedWide = Member(row.Token, wide: true, unsigned: true) == NullableInt32New;
            bool wide = IsWideNullableEnum(enumIdentity) && Member(row.Token, wide: true, unsigned: IsUnsignedNullableEnum(enumIdentity), enumIdentity: enumIdentity) == NullableInt32New ||
                unsignedWide || Member(row.Token, wide: true) == NullableInt32New;
            bool unsigned = IsUnsignedNullableEnum(enumIdentity) && Member(row.Token, wide: IsWideNullableEnum(enumIdentity), unsigned: true, narrowBits: NarrowNullableBits(enumIdentity), enumIdentity: enumIdentity) == NullableInt32New || logical || character || unsignedByte || unsignedNarrow || unsignedWide || Member(row.Token, unsigned: true) == NullableInt32New;
            if (row.Encoding == 0x73 && (wide || unsigned || narrowBits != 0 || enumIdentity is not null && Member(row.Token, wide: wide, unsigned: IsUnsignedNullableEnum(enumIdentity), narrowBits: narrowBits, enumIdentity: enumIdentity) == NullableInt32New || Member(row.Token) == NullableInt32New)) {
                Replace(index, index, [row with { Encoding = NullableInt32New, Name = "nullable-scalar.new", NullableWide = wide, NullableUnsigned = unsigned, NullableNarrowBits = narrowBits, NullableChar = character, NullableBoolean = logical, NullableEnumIdentity = enumIdentity }]);
                continue;
            }
            if (row.Encoding == 0x12 && row.Literal >= 0 && row.Literal < locals.Types.Count &&
                IsNullableLocal(declarations[(int)row.Literal]) && index + 1 < instructions.Count &&
                !targets.Contains(instructions[index + 1].Offset)) {
                DecodedInstruction next = instructions[index + 1];
                wide = IsWideNullable(declarations[(int)row.Literal]);
                unsigned = IsUnsignedNullable(declarations[(int)row.Literal]);
                enumIdentity = IsNullableEnum(declarations[(int)row.Literal]) ? declarations[(int)row.Literal] : null;
                logical = declarations[(int)row.Literal] == NullableBooleanIdentity;
                character = declarations[(int)row.Literal] == NullableCharIdentity;
                narrowBits = NarrowNullableBits(declarations[(int)row.Literal]);
                int callIndex = index + 2;
                if (wide && next.Encoding is >= 0x15 and <= 0x20 && callIndex < instructions.Count &&
                    instructions[callIndex].Encoding == 0x6a && !targets.Contains(instructions[callIndex].Offset)) {
                    next = next with { Encoding = 0x21, Size = next.Size + instructions[callIndex].Size };
                    callIndex++;
                }
                if (callIndex < instructions.Count && !targets.Contains(instructions[callIndex].Offset) &&
                    instructions[callIndex].Encoding == 0x28 && Member(instructions[callIndex].Token, wide, unsigned, narrowBits, character, logical, enumIdentity) is NullableInt32Default or NullableInt32New &&
                    (next.Encoding is >= 0x15 and <= 0x21 ||
                     next.Encoding is >= 0x02 and <= 0x05 or 0x0e ||
                     next.Encoding is >= 0x06 and <= 0x09 or 0x11)) {
                    bool initialize = Member(instructions[callIndex].Token, wide, unsigned, narrowBits, character, logical, enumIdentity) == NullableInt32New;
                    Replace(index, callIndex, [row with { Encoding = initialize ? NullableInt32Initialize : NullableInt32Default, Name = "nullable-int32.local-input", Size = row.Size + next.Size + instructions[callIndex].Size,
                        NullableDefault = next, ErasedReceiverDepth = row.ErasedReceiverDepth + (initialize ? 2 : 1), NullableWide = wide, NullableUnsigned = unsigned, NullableNarrowBits = narrowBits, NullableChar = character, NullableBoolean = logical, NullableEnumIdentity = enumIdentity }]);
                    continue;
                }
                ushort operation = next.Encoding == 0xfe15 && IsNullableInt32Type(metadata, MetadataTokens.EntityHandle(next.Token), wide, unsigned, narrowBits, character, logical, enumIdentity, locals.EnumDeclarations, locals.ExactMethodArguments)
                    ? NullableInt32Zero : next.Encoding == 0x28 ? Member(next.Token, wide, unsigned, narrowBits, character, logical, enumIdentity) : (ushort)0;
                if (operation is NullableInt32Zero or NullableInt32Value or NullableInt32HasValue or NullableCheckedValue) {
                    Replace(index, index + 1, [row with { Encoding = operation, Name = "nullable-int32.local", Size = row.Size + next.Size,
                        ErasedReceiverDepth = row.ErasedReceiverDepth + (operation == NullableInt32Zero ? 1 : 0), NullableWide = wide, NullableUnsigned = unsigned, NullableNarrowBits = narrowBits, NullableChar = character, NullableBoolean = logical, NullableEnumIdentity = enumIdentity }]);
                    continue;
                }
                if (signature is not null && TryInt64ConstructorArgument(metadata, pe, instructions, index, locals, signature,
                    declarations[(int)row.Literal]!, new Dictionary<int, bool>(), provenance, default,
                    out int defaultEnd, out projectionFailure, nullableDefault: true)) {
                    // Retain every argument instruction in order. The receiver is a local
                    // loan, not a first-class byref; the proof excludes stores to that local.
                    List<DecodedInstruction> result = new List<DecodedInstruction>();
                    result.Add(row with { Encoding = 0, Name = "nullable-default.erased-address", ErasedReceiverDepth = row.ErasedReceiverDepth + 1 });
                    for (int part = index + 1; part < defaultEnd; part++) result.Add(instructions[part] with { ErasedReceiverDepth = instructions[part].ErasedReceiverDepth + 1 });
                    result.Add(instructions[defaultEnd] with { Encoding = NullableDefaultStack, Name = "nullable.default-stack", Literal = row.Literal, NullableWide = wide, NullableUnsigned = unsigned, NullableNarrowBits = narrowBits, NullableChar = character, NullableBoolean = logical, NullableEnumIdentity = enumIdentity });
                    Replace(index, defaultEnd, result);
                    continue;
                }
                if (projectionFailure is not null) return instructions;
                if (signature is not null && TryInt64ConstructorArgument(metadata, pe, instructions, index, locals, signature,
                    declarations[(int)row.Literal]!, new Dictionary<int, bool>(), provenance, default, out int end, out projectionFailure)) {
                    List<DecodedInstruction> result = new List<DecodedInstruction>();
                    result.Add(row with { Encoding = 0, Name = "nullable-i8.erased-address", ErasedReceiverDepth = row.ErasedReceiverDepth + 1 });
                    for (int part = index + 1; part < end; part++) result.Add(instructions[part] with { ErasedReceiverDepth = instructions[part].ErasedReceiverDepth + 1 });
                    result.Add(instructions[end] with { Encoding = NullableInitializeStack, Name = "nullable-scalar.initialize", Literal = row.Literal, NullableWide = wide, NullableUnsigned = unsigned, NullableNarrowBits = narrowBits, NullableChar = character, NullableBoolean = logical, NullableEnumIdentity = enumIdentity });
                    Replace(index, end, result);
                    continue;
                }
                if (projectionFailure is not null) return instructions;
            }
        }
        return normalized;
    }

    private static V2Value NullableDefaultValue(DecodedInstruction row, V2State state) => row.Encoding switch {
        >= 0x15 and <= 0x20 => V2Value.Constant(row.Literal, RestrictedCilTypeV1.Int32, "nullable-default"),
        0x21 => V2Value.Constant(row.Literal, RestrictedCilTypeV1.Int64, "nullable-default"),
        >= 0x02 and <= 0x05 => state.Arguments.ElementAtOrDefault(row.Encoding - 0x02) ?? V2Value.Uninitialized,
        0x0e => state.Arguments.ElementAtOrDefault((int)row.Literal) ?? V2Value.Uninitialized,
        >= 0x06 and <= 0x09 => state.Locals.ElementAtOrDefault(row.Encoding - 0x06) ?? V2Value.Uninitialized,
        0x11 => state.Locals.ElementAtOrDefault((int)row.Literal) ?? V2Value.Uninitialized,
        _ => V2Value.Uninitialized };
}
