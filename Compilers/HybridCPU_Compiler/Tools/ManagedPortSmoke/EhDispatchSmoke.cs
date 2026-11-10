using System.Buffers.Binary;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;

internal static class EhDispatchSmoke
{
    public static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        HybridCpuManagedTypeSystemBuildV1 built = new HybridCpuManagedTypeSystemBuilderV1().Build([
            new("TestException", HybridCpuManagedTypeKindV1.Class, null, [], [])]);
        Check(built.IsSuccess, built.Reason);
        HybridCpuManagedTypeSystemV1 types = built.TypeSystem!;
        ulong exceptionType = types.Descriptors.Single().TypeId;
        HybridCpuManagedEhClauseRegistrationV1 Catch(int start, int size, int handler, int ordinal) =>
            new(HybridCpuManagedEhClauseKindV1.Catch, start, size, handler, 10, exceptionType, ordinal);
        HybridCpuManagedEhClauseRegistrationV1 Finally(int start, int size, int handler, int ordinal) =>
            new(HybridCpuManagedEhClauseKindV1.Finally, start, size, handler, 10, 0, ordinal);
        HybridCpuManagedExceptionRuntimeV1 Runtime(params HybridCpuManagedEhClauseRegistrationV1[] clauses) =>
            new(types, [new("M", 0, 300, Encode(clauses), HybridCpuManagedUnwindCodecV2.Encode(
                new(HybridCpuManagedFrameKindV1.Managed, HybridCpuManagedCfaBaseV1.StackPointer, 0, 1, null, [])))]);
        HybridCpuManagedEhFrameSnapshotV1[] Frame(int pc) => [new("M", pc, 4096, 0)];
        HybridCpuManagedExceptionRuntimeV1 rethrow = Runtime(Catch(0, 100, 120, 0), Catch(0, 200, 220, 1));
        HybridCpuManagedExceptionRuntimeV1 boundary = Runtime(Catch(0, 256, 256, 0));
        Check(boundary.Dispatch(1234, exceptionType, [new("M", 256, 4096, 0,
                InstructionPointerIsReturnAddress: true)]).Status == HybridCpuManagedExceptionStatusV1.Handled,
            "return PC at try end must search at the preceding native bundle");
        Check(boundary.Dispatch(1234, exceptionType, Frame(256)).Status == HybridCpuManagedExceptionStatusV1.UnhandledTermination,
            "a real fault PC at try end must not be biased into the try");
        Check(boundary.Dispatch(1234, exceptionType, [new("M", 0, 4096, 0,
                InstructionPointerIsReturnAddress: true)]).Status == HybridCpuManagedExceptionStatusV1.InvalidMetadata,
            "return-PC subtraction must not underflow into a valid frame");
        HybridCpuManagedExceptionDispatchResultV1 result = rethrow.Dispatch(1234, exceptionType, Frame(125), rethrow: true);
        Check(result.Status == HybridCpuManagedExceptionStatusV1.Handled && result.HandlerInstructionPointer == 220 &&
            result.UnwoundFrames == 0 && result.ExceptionReference == 1234,
            "rethrow must search enclosing catches in the current frame, preserving exception identity");
        Check(rethrow.Dispatch(1234, exceptionType, Frame(20), rethrow: true).Status == HybridCpuManagedExceptionStatusV1.InvalidMetadata,
            "rethrow outside a catch must fail closed");

        int calls = 0;
        HybridCpuManagedExceptionRuntimeV1 nested = Runtime(Finally(0, 50, 60, 0), Catch(0, 100, 120, 1));
        result = nested.Dispatch(1234, exceptionType, Frame(20), (_, pc, reference) =>
        { Check(pc == 60 && reference == 1234, "exact finally entry and exception"); calls++; return new(true); });
        Check(result.Status == HybridCpuManagedExceptionStatusV1.Handled && result.HandlerInstructionPointer == 120 &&
            calls == 1 && result.FinallyInvocations == 1, "finally inside the selected catch frame must execute before catch transfer");
        Check(nested.Dispatch(1234, exceptionType, Frame(20)).Status == HybridCpuManagedExceptionStatusV1.InvalidMetadata,
            "a finally cannot silently succeed without an execution callback");
        calls = 0;
        result = Runtime(Catch(0, 50, 60, 0), Finally(0, 200, 220, 1)).Dispatch(1234, exceptionType, Frame(20),
            (_, _, _) => { calls++; return new(true); });
        Check(result.Status == HybridCpuManagedExceptionStatusV1.Handled && calls == 0,
            "an enclosing finally must not run when transfer remains inside its try region");
        result = Runtime().Dispatch(1234, exceptionType, Frame(20));
        Check(result.Status == HybridCpuManagedExceptionStatusV1.UnhandledTermination && !result.UsedArchitecturalTrap,
            "unhandled dispatch remains a managed outcome, not an architectural trap");
        calls = 0;
        result = nested.Dispatch(1234, exceptionType, Frame(20), (_, _, _) => { calls++; return new(true, 5678, exceptionType); });
        Check(result.Status == HybridCpuManagedExceptionStatusV1.Handled && result.HandlerInstructionPointer == 120 &&
            result.ExceptionReference == 5678 && calls == 1,
            "throw from finally must search enclosing catches in the same frame from the finally PC");
        List<ulong> gcReferences = new List<ulong>();
        result = nested.Dispatch(1234, exceptionType, Frame(20), (_, _, _) => new(true, 5678, exceptionType),
            (reference, _) => { gcReferences.Add(reference); return reference != 5678; });
        Check(result.Status == HybridCpuManagedExceptionStatusV1.GcRejected && gcReferences.SequenceEqual(new ulong[] { 1234, 5678 }),
            "replacement exceptions must pass a GC/root checkpoint before dispatch resumes");

        List<string> trace = new List<string>();
        Exception original = new Exception();
        try { try { throw original; } catch { trace.Add("inner"); throw; } }
        catch (Exception caught) { Check(ReferenceEquals(original, caught), "CoreCLR rethrow identity"); trace.Add("outer"); }
        Check(trace.SequenceEqual(new[] { "inner", "outer" }), "CoreCLR same-frame rethrow oracle");
        trace.Clear();
        try { try { throw original; } finally { trace.Add("finally"); } }
        catch { trace.Add("catch"); }
        Check(trace.SequenceEqual(new[] { "finally", "catch" }), "CoreCLR finally-before-catch oracle");
        byte[] encodedEh = Encode([Catch(0, 100, 120, 0)]);
        byte[] encodedUnwind = HybridCpuManagedUnwindCodecV2.Encode(
            new(HybridCpuManagedFrameKindV1.Managed, HybridCpuManagedCfaBaseV1.StackPointer, 0, 1, null, []));
        HybridCpuImageRuntimeBootstrapDescriptorV1 bootstrap = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, "M", "M",
            codeManagerRecords: [new("M", 0, 300, "0".PadLeft(64, '0'), "1".PadLeft(64, '0'))],
            ehMethods: [new("M", 0, 300, encodedEh, encodedUnwind)]);
        HybridCpuImageRuntimeBootstrapDescriptorV1 decoded = HybridCpuManagedBootstrapEncodingV1.Decode(HybridCpuManagedBootstrapEncodingV1.Encode(bootstrap));
        Check(decoded.EhMethods is [var decodedMethod] && decodedMethod.EhInfo.SequenceEqual(encodedEh) &&
            decodedMethod.UnwindInfo.SequenceEqual(encodedUnwind),
            "bootstrap encoding must preserve exact final-PC EH and unwind-v2 payloads");
        Check(new HybridCpuManagedExceptionRuntimeV1(types, decoded.EhMethods!).Dispatch(
            1234, exceptionType, Frame(20)).Status == HybridCpuManagedExceptionStatusV1.Handled,
            "runtime must consume bootstrap-owned EH registrations without a host side table");
        Console.WriteLine("PASS EH dispatch same-frame rethrow/finally ordering and missing-executor rejection vs CoreCLR (not native execution)");
    }

    internal static byte[] Encode(IReadOnlyList<HybridCpuManagedEhClauseRegistrationV1> clauses)
    {
        byte[] bytes = new byte[12 + clauses.Count * 40];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, HybridCpuManagedEhSchemaV1.EhMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), HybridCpuManagedEhSchemaV1.SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), clauses.Count);
        for (int i = 0; i < clauses.Count; i++)
        {
            HybridCpuManagedEhClauseRegistrationV1 row = clauses[i]; int offset = 12 + i * 40;
            bytes[offset] = (byte)row.Kind;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4), row.TryStartOffsetBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 8), row.TrySizeBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 12), row.HandlerStartOffsetBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 16), row.HandlerSizeBytes);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 20), row.CatchTypeId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 28), row.Ordinal);
        }
        return bytes;
    }
}
