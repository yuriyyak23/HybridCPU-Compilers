#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>
/// Source-bound vector-segment read admission projection. The emitted HWIR models
/// only the public call's positive-size and per-class capacity decision. Queue,
/// completion and cancellation ordering is source-checked for active and Golden
/// files, but is not represented as clocked state here. No segment payload is modeled.
/// </summary>
public static class BridgeHwirVectorSegmentReadAdmission
{
    public const string ActiveSha256 = "6d7a0d7b72f2594701dd49bedb6a5a6d0eb500ea19091d1024c1d98cb556d551";
    public const string GoldenSha256 = "3c4071d73445a67785282670c01e3f99038585e93906893119ed8eef38d56495";
    public const int GoldenBlobSize = 37439;
    public const string ActiveModelCommit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
    public const string RelativePath = "HybridCPU_ISE/CloseToHSL/Memory/Timing/MemoryCycleController.cs";

    public sealed record MethodEvidence(string Name, string ActiveSha256, string GoldenSha256, bool ActiveContractMatches, bool GoldenContractMatches);

    /// <summary>
    /// Eight-entry logical request lifecycle projection. A controller-edge service
    /// result is an external observation; data bytes and physical RAM are omitted.
    /// </summary>
    public static HwValidatedDesign CompileLifecycle(byte[] activeBytes, byte[] goldenBytes,
        string baseCommit, HwToolchainIdentity toolchain, out IReadOnlyList<MethodEvidence> evidence)
    {
        _ = Compile(activeBytes, goldenBytes, baseCommit, toolchain, out evidence);
        var tree = CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(activeBytes).TrimStart('\uFEFF'));
        ClassDeclarationSyntax owner = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "MemoryCycleController");
        MethodDeclarationSyntax ingress = owner.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "TryAcceptVectorSegmentLoad");
        var position = ingress.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", baseCommit, RelativePath,
            "CircuitWeave.VectorSegmentRead.LogicalLifecycle", "HybridCPU_ISE.CloseToHSL.Memory.Timing.MemoryCycleController",
            "TryAcceptVectorSegmentLoad", Hash(Encoding.UTF8.GetBytes("vector-segment-logical-lifecycle/v1|" + ActiveSha256 + "|" + GoldenSha256)),
            HwSourceRole.Primary, position.Line + 1, position.Character + 1);
        var ports = new List<HwPortContract>();
        var nodes = new List<HwNode>();
        int serial = 0;
        var constants = new Dictionary<(ulong, int), string>();
        var expressions = new Dictionary<string, string>(StringComparer.Ordinal);
        HwBitVectorType T(int width) => new(width);
        string Input(string name, int width) { ports.Add(new(name, HwPortDirection.Input, T(width))); return name; }
        string C(ulong value, int width)
        {
            if (constants.TryGetValue((value, width), out string? old)) return old;
            string id = "k" + serial++;
            nodes.Add(new HwConstantNode(id, T(width), Convert.ToString(unchecked((long)value), 2).PadLeft(width, '0')[^width..], origin));
            constants.Add((value, width), id);
            return id;
        }
        string Op(string opcode, int width, params string[] args)
        {
            string key = opcode + "|" + width + "|" + string.Join("|", args);
            if (expressions.TryGetValue(key, out string? old)) return old;
            string id = "n" + serial++;
            nodes.Add(new HwOperationNode(id, opcode, args, T(width), Origin: origin));
            expressions.Add(key, id);
            return id;
        }
        string Eq(string a, string b) => Op("eq", 1, a, b);
        string And(string a, string b) => a == b ? a : Op("and", 1, a, b);
        string Or(string a, string b) => a == b ? a : Op("or", 1, a, b);
        string Mux(string s, string y, string n, int width) => y == n ? y : Op("mux", width, s, y, n);
        string Less64(string a, string b) => Op("ult64", 1, a, b);
        void Output(string name, string value, int width)
        { ports.Add(new(name, HwPortDirection.Output, T(width))); nodes.Add(new HwOutputDriveNode("drive_" + name, name, value, origin)); }

        string zero = C(0, 1), one = C(1, 1), z32 = C(0, 32), o32 = C(1, 32), z64 = C(0, 64), o64 = C(1, 64);
        string eventCount = Input("event_count", 32);
        string cycle = Input("cycle", 64);
        string nextId = Input("next_id", 64);
        string eventKind = Input("event_kind", 2); // 0=accept, 1=controller edge, 2=take, 3=cancel
        string requestId = Input("request_id", 64);
        string device = Input("device", 64);
        string address = Input("address", 64);
        string size = Input("size_signed", 32);
        string serviceSucceeded = Input("service_succeeded", 1); // observed backend/controller service outcome; no bytes
        string isAccept = Eq(eventKind, C(0, 2));
        string isAdvance = Eq(eventKind, C(1, 2));
        string isTake = Eq(eventKind, C(2, 2));
        string isCancel = Eq(eventKind, C(3, 2));
        string sizeSign = Eq(Op("and", 32, size, C(0x80000000, 32)), C(0x80000000, 32));
        string sizeZero = Eq(size, z32);
        string sizePositive = And(Eq(sizeSign, zero), Eq(sizeZero, zero));

        (string name, int width)[] stateFields =
        [("phase", 2), ("id", 64), ("device", 64), ("address", 64), ("size", 32), ("succeeded", 1), ("published_cycle", 64)];
        string[] names = stateFields.Select(f => f.name).ToArray();
        var slots = Enumerable.Range(0, 8).Select(i => stateFields.ToDictionary(f => f.name, f => Input("s" + i + "_" + f.name, f.width))).ToArray();
        string[] empty = slots.Select(s => Eq(s["phase"], C(0, 2))).ToArray();
        string[] queued = slots.Select(s => Eq(s["phase"], C(1, 2))).ToArray();
        string[] pending = slots.Select(s => Eq(s["phase"], C(2, 2))).ToArray();
        string[] published = slots.Select(s => Eq(s["phase"], C(3, 2))).ToArray();
        string[] freeFirst = new string[8], oldest = new string[8];
        string freeSeen = zero;
        string valid = Less64(Op("zext", 64, eventCount), C(4096, 64));
        valid = And(valid, Op("xor", 1, Eq(nextId, z64), one));
        valid = And(valid, Op("xor", 1, Less64(C(4097, 64), nextId), one));
        valid = And(valid, Less64(C(0, 64), nextId));
        valid = And(valid, Op("xor", 1, Less64(Op("add", 64, Op("zext", 64, eventCount), o64), nextId), one)); // nextId <= eventCount + 1
        valid = And(valid, Op("xor", 1, Less64(Op("zext", 64, eventCount), cycle), one)); // cycle <= event_count
        valid = And(valid, Or(Or(isAccept, isAdvance), Or(isTake, isCancel)));
        for (int i = 0; i < 8; i++)
        {
            freeFirst[i] = And(empty[i], Op("xor", 1, freeSeen, one));
            freeSeen = Or(freeSeen, empty[i]);
            string nonEmpty = Op("xor", 1, empty[i], one);
            string slotShape = And(Op("xor", 1, Eq(slots[i]["id"], z64), one),
                And(Less64(slots[i]["id"], nextId), And(Eq(Eq(Op("and", 32, slots[i]["size"], C(0x80000000, 32)), C(0x80000000, 32)), zero),
                Eq(Eq(slots[i]["size"], z32), zero))));
            slotShape = And(slotShape, Or(Op("xor", 1, published[i], one), Op("xor", 1, Less64(cycle, slots[i]["published_cycle"]), one)));
            valid = And(valid, Or(empty[i], slotShape));
            oldest[i] = queued[i];
            for (int j = 0; j < 8; j++)
            {
                if (i != j) oldest[i] = And(oldest[i], Or(Op("xor", 1, queued[j], one), Less64(slots[i]["id"], slots[j]["id"])));
                if (j < i) valid = And(valid, Or(Or(empty[i], empty[j]), Op("xor", 1, Eq(slots[i]["id"], slots[j]["id"]), one)));
            }
            _ = nonEmpty;
        }
        string hasQueued = zero;
        string selectedId = z64;
        for (int i = 0; i < 8; i++) { hasQueued = Or(hasQueued, queued[i]); selectedId = Mux(oldest[i], slots[i]["id"], selectedId, 64); }

        string acceptRequested = And(valid, isAccept);
        string accepted = And(acceptRequested, And(sizePositive, freeSeen));
        string backpressured = And(acceptRequested, And(sizePositive, Op("xor", 1, freeSeen, one)));
        string rejectedSize = And(acceptRequested, Op("xor", 1, sizePositive, one));
        string advance = And(valid, isAdvance);
        string service = And(advance, hasQueued);
        string take = zero, cancel = zero, takenSuccess = zero, takenCycle = z64;
        string nextCycle = Op("add", 64, cycle, o64);
        Output("valid", valid, 1);
        Output("next_event_count", Mux(valid, Op("add", 32, eventCount, o32), eventCount, 32), 32);
        Output("next_cycle", Mux(advance, nextCycle, cycle, 64), 64);
        Output("next_request_id", Mux(accepted, Op("add", 64, nextId, o64), nextId, 64), 64);
        Output("accepted", accepted, 1);
        Output("accepted_id", Mux(accepted, nextId, z64, 64), 64);
        Output("rejected_nonpositive_size", rejectedSize, 1);
        Output("backpressured_at_capacity", backpressured, 1);
        Output("service_this_edge", service, 1);
        Output("service_request_id", Mux(service, selectedId, z64, 64), 64);

        for (int i = 0; i < 8; i++)
        {
            string idMatches = Eq(requestId, slots[i]["id"]);
            string takeSlot = And(valid, And(isTake, And(published[i], idMatches)));
            string cancelSlot = And(valid, And(isCancel, And(Op("xor", 1, empty[i], one), idMatches)));
            string remove = Or(takeSlot, cancelSlot);
            take = Or(take, takeSlot);
            cancel = Or(cancel, cancelSlot);
            takenSuccess = Mux(takeSlot, slots[i]["succeeded"], takenSuccess, 1);
            takenCycle = Mux(takeSlot, slots[i]["published_cycle"], takenCycle, 64);
            string promote = And(advance, pending[i]);
            string serve = And(service, oldest[i]);
            string insert = And(accepted, freeFirst[i]);
            foreach ((string name, int width) in stateFields)
            {
                string old = slots[i][name];
                string value;
                if (name == "phase")
                    value = Mux(remove, C(0, 2), Mux(insert, C(1, 2), Mux(serve, C(2, 2), Mux(promote, C(3, 2), old, 2), 2), 2), 2);
                else if (name == "published_cycle") value = Mux(promote, nextCycle, old, 64);
                else if (name == "succeeded") value = Mux(serve, serviceSucceeded, old, 1);
                else
                {
                    string input = name switch { "id" => nextId, "device" => device, "address" => address, _ => size };
                    value = Mux(insert, input, old, width);
                }
                Output("next_s" + i + "_" + name, value, width);
            }
        }
        Output("taken", take, 1);
        Output("taken_succeeded", takenSuccess, 1);
        Output("taken_published_cycle", takenCycle, 64);
        Output("canceled", cancel, 1);

        const string moduleName = "vector_segment_read_lifecycle";
        var module = new HwModule(moduleName, moduleName, ports, nodes, [],
            new HwHardwareContract("memory.vector-segment-read.logical-lifecycle.unqualified.v1", HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var result = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", moduleName, [module], toolchain, []));
        return result.Validated ?? throw new InvalidDataException("Vector segment lifecycle HWIR refused: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    public static HwValidatedDesign Compile(byte[] activeBytes, byte[] goldenBytes,
        string baseCommit, HwToolchainIdentity toolchain, out IReadOnlyList<MethodEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(activeBytes);
        ArgumentNullException.ThrowIfNull(goldenBytes);
        if (Hash(activeBytes) != ActiveSha256 || Hash(goldenBytes) != GoldenSha256 || goldenBytes.Length != GoldenBlobSize)
            throw new InvalidDataException("Vector-segment active/Golden source bytes changed.");
        if (baseCommit != ActiveModelCommit ||
            toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("Pinned active model commit and combinational profile required.");

        SyntaxNode Parse(byte[] bytes)
        {
            var tree = CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Invalid MemoryCycleController syntax.");
            return tree.GetRoot();
        }
        static ClassDeclarationSyntax Owner(SyntaxNode root) => root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "MemoryCycleController");
        var activeOwner = Owner(Parse(activeBytes));
        var goldenOwner = Owner(Parse(goldenBytes));
        static MethodDeclarationSyntax Method(ClassDeclarationSyntax owner, string name) => owner.Members
            .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);
        static string TokenText(MethodDeclarationSyntax method) => string.Concat(method.DescendantTokens().Select(t => t.Text));
        static string MethodHash(MethodDeclarationSyntax method) => Hash(Encoding.UTF8.GetBytes(TokenText(method)));

        static bool Contract(ClassDeclarationSyntax owner, string name, string[] mustContain, string[] mustNotContain)
        {
            MethodDeclarationSyntax method = Method(owner, name);
            string text = TokenText(method);
            return mustContain.All(text.Contains) && mustNotContain.All(x => !text.Contains(x, StringComparison.Ordinal));
        }
        static bool Capacity(ClassDeclarationSyntax owner) => owner.Members.OfType<FieldDeclarationSyntax>()
            .Any(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "VectorSegmentLoadCapacity" &&
                v.Initializer?.Value.ToString() == "8"));

        var items = new List<MethodEvidence>();
        void Check(string name, string[] required, string[] forbidden)
        {
            MethodDeclarationSyntax a = Method(activeOwner, name), g = Method(goldenOwner, name);
            items.Add(new(name, MethodHash(a), MethodHash(g), Contract(activeOwner, name, required, forbidden), Contract(goldenOwner, name, required, forbidden)));
        }
        Check("TryAcceptVectorSegmentLoad", ["TryAcceptRead(ReadRequestClass.VectorSegmentMicroOp,deviceId,address,size)"], []);
        Check("TryAcceptRead", ["requestClass==ReadRequestClass.VectorSegmentMicroOp&&size<=0", "outstandingForClass>=capacity", "ReadRequestClass.VectorSegmentMicroOp=>VectorSegmentLoadCapacity", "_outstanding.Add(", "_readQueue.Enqueue(requestId)"], []);
        Check("TickOneCycle", ["_nextCompletions", "_publishedCompletions.Add(", "_nextCompletions.Clear()", "_readQueue.Dequeue()", "_nextCompletions.Add(", "break;"], []);
        Check("TryTakeCompletion", ["_publishedCompletions.Remove(requestId,outcompletion)", "_outstanding.Remove(requestId,outControllerRequestrequest)", "DecrementOutstandingClass(request)"], []);
        Check("TryCancel", ["_outstanding.Remove(requestId,outControllerRequestrequest)", "DecrementOutstandingClass(request)", "_nextCompletions.Remove(requestId)", "_publishedCompletions.Remove(requestId)"], []);
        if (items.Any(x => !x.ActiveContractMatches || !x.GoldenContractMatches) || !Capacity(activeOwner) || !Capacity(goldenOwner))
            throw new InvalidDataException("Active or Golden vector-segment lifecycle source guard failed.");
        static bool Ordered(ClassDeclarationSyntax cls, string name, params string[] tokens)
        {
            string body = TokenText(Method(cls, name));
            int prior = -1;
            foreach (string token in tokens)
            {
                int current = body.IndexOf(token, prior + 1, StringComparison.Ordinal);
                if (current < 0 || current <= prior) return false;
                prior = current;
            }
            return true;
        }
        foreach (ClassDeclarationSyntax cls in new[] { activeOwner, goldenOwner })
        {
            if (!Ordered(cls, "TryAcceptRead", "size<=0", "lock(_gate)", "_outstanding.Add(", "_readQueue.Enqueue(requestId)"))
                throw new InvalidDataException("Vector-segment admission order changed.");
            if (!Ordered(cls, "TickOneCycle", "_publishedCompletions.Add(", "_nextCompletions.Clear()", "_readQueue.Dequeue()", "_nextCompletions.Add(", "break;"))
                throw new InvalidDataException("Controller-edge publication/service order changed.");
            if (!Ordered(cls, "TryTakeCompletion", "_publishedCompletions.Remove(requestId", "_outstanding.Remove(requestId", "DecrementOutstandingClass(request)"))
                throw new InvalidDataException("Completion take/release order changed.");
            if (!Ordered(cls, "TryCancel", "_outstanding.Remove(requestId", "DecrementOutstandingClass(request)", "_nextCompletions.Remove(requestId)", "_publishedCompletions.Remove(requestId)"))
                throw new InvalidDataException("Cancellation cleanup order changed.");
        }
        evidence = items;

        MethodDeclarationSyntax ingress = Method(activeOwner, "TryAcceptVectorSegmentLoad");
        var pos = ingress.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        string identity = Hash(Encoding.UTF8.GetBytes("vector-segment-read-admission/v1|" + ActiveSha256 + "|" + GoldenSha256));
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", baseCommit, RelativePath,
            "CircuitWeave.VectorSegmentRead.Admission", "HybridCPU_ISE.CloseToHSL.Memory.Timing.MemoryCycleController",
            "TryAcceptVectorSegmentLoad", identity, HwSourceRole.Primary, pos.Line + 1, pos.Character + 1);
        var ports = new List<HwPortContract>();
        var nodes = new List<HwNode>();
        int serial = 0;
        HwBitVectorType T(int width) => new(width);
        string Input(string name, int width) { ports.Add(new(name, HwPortDirection.Input, T(width))); return name; }
        string Constant(uint value, int width)
        {
            string id = "k" + serial++;
            nodes.Add(new HwConstantNode(id, T(width), Convert.ToString(value, 2).PadLeft(width, '0')[^width..], origin));
            return id;
        }
        string Op(string opcode, int width, params string[] args)
        {
            string id = "n" + serial++;
            nodes.Add(new HwOperationNode(id, opcode, args, T(width), Origin: origin));
            return id;
        }
        string Mux(string select, string yes, string no, int width) => Op("mux", width, select, yes, no);
        void Output(string name, string value, int width)
        { ports.Add(new(name, HwPortDirection.Output, T(width))); nodes.Add(new HwOutputDriveNode("drive_" + name, name, value, origin)); }

        string request = Input("request", 1);
        string size = Input("size_signed", 32);
        string outstanding = Input("vector_segment_outstanding", 32);
        string sign = Op("eq", 1, Op("and", 32, size, Constant(0x80000000, 32)), Constant(0x80000000, 32));
        string nonzero = Op("eq", 1, size, Constant(0, 32));
        string positive = Op("and", 1, Op("eq", 1, sign, Constant(0, 1)), Op("eq", 1, nonzero, Constant(0, 1)));
        string underCapacity = Op("ult64", 1, Op("zext", 64, outstanding), Constant(8, 64));
        string accepted = Op("and", 1, request, Op("and", 1, positive, underCapacity));
        string rejected = Op("and", 1, request, Op("eq", 1, positive, Constant(0, 1)));
        string full = Op("eq", 1, underCapacity, Constant(0, 1));
        string backpressure = Op("and", 1, request, Op("and", 1, positive, full));
        string status = Mux(request, Mux(positive, Mux(underCapacity, Constant(1, 2), Constant(2, 2), 2), Constant(3, 2), 2), Constant(0, 2), 2);
        Output("accepted", accepted, 1);
        Output("rejected_nonpositive_size", rejected, 1);
        Output("backpressured_at_capacity", backpressure, 1);
        Output("admission_status", status, 2); // 0=no request, 1=accepted, 2=backpressured, 3=rejected size
        Output("capacity_remaining", Op("sub", 32, Constant(8, 32), outstanding), 32);

        const string moduleName = "vector_segment_read_admission";
        var module = new HwModule(moduleName, moduleName, ports, nodes, [],
            new HwHardwareContract("memory.vector-segment-read.admission-source-projection.v1", HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var validated = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", moduleName, [module], toolchain, []));
        return validated.Validated ?? throw new InvalidDataException("Vector segment admission HWIR refused: " +
            string.Join("; ", validated.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
