using System.Text.Json;
using System.Text.Json.Nodes;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU.Compiler.Core.IR.Telemetry.Contracts;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Native;
using System.Reflection;

internal static class ProfileIngressChecks
{
    public static void Run()
    {
        TypedSlotTelemetryProfile profile = new TypedSlotTelemetryProfile("program", new Dictionary<IrSlotClass, long>(),
            new Dictionary<IrSlotClass, long>(), new Dictionary<IrTypedSlotRejectReason, long>(),
            0.2, 0.8, 10, 16, 4, 1, 0.8, 0, new Dictionary<int, long>(), null);
        string json = JsonSerializer.Serialize(new { Schema = TelemetryProfileReader.ProfileSchemaV1, Profile = profile });
        TelemetryProfileReader reader = TelemetryProfileReader.CreateVersionedJson(json, "program", out string? digest);
        if (!reader.HasProfile || digest?.Length != 64) throw new Exception("Valid profile rejected.");
        JsonObject node = JsonNode.Parse(json)!.AsObject();
        JsonObject reordered = new JsonObject(node.Reverse().Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        TelemetryProfileReader.CreateVersionedJson(reordered.ToJsonString(), "program", out string? other);
        if (digest != other) throw new Exception("Property order changed digest.");
        JsonObject mapped = JsonNode.Parse(json)!.AsObject();
        mapped["Profile"]!["PerVtInjectionCounts"] = JsonNode.Parse("{\"1\":2,\"2\":3}");
        TelemetryProfileReader mappedReader = TelemetryProfileReader.CreateVersionedJson(mapped.ToJsonString(), "program", out string? mappedDigest);
        mapped["Profile"]!["PerVtInjectionCounts"] = JsonNode.Parse("{\"2\":3,\"1\":2}");
        TelemetryProfileReader.CreateVersionedJson(mapped.ToJsonString(), "program", out string? reorderedDigest);
        if (!mappedReader.HasProfile || mappedDigest == digest || mappedDigest != reorderedDigest)
            throw new Exception("Dictionary order/content identity drift.");
        int negatives = 0;
        void Reject(string input, string hash = "program")
        {
            if (TelemetryProfileReader.CreateVersionedJson(input, hash, out string? invalid).HasProfile || invalid is not null)
                throw new Exception("Invalid profile admitted.");
            negatives++;
        }
        Reject(json, "stale");
        Reject("{");
        Reject(json.Replace(TelemetryProfileReader.ProfileSchemaV1, "future/v2"));
        Reject(json.Replace("\"TotalBundlesExecuted\":10", "\"TotalBundlesExecuted\":-1"));
        Reject(json.Replace("\"AverageNopDensity\":0.2", "\"AverageNopDensity\":1.2"));
        Reject(json.Replace("\"TotalInjectionsPerClass\":{}", "\"TotalInjectionsPerClass\":null"));
        Reject(json.Replace("\"TotalBundlesExecuted\":10", "\"TotalBundlesExecuted\":10,\"TotalBundlesExecuted\":11"));
        Reject(json.Replace("\"TotalInjectionsPerClass\":{}", "\"TotalInjectionsPerClass\":{\"999\":1}"));
        Reject(json.Replace("\"ProgramHash\":\"program\"", "\"ProgramHash\":\"program\",\"Unknown\":1"));
        Reject(new string(' ', TelemetryProfileReader.MaximumProfileJsonCharacters + 1));
        Console.WriteLine($"PASS profile ingress: 3 positive/identity + {negatives} negative checks; Qualification: no");
        CheckCompilationLane();
        CheckPressureTieBreak();
        TypedSlotTelemetryProfile large = profile with
        {
            TotalInjectionsPerClass = new Dictionary<IrSlotClass, long> { [IrSlotClass.AluClass] = long.MaxValue },
            TotalRejectsPerClass = new Dictionary<IrSlotClass, long> { [IrSlotClass.AluClass] = long.MaxValue },
            PerVtInjectionCounts = new Dictionary<int, long> { [1] = long.MaxValue },
            PerVtRejectionCounts = new Dictionary<int, long> { [1] = long.MaxValue }
        };
        TelemetryProfileReader largeReader = TelemetryProfileReader.Create(large);
        if (largeReader.GetCertificatePressureByClass(IrSlotClass.AluClass) != 0.5 ||
            largeReader.GetPerVtInjectabilityRate(1) != 0.5 || largeReader.GetPerVtInjectabilityPressure(1) != 0.5)
            throw new Exception("Certificate/VT ratio counter overflow.");
        Console.WriteLine("PASS certificate/VT large-counter ratios: 3 checks");
    }

    private static void CheckPressureTieBreak()
    {
        // Exercise the actual private priority comparer, not a mirrored implementation.
        Type schedulerType = typeof(HybridCpuLocalListScheduler);
        Type priorityType = schedulerType.GetNestedType("ReadyNodePriority", System.Reflection.BindingFlags.NonPublic)!;
        object Priority(int index, int pressure, int latency = 1)
        {
            ConstructorInfo constructor = priorityType.GetConstructors().Single();
            ParameterInfo[] parameters = constructor.GetParameters();
            object[] values = parameters.Select(p => (object)(p.Name switch
            {
                "InstructionIndex" => index, "ClassPressurePenalty" => pressure,
                "MinimumLatencyCycles" => latency, _ => 0
            })).ToArray();
            return constructor.Invoke(values);
        }
        MethodInfo compare = schedulerType.GetMethod("CompareCycleCandidatePriorities", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        int Compare(HybridCpuLocalListScheduler scheduler, object a, object b) => (int)compare.Invoke(scheduler, [a, b])!;
        HybridCpuLocalListScheduler smooth = new HybridCpuLocalListScheduler { UseClassPressureSmoothingTieBreaks = true };
        if (Compare(smooth, Priority(1, 2), Priority(2, 0)) <= 0)
            throw new Exception("Pressure tie-break shadowed by instruction index.");
        if (Compare(smooth, Priority(1, 0), Priority(2, 0)) >= 0 ||
            Compare(smooth, Priority(1, 2, 2), Priority(2, 0, 1)) >= 0 ||
            Compare(new(), Priority(1, 0), Priority(2, 0)) >= 0)
            throw new Exception("Latency/default/index ordering drift.");
        Console.WriteLine("PASS pressure comparator: pressure precedes final index; latency/default/index controls");
    }

    private static void CheckCompilationLane()
    {
        HybridCpuInstructionWord Carrier(string source)
        {
            NativeCompilationResult result = new NativeAssemblyFrontend().Compile(new NativeFrontendRequest(0, source));
            if (!result.Succeeded) throw new Exception("Carrier ingress rejected.");
            uint opcode = (uint)result.Artifacts!.CanonicalIr.Instructions.Single().Opcode;
            return Enumerable.Range(0, 8).Select(result.Artifacts.CompiledProgram.LoweredBundles[0].GetInstruction)
                .First(w => w.OpCode == opcode);
        }
        HybridCpuInstructionWord[] words = Enumerable.Range(0, 16).Select(n => Carrier(
            $"{(n % 2 == 0 ? "ADD" : "MUL")} r{10 + n % 8}, r{18 + n % 8}, r{18 + (n + 1) % 8}")).ToArray();
        HybridCpuCompiledProgram baseline = HybridCpuCanonicalCompiler.CompileProgram(0, words);
        HybridCpuProfileCompilationResultV1 off = HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words, new());
        void SameDefault(HybridCpuProfileCompilationResultV1 value)
        {
            if (!baseline.ProgramImage.SequenceEqual(value.CompiledProgram.ProgramImage) ||
                value.EffectiveOptionsDigest != off.EffectiveOptionsDigest || value.ActiveProfileDigest is not null)
                throw new Exception("Disabled profile changed default output/identity.");
        }
        SameDefault(off);
        SameDefault(HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words,
            new() { SelectionPolicy = HybridCpuProfileSelectionPolicyV1.ClassPressurePriority }));
        SameDefault(HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words,
            new(true, "{") { SelectionPolicy = HybridCpuProfileSelectionPolicyV1.ClassPressurePriority }));
        bool unknownPolicyDenied = false;
        try { HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words, new() { SelectionPolicy = (HybridCpuProfileSelectionPolicyV1)255 }); }
        catch (ArgumentOutOfRangeException) { unknownPolicyDenied = true; }
        if (!unknownPolicyDenied) throw new Exception("Unknown profile policy accepted.");
        SameDefault(HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words, new(false, "{")));
        SameDefault(HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words, new(true, "{")));
        TypedSlotTelemetryProfile profile = new TypedSlotTelemetryProfile(off.ProfileInputIdentity,
            new Dictionary<IrSlotClass, long> { [IrSlotClass.AluClass] = long.MaxValue },
            new Dictionary<IrSlotClass, long> { [IrSlotClass.AluClass] = long.MaxValue },
            new Dictionary<IrTypedSlotRejectReason, long>(), 0, 1, 1, 0, 0, 0, 0, 0,
            new Dictionary<int, long>(), null);
        if (TelemetryProfileReader.Create(profile).GetClassPressure(IrSlotClass.AluClass) != 0.5)
            throw new Exception("Counter sum overflow.");
        string json = JsonSerializer.Serialize(new { Schema = TelemetryProfileReader.ProfileSchemaV1, Profile = profile });
        HybridCpuProfileCompilationResultV1 active = HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words, new(true, json));
        HybridCpuProfileCompilationResultV1 priority = HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words,
            new(true, json) { SelectionPolicy = HybridCpuProfileSelectionPolicyV1.ClassPressurePriority });
        if (priority.ProfileStatus != "profile-active" || priority.EffectiveOptionsDigest == active.EffectiveOptionsDigest ||
            priority.ActiveProfileDigest != active.ActiveProfileDigest)
            throw new Exception("Active profile policy identity drift.");
        Console.WriteLine("PASS explicit profile policies: off/invalid equivalence, unknown rejection, active mode identity (4 checks)");
        HybridCpuProfileCompilationResultV1 repeat = HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words, new(true, json));
        HybridCpuProfileCompilationResultV1 traced = HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words,
            new(true, json) { CaptureDecisionTrace = true });
        HybridCpuProfileCompilationResultV1 tracedRepeat = HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words,
            new(true, json) { CaptureDecisionTrace = true });
        if (traced.DecisionTrace is not { Truncated: false } trace ||
            !traced.CompiledProgram.ProgramImage.SequenceEqual(active.CompiledProgram.ProgramImage) ||
            traced.EffectiveOptionsDigest != active.EffectiveOptionsDigest ||
            JsonSerializer.Serialize(trace) != JsonSerializer.Serialize(tracedRepeat.DecisionTrace) ||
            !trace.Events.Any(e => e.ClassPressureCostBucket > 0) ||
            trace.Events.Any(e => e.ClassPressureCostBucket is < 0 or > 2 || !e.StructurallyAdmissible && e.ClassPressureCostBucket is not null))
            throw new Exception("Trace changed selection or pressure bucket contract.");
        foreach (HybridCpuProfileCandidateEventV1? selected in trace.Events.Where(e => e.Kind == "selection"))
            if (!trace.Events.Any(e => e.Decision == selected.Decision && e.Kind == "candidate" &&
                e.InstructionIndex == selected.InstructionIndex && e.StructurallyAdmissible &&
                e.ClassPressureCostBucket == selected.ClassPressureCostBucket))
                throw new Exception("Selected candidate lacks structural admission event.");
        HybridCpuInstructionWord[] wide = Enumerable.Range(0, 48).Select(n => Carrier($"ADDI r{1 + n % 31}, r0, 1")).ToArray();
        HybridCpuProfileCompilationResultV1 wideTrace = HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, wide,
            new() { CaptureDecisionTrace = true });
        if (wideTrace.DecisionTrace is not { Truncated: true } bounded || bounded.Events.Count != 1024 ||
            !wideTrace.CompiledProgram.ProgramImage.SequenceEqual(HybridCpuCanonicalCompiler.CompileProgram(0, wide).ProgramImage))
            throw new Exception("Bounded trace changed compilation.");
        Console.WriteLine($"PASS profile decision trace: deterministic/admitted/buckets/observational/truncation checks; events={trace.Events.Count}, bounded={bounded.Events.Count}");
        if (active.ProfileStatus != "profile-active" || active.ActiveProfileDigest is null ||
            active.EffectiveOptionsDigest == off.EffectiveOptionsDigest ||
            active.ScheduleFingerprint != repeat.ScheduleFingerprint ||
            !active.CompiledProgram.ProgramImage.SequenceEqual(repeat.CompiledProgram.ProgramImage))
            throw new Exception("Active profile selection/identity drift.");
        SameDefault(HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words,
            new(true, json.Replace(off.ProfileInputIdentity, "stale"))));
        HybridCpuInstructionWord[] changedWords = words.Append(words[0]).ToArray();
        HybridCpuProfileCompilationResultV1 changed = HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, changedWords, new(true, json));
        if (changed.ProfileInputIdentity == off.ProfileInputIdentity || changed.ActiveProfileDigest is not null ||
            !changed.CompiledProgram.ProgramImage.SequenceEqual(HybridCpuCanonicalCompiler.CompileProgram(0, changedWords).ProgramImage))
            throw new Exception("Changed input reused stale profile.");
        HybridCpuInstructionWord[] cappedWords = Enumerable.Repeat(words[0], 49).ToArray();
        HybridCpuProfileCompilationResultV1 cappedOff = HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, cappedWords, new());
        string cappedJson = json.Replace(off.ProfileInputIdentity, cappedOff.ProfileInputIdentity);
        HybridCpuProfileCompilationResultV1 capped = HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, cappedWords, new(true, cappedJson));
        if (capped.ProfileStatus != "profile-disabled-scheduler-cap" || capped.ActiveProfileDigest is not null ||
            capped.EffectiveOptionsDigest != cappedOff.EffectiveOptionsDigest ||
            !capped.CompiledProgram.ProgramImage.SequenceEqual(cappedOff.CompiledProgram.ProgramImage))
            throw new Exception("Scheduler cap profile fallback drift.");
        Console.WriteLine("PASS profile compilation lane: 8 bounded checks; default/disabled equality, active repeat, stale input, scheduler cap, counter overflow; Qualification: no");
        Console.WriteLine(JsonSerializer.Serialize(new { active.ProfileInputIdentity, active.ProfileStatus,
            active.ActiveProfileDigest, active.EffectiveOptionsDigest, active.ScheduleFingerprint,
            TraceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(trace)))),
            ImageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(active.CompiledProgram.ProgramImage)) }));
    }
}
