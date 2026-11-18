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
/// Bounded event-order candidate for ExplicitPacketScalar and SingleLaneScalarLoad sharing one FIFO.
/// One serialized call per step; Advance is one compatibility iteration, not a physical clock.
/// Sixteen fixed slots retain captured bank index/generation and request class; each class is limited to eight outstanding. No RAM, reset, stall, DMA,
/// translation, retire, telemetry, exception, or physical read-timing equivalence is claimed.
/// Start with event_count=cycle=0, next_id=1 and all slot phases=0; only chain output state into the next step. Identity uniqueness, occupied payload shape, and per-class occupancy follow from this closed transition chain.
/// Phase 0/1/2/3 means Empty/Queued/Next/Published; event 0/1/2/3 is Accept/Advance/Take/Cancel.
/// At most 4096 events and monotonically allocated IDs exclude wrapping/reuse. Empty-slot
/// payload is unspecified. External captured bindings/results are observations, not authority.
/// </summary>
public static class BridgeHwirSharedScalarReadLogicalEvent
{
    public const string ControllerSha256 = "6d7a0d7b72f2594701dd49bedb6a5a6d0eb500ea19091d1024c1d98cb556d551";
    public const string BindingSha256 = "5d8135c290565068cfabe9ecaecfa31d73a4a66aad134ab64877fb31be7101f7";
    public const string Scope = "Bounded serialized shared-FIFO logical-event candidate, eight outstanding requests per scalar class, horizon 4096; no physical RAM/read timing/reset/stall equivalence; external backend and captured geometry binding remain unqualified.";

    public static HwValidatedDesign Compile(byte[] controllerSource, byte[] bindingSource,
        string baseCommit, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(controllerSource); ArgumentNullException.ThrowIfNull(bindingSource);
        if (Hash(controllerSource) != ControllerSha256 || Hash(bindingSource) != BindingSha256)
            throw new InvalidDataException("Shared scalar logical-event source bytes changed.");
        if (baseCommit.Length != 40 || baseCommit.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1") throw new InvalidDataException("Pinned base context and combinational profile required.");
        var root = Parse(controllerSource); var bindingRoot = Parse(bindingSource);
        var owner = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "MemoryCycleController");
        MethodDeclarationSyntax Method(string name) => owner.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);
        void Contains(string method, params string[] fragments)
        {
            string text = string.Concat(Method(method).DescendantTokens().Select(t => t.Text));
            foreach (string fragment in fragments) if (!text.Contains(fragment, StringComparison.Ordinal)) throw new InvalidDataException("Source guard changed: " + method);
        }
        if (!owner.Members.OfType<FieldDeclarationSyntax>().Any(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "ExplicitPacketScalarLoadCapacity" && v.Initializer?.Value.ToString() == "8")) ||
            !owner.Members.OfType<FieldDeclarationSyntax>().Any(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "SingleLaneScalarLoadCapacity" && v.Initializer?.Value.ToString() == "8")) ||
            !owner.Members.OfType<FieldDeclarationSyntax>().Any(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "_nextRequestId" && v.Initializer?.Value.ToString() == "1"))) throw new InvalidDataException("Capacity/initial request identity changed.");
        Contains("TryAcceptExplicitPacketScalarLoad", "TryAcceptRead(ReadRequestClass.ExplicitPacketScalar,deviceId,address,size)");
        Contains("TryAcceptSingleLaneScalarLoad", "TryAcceptRead(ReadRequestClass.SingleLaneScalarMicroOp,deviceId,address,size)");
        Contains("TryAcceptRead", "sizeisnot(1or2or4or8)", "outstandingForClass>=capacity", "_outstanding.Add(", "_readQueue.Enqueue(requestId)");
        var acceptText = Method("TryAcceptRead").ToString();
        if (acceptText.IndexOf("_outstanding.Add", StringComparison.Ordinal) >= acceptText.IndexOf("_readQueue.Enqueue", StringComparison.Ordinal)) throw new InvalidDataException("Admission queue order changed.");
        Contains("TickOneCycle", "_memoryCycle=checked(_memoryCycle+1)", "_nextCompletions.Clear()", "_readQueue.Dequeue()", "_nextCompletions.Add(");
        var tick = Method("TickOneCycle").ToString();
        if (tick.IndexOf("_publishedCompletions.Add", StringComparison.Ordinal) >= tick.IndexOf("_readQueue.Dequeue", StringComparison.Ordinal)) throw new InvalidDataException("Publication/service order changed.");
        Contains("TryTakeCompletion", "_publishedCompletions.Remove(requestId,outcompletion)", "_outstanding.Remove(requestId,outControllerRequestrequest)");
        Contains("TryCancel", "_outstanding.Remove(requestId,outControllerRequestrequest)", "_nextCompletions.Remove(requestId)", "_publishedCompletions.Remove(requestId)");
        Contains("AdvanceCompatibilityCycles", "TickOneCycle()"); Contains("AdvancePlatformEdge", "TickOneCycle()");
        if (!bindingRoot.DescendantNodes().OfType<RecordDeclarationSyntax>().Any(s => s.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) && s.Identifier.ValueText == "PhysicalMemoryBankBinding")) throw new InvalidDataException("Captured physical binding type missing.");
        string digest = Hash(Encoding.UTF8.GetBytes("shared-scalar-logical-event/v1|" + ControllerSha256 + "|" + BindingSha256 + "|" + Scope));
        var position = Method("TryAcceptRead").Identifier.GetLocation().GetLineSpan().StartLinePosition;
        var origin = new HwSourceOrigin("urn:circuitweave:dirty-source-capture:sha256:" + ControllerSha256, GitBlobId(controllerSource),
            "HybridCPU_ISE/CloseToHSL/Memory/Timing/MemoryCycleController.cs", "CircuitWeave.SharedScalarRead.LogicalEvent",
            "HybridCPU_ISE.CloseToHSL.Memory.Timing.MemoryCycleController", "TryAcceptRead", digest, HwSourceRole.Primary, position.Line + 1, position.Character + 1);
        _ = bindingRoot.DescendantNodes().OfType<RecordDeclarationSyntax>().Single(s => s.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) && s.Identifier.ValueText == "PhysicalMemoryBankBinding");
        var ports = new List<HwPortContract>(); var nodes = new List<HwNode>(); int serial = 0;
        var constants = new Dictionary<(ulong,int),string>();
        var expressions = new Dictionary<string,string>(StringComparer.Ordinal);
        HwBitVectorType T(int width) => new(width);
        string Input(string name, int width) { ports.Add(new(name, HwPortDirection.Input, T(width))); return name; }
        string C(ulong value, int width) { if(constants.TryGetValue((value,width),out var prior)) return prior; string id = "k" + serial++; nodes.Add(new HwConstantNode(id, T(width), Convert.ToString(unchecked((long)value), 2).PadLeft(width, '0')[^width..], origin)); constants.Add((value,width),id); return id; }
        var zero = C(0, 1); var one = C(1, 1); var z64 = C(0, 64); var o64 = C(1, 64); var z32 = C(0, 32); var o32 = C(1, 32);
        string Op(string opcode, int width, params string[] args) { string key = opcode + "|" + width + "|" + string.Join("|", args); if(expressions.TryGetValue(key,out var prior)) return prior; string id = "n" + serial++; nodes.Add(new HwOperationNode(id, opcode, args, T(width), Origin: origin)); expressions.Add(key,id); return id; }
        string Eq(string a, string b) => Op("eq", 1, a, b);
        string And(string a, string b) => a == b ? a : a == zero || b == zero ? zero : a == one ? b : b == one ? a : Op("and", 1, a, b);
        string Or(string a, string b) => a == b ? a : a == one || b == one ? one : a == zero ? b : b == zero ? a : Op("or", 1, a, b);
        string Not(string a) => a == zero ? one : a == one ? zero : Op("xor", 1, a, C(1,1));
        string Mux(string c, string yes, string no, int width) { if(yes==no)return yes;if(c==one)return yes;if(c==zero)return no;string key = "mux|" + width + "|" + c + "|" + yes + "|" + no; if(expressions.TryGetValue(key,out var prior)) return prior; string id = "n" + serial++; nodes.Add(new HwMuxNode(id, c, yes, no, T(width), origin)); expressions.Add(key,id); return id; }
        void Output(string name, string value, int width) { ports.Add(new(name, HwPortDirection.Output, T(width))); nodes.Add(new HwOutputDriveNode("drive_" + name, name, value, origin)); }
        var phase = Enumerable.Range(0, 4).Select(i => C((ulong)i, 2)).ToArray();
        var eventCount = Input("event_count", 32); var cycle = Input("cycle", 64); var nextId = Input("next_id", 64); var ev = Input("event_kind", 3);
        var eventFlags = Enumerable.Range(0, 4).Select(i => Eq(ev, C((ulong)i, 3))).ToArray();
        var reqId = Input("request_id", 64); var device = Input("device", 64); var address = Input("address", 64); var size = Input("size", 32);
        var bank = Input("bank_index", 32); var generation = Input("bank_generation", 64); var captureOk = Input("capture_ok", 1);
        var backendValid = Input("backend_valid", 1); var backendSucceeded = Input("backend_succeeded", 1); var backendData = Input("backend_data", 64);
        string[] matchFields = ["id", "device", "address", "size", "bank_index", "bank_generation"];
        var requestClass = Input("request_class", 1);
        var backend = matchFields.ToDictionary(f => f, f => Input("backend_" + f, f is "size" or "bank_index" ? 32 : 64));
        (string name, int width)[] fields = [("phase",2),("class",1),("id",64),("device",64),("address",64),("size",32),("bank_index",32),("bank_generation",64),("data",64),("succeeded",1),("published_cycle",64)];
        var slots = Enumerable.Range(0, 16).Select(i => fields.ToDictionary(f => f.name, f => Input("s" + i + "_" + f.name, f.width))).ToArray();
        string SizeOk(string s) => Or(Or(Eq(s,C(1,32)), Eq(s,C(2,32))),Or(Eq(s,C(4,32)), Eq(s,C(8,32))));
        string BankOk(string b) => Op("ult64", 1, Op("zext",64,b), C(1UL << 31,64));
        var empty = slots.Select(s => Eq(s["phase"],phase[0])).ToArray(); var queued = slots.Select(s => Eq(s["phase"],phase[1])).ToArray();
        var next = slots.Select(s => Eq(s["phase"],phase[2])).ToArray(); var published = slots.Select(s => Eq(s["phase"],phase[3])).ToArray();
        string valid = And(Op("ult64",1,Op("zext",64,eventCount),C(4096,64)), Not(Eq(nextId,z64)));
        valid = And(valid, Not(Op("ult64",1,C(4097,64),nextId)));
        valid = And(valid, Not(Op("ult64",1,Op("zext",64,eventCount),cycle)));
        valid = And(valid, Not(Op("ult64",1,Op("add",64,Op("zext",64,eventCount),o64),nextId)));
        valid = And(valid, Or(Or(eventFlags[0],eventFlags[1]),Or(eventFlags[2],eventFlags[3])));
        string freeSeen = zero; var allocate = new string[16]; var oldest = new string[16];
        string classOutstanding = C(0,5); string explicitOutstanding = C(0,5); string singleLaneOutstanding = C(0,5);
        for (int i=0;i<16;i++)
        {
            allocate[i] = And(empty[i],Not(freeSeen)); freeSeen = Or(freeSeen,empty[i]);
            string occupied = Not(empty[i]);
            classOutstanding = Op("add", 5, classOutstanding, Op("zext", 5, And(occupied, Eq(slots[i]["class"], requestClass))));
            explicitOutstanding = Op("add", 5, explicitOutstanding, Op("zext", 5, And(occupied, Eq(slots[i]["class"], zero))));
            singleLaneOutstanding = Op("add", 5, singleLaneOutstanding, Op("zext", 5, And(occupied, Eq(slots[i]["class"], one))));
        }
        string hasQueued=zero; var selected = matchFields.ToDictionary(f=>f,f=>f is "size" or "bank_index" ? z32 : z64);
        for(int i=0;i<16;i++)
        {
            string choose = And(queued[i], Or(Not(hasQueued), Op("ult64",1,slots[i]["id"],selected["id"])));
            foreach(string f in matchFields) selected[f]=Mux(choose,slots[i][f],selected[f],f is "size" or "bank_index" ?32:64);
            hasQueued=Or(hasQueued,queued[i]);
        }
        for(int i=0;i<16;i++) oldest[i]=And(queued[i],Eq(slots[i]["id"],selected["id"]));
        string resultMatches=backendValid; foreach(string f in matchFields) resultMatches=And(resultMatches,Eq(selected[f],backend[f]));
        valid=And(valid,Or(Not(And(eventFlags[1],hasQueued)),resultMatches));
        valid = And(valid, And(Op("ult64", 1, Op("zext", 64, explicitOutstanding), C(9,64)), Op("ult64", 1, Op("zext", 64, singleLaneOutstanding), C(9,64))));
        string classHasCapacity = Op("ult64", 1, Op("zext", 64, classOutstanding), C(8,64));
        valid=And(valid,Or(Not(And(eventFlags[0],And(SizeOk(size),classHasCapacity))),And(captureOk,And(BankOk(bank),Not(Eq(generation,z64))))));
        string accept=And(valid,And(eventFlags[0],And(SizeOk(size),And(freeSeen,classHasCapacity))));
        string advance=And(valid,eventFlags[1]); string service=And(advance,hasQueued); string take=zero; string cancel=zero;
        var nextCycle=Op("add",64,cycle,o64);
        Output("valid",valid,1); Output("next_event_count",Mux(valid,Op("add",32,eventCount,o32),eventCount,32),32);
        Output("next_cycle",Mux(advance,nextCycle,cycle,64),64); Output("next_request_id",Mux(accept,Op("add",64,nextId,o64),nextId,64),64);
        // admission_status 0=None, 1=Accepted, 2=Backpressured, 3=RejectedSize.
        var status=Mux(SizeOk(size),Mux(And(freeSeen,classHasCapacity),C(1,2),C(2,2),2),C(3,2),2);
        Output("admission_status",Mux(And(valid,eventFlags[0]),status,phase[0],2),2); Output("accepted_id",Mux(accept,nextId,z64,64),64);
        Output("serviced",service,1); foreach(string f in matchFields) Output("service_"+f,Mux(service,selected[f],f is "size" or "bank_index"?z32:z64,f is "size" or "bank_index"?32:64),f is "size" or "bank_index"?32:64);
        string takenData=z64, takenSuccess=zero, takenCycle=z64; string publishedCount=C(0,4);
        for(int i=0;i<16;i++)
        {
            string idMatch=Eq(reqId,slots[i]["id"]); string takeSlot=And(valid,And(eventFlags[2],And(published[i],idMatch)));
            string cancelSlot=And(valid,And(eventFlags[3],And(Not(empty[i]),idMatch))); string remove=Or(takeSlot,cancelSlot);
            take=Or(take,takeSlot);cancel=Or(cancel,cancelSlot);takenData=Mux(takeSlot,slots[i]["data"],takenData,64);takenSuccess=Mux(takeSlot,slots[i]["succeeded"],takenSuccess,1);takenCycle=Mux(takeSlot,slots[i]["published_cycle"],takenCycle,64);
            string publish=And(advance,next[i]); string serve=And(service,oldest[i]); string insert=And(accept,allocate[i]);
            publishedCount=Op("add",4,publishedCount,Op("zext",4,publish));
            foreach(var f in fields)
            {
                string old=slots[i][f.name]; string value=old;
                if(f.name=="phase") value=Mux(remove,phase[0],Mux(insert,phase[1],Mux(serve,phase[2],Mux(publish,phase[3],old,2),2),2),2);
                else if(f.name=="published_cycle") value=Mux(publish,nextCycle,old,64);
                else if(f.name=="data")
                {
                    string mask=Mux(Eq(slots[i]["size"],C(1,32)),C(255,64),Mux(Eq(slots[i]["size"],C(2,32)),C(65535,64),Mux(Eq(slots[i]["size"],C(4,32)),C(uint.MaxValue,64),C(ulong.MaxValue,64),64),64),64);
                    value=Mux(serve,Op("and",64,backendData,mask),old,64);
                }
                else if(f.name=="succeeded") value=Mux(serve,backendSucceeded,old,1);
                else if (f.name == "class") value=Mux(insert,requestClass,old,1);
                else { string input=f.name switch { "id"=>nextId,"device"=>device,"address"=>address,"size"=>size,"bank_index"=>bank,_=>generation }; value=Mux(insert,input,old,f.width); }
                Output("next_s"+i+"_"+f.name,value,f.width);
            }
        }
        Output("taken",take,1);Output("canceled",cancel,1);Output("taken_data",takenData,64);Output("taken_succeeded",takenSuccess,1);Output("taken_published_cycle",takenCycle,64);Output("published_count",publishedCount,4);
        // Both pinned source byte hashes participate in the identity digest. The HWIR
        // contract permits node origins only when they equal their module's primary origin.
        const string name="shared_scalar_read_logical_event";
        var module=new HwModule(name,name,ports,nodes,[],new HwHardwareContract("memory.shared-scalar-read.logical-event.unqualified.v1",HwModuleKind.Combinational,ports,new HwTimingContract(0,1)),origin);
        var result=new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",name,[module],toolchain,[]));
        return result.Validated ?? throw new InvalidDataException($"Shared scalar logical-event HWIR refused ({nodes.Count} nodes/{ports.Count} ports): "+string.Join("; ",result.Diagnostics.Select(d=>d.Code+" "+d.Message)));
    }

    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string GitBlobId(byte[] bytes)
    {
        byte[] header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        byte[] body = new byte[header.Length + bytes.Length];
        Buffer.BlockCopy(header, 0, body, 0, header.Length);
        Buffer.BlockCopy(bytes, 0, body, header.Length, bytes.Length);
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(body)).ToLowerInvariant();
    }
    private static SyntaxNode Parse(byte[] bytes)
    {
        var tree=CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
        if(tree.GetDiagnostics().Any(d=>d.Severity==DiagnosticSeverity.Error)) throw new InvalidDataException("Invalid source syntax.");
        return tree.GetRoot();
    }
}
#endif
