using CircuitWeave.Contracts;
using CircuitWeave.HWIR;

internal static class VectorSegmentReadOracle
{
    public sealed record Result(int Vectors, int Mismatches, string Seed, string TraceSha256, string Scope);
    private sealed record Slot(int Phase, ulong Id, ulong Device, ulong Address, uint Size, bool Succeeded, ulong PublishedCycle);
    private sealed record Event(int Kind, ulong RequestId = 0, ulong Device = 0, ulong Address = 0, int Size = 1, bool Succeeded = true);

    public static Result Crosscheck(HwModule module, int seed, int maxEvents, string? vectorPath = null)
    {
        if (module.Name != "vector_segment_read_lifecycle") throw new ArgumentException("Unexpected module.");
        var nodes = module.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var drives = module.Nodes.OfType<HwOutputDriveNode>().ToDictionary(n => n.OutputPort, n => n.Value, StringComparer.Ordinal);
        var slots = Enumerable.Range(0, 8).Select(_ => new Slot(0, 0, 0, 0, 0, false, 0)).ToArray();
        uint eventCount = 0;
        ulong cycle = 0, nextId = 1;
        var vectors = new List<Event>();

        // Directed boundaries: positive signed sizes, nonpositive sizes, capacity,
        // FIFO service, pending/published cancellation and completion take.
        vectors.AddRange(Enumerable.Range(0, 8).Select(i => new Event(0, Device: (ulong)i + 10, Address: (ulong)i * 64, Size: i == 7 ? int.MaxValue : i + 1)));
        vectors.Add(new Event(0, Size: 0));
        vectors.Add(new Event(0, Size: -1));
        vectors.Add(new Event(0, Size: 12)); // capacity backpressure
        vectors.Add(new Event(3, RequestId: 4)); // cancel queued
        vectors.Add(new Event(1, Succeeded: false)); // service oldest live request
        vectors.Add(new Event(3, RequestId: 1)); // cancel pending completion
        vectors.Add(new Event(1)); // publish pending, service next queued
        vectors.Add(new Event(3, RequestId: 2)); // cancel published completion
        vectors.Add(new Event(1)); // promote next pending
        vectors.Add(new Event(2, RequestId: 3)); // take published completion

        var random = new Random(seed);
        for (int i = vectors.Count; i < maxEvents; i++)
        {
            int kind = random.Next(100) switch
            {
                < 37 => 0,
                < 69 => 1,
                < 84 => 2,
                _ => 3
            };
            Slot? selected = slots.Where(s => s.Phase != 0).MinBy(s => s.Id);
            ulong id = selected?.Id ?? 0;
            int size = kind == 0 ? new[] { int.MinValue, -1, 0, 1, 2, 8, 64, int.MaxValue }[random.Next(8)] : 1;
            vectors.Add(new Event(kind, kind is 2 or 3 && random.Next(4) != 0 ? id : (ulong)random.NextInt64(0, (long)Math.Max(nextId + 2, 2)),
                (ulong)random.NextInt64(0, 100), (ulong)random.NextInt64(0, 1_000_000), size, random.Next(2) == 1));
        }

        int[] phaseWidths = [2, 64, 64, 64, 32, 1, 64];
        string[] fieldNames = ["phase", "id", "device", "address", "size", "succeeded", "published_cycle"];
        var trace = new List<string>(maxEvents);
        var vectorRows = vectorPath is null ? null : new List<object>(maxEvents);
        for (int index = 0; index < vectors.Count; index++)
        {
            Event e = vectors[index];
            var input = new Dictionary<string, ulong>(StringComparer.Ordinal)
            {
                ["event_count"] = eventCount, ["cycle"] = cycle, ["next_id"] = nextId,
                ["event_kind"] = (ulong)e.Kind, ["request_id"] = e.RequestId,
                ["device"] = e.Device, ["address"] = e.Address,
                ["size_signed"] = unchecked((uint)e.Size), ["service_succeeded"] = e.Succeeded ? 1UL : 0UL
            };
            for (int s = 0; s < 8; s++)
            {
                Slot slot = slots[s];
                for (int f = 0; f < fieldNames.Length; f++)
                    input[$"s{s}_{fieldNames[f]}"] = f switch
                    {
                        0 => (ulong)slot.Phase, 1 => slot.Id, 2 => slot.Device, 3 => slot.Address,
                        4 => slot.Size, 5 => slot.Succeeded ? 1UL : 0UL, _ => slot.PublishedCycle
                    };
            }
            Dictionary<string, ulong> actual = Evaluate(module, nodes, drives, input);
            var expectedOutputs = new Dictionary<string, ulong>(StringComparer.Ordinal);

            bool valid = eventCount < 4096 && nextId != 0 && nextId <= eventCount + 1 && cycle <= eventCount &&
                slots.All(s => s.Phase == 0 || (s.Id != 0 && s.Id < nextId && s.Size > 0 && (s.Phase != 3 || s.PublishedCycle <= cycle))) &&
                slots.Where(s => s.Phase != 0).Select(s => s.Id).Distinct().Count() == slots.Count(s => s.Phase != 0);
            if (!valid || actual["valid"] != 1) throw Mismatch(index, "valid", valid ? 1UL : 0UL, actual["valid"]);
            expectedOutputs["valid"] = valid ? 1UL : 0UL;

            Slot[] old = (Slot[])slots.Clone();
            bool accept = e.Kind == 0;
            bool positiveSize = e.Size > 0;
            int free = Array.FindIndex(old, s => s.Phase == 0);
            bool accepted = accept && positiveSize && free >= 0;
            bool rejected = accept && !positiveSize;
            bool backpressured = accept && positiveSize && free < 0;
            bool service = e.Kind == 1 && old.Any(s => s.Phase == 1);
            Slot? oldestQueued = service ? old.Where(s => s.Phase == 1).MinBy(s => s.Id) : null;
            Slot? taken = e.Kind == 2 ? old.SingleOrDefault(s => s.Phase == 3 && s.Id == e.RequestId) : null;
            Slot? canceled = e.Kind == 3 ? old.SingleOrDefault(s => s.Phase != 0 && s.Id == e.RequestId) : null;
            ulong nextCycle = cycle + (e.Kind == 1 ? 1UL : 0UL);
            Check("accepted", accepted ? 1UL : 0UL);
            Check("accepted_id", accepted ? nextId : 0);
            Check("rejected_nonpositive_size", rejected ? 1UL : 0UL);
            Check("backpressured_at_capacity", backpressured ? 1UL : 0UL);
            Check("service_this_edge", service ? 1UL : 0UL);
            Check("service_request_id", oldestQueued?.Id ?? 0);
            Check("next_event_count", eventCount + 1UL);
            Check("next_cycle", nextCycle);
            Check("next_request_id", nextId + (accepted ? 1UL : 0UL));
            Check("taken", taken is null ? 0UL : 1UL);
            Check("taken_succeeded", taken?.Succeeded == true ? 1UL : 0UL);
            Check("taken_published_cycle", taken?.PublishedCycle ?? 0);
            Check("canceled", canceled is null ? 0UL : 1UL);

            var nextSlots = old.ToArray();
            for (int s = 0; s < old.Length; s++)
            {
                Slot slot = old[s];
                bool remove = (taken is not null && slot.Id == taken.Id) || (canceled is not null && slot.Id == canceled.Id);
                bool promote = e.Kind == 1 && slot.Phase == 2;
                bool serve = service && oldestQueued?.Id == slot.Id;
                bool insert = accepted && s == free;
                Slot next = slot;
                if (remove) next = new Slot(0, slot.Id, slot.Device, slot.Address, slot.Size, slot.Succeeded, slot.PublishedCycle);
                else if (insert) next = new Slot(1, nextId, e.Device, e.Address, unchecked((uint)e.Size), slot.Succeeded, slot.PublishedCycle);
                else if (serve) next = slot with { Phase = 2, Succeeded = e.Succeeded };
                else if (promote) next = slot with { Phase = 3, PublishedCycle = nextCycle };
                nextSlots[s] = next;
                for (int f = 0; f < fieldNames.Length; f++)
                {
                    ulong expected = f switch { 0 => (ulong)next.Phase, 1 => next.Id, 2 => next.Device, 3 => next.Address, 4 => next.Size, 5 => next.Succeeded ? 1UL : 0UL, _ => next.PublishedCycle };
                    Check($"next_s{s}_{fieldNames[f]}", expected);
                }
            }
            slots = nextSlots;
            eventCount++;
            if (e.Kind == 1) cycle++;
            if (accepted) nextId++;
            trace.Add($"{index}:{e.Kind}:{e.RequestId}:{(accepted ? 1 : 0)}:{(service ? oldestQueued!.Id : 0)}:{(taken is null ? 0 : 1)}:{(canceled is null ? 0 : 1)}:{cycle}:{nextId}");

            void Check(string signal, ulong expected)
            {
                expectedOutputs[signal] = expected;
                if (actual[signal] != expected) throw new InvalidDataException($"Event={e}, eventCount={eventCount}, cycle={cycle}, nextId={nextId}; old slots: {string.Join(';', slots.Select((x, i) => i + ":" + x.Phase + "/" + x.Id + "/" + x.Succeeded + "/" + x.PublishedCycle))}; " + Mismatch(index, signal, expected, actual[signal]).Message);
            }
            vectorRows?.Add(new { inputs = input, outputs = expectedOutputs });
        }
        if (vectorRows is not null)
            File.WriteAllLines(vectorPath!, vectorRows.Select(row => System.Text.Json.JsonSerializer.Serialize(row)));
        string traceText = string.Join("\n", trace);
        return new(vectors.Count, 0, seed.ToString("x"), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(traceText))).ToLowerInvariant(), "independent software lifecycle oracle vs generated HWIR equations only; not active/Golden C# runtime or RTL");

        static InvalidDataException Mismatch(int i, string signal, ulong expected, ulong actual) =>
            new($"Vector segment lifecycle oracle mismatch at event {i}, {signal}: expected {expected}, actual {actual}.");
    }

    private static Dictionary<string, ulong> Evaluate(HwModule module, IReadOnlyDictionary<string, HwNode> nodes,
        IReadOnlyDictionary<string, string> drives, IReadOnlyDictionary<string, ulong> inputs)
    {
        var values = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (HwPortContract port in module.Ports.Where(p => p.Direction == HwPortDirection.Input))
        {
            if (!inputs.TryGetValue(port.Name, out ulong value)) throw new InvalidDataException("Missing input " + port.Name);
            values.Add(port.Name, Mask(value, port.Type.Width));
        }
        ulong Get(string id)
        {
            if (values.TryGetValue(id, out ulong old)) return old;
            if (!nodes.TryGetValue(id, out HwNode? node)) throw new InvalidDataException("Unknown HWIR value " + id);
            ulong result = node switch
            {
                HwConstantNode c => Convert.ToUInt64(c.BitPattern, 2),
                HwMuxNode m => Get(m.Condition) != 0 ? Get(m.WhenTrue) : Get(m.WhenFalse),
                HwOperationNode o => Apply(o, Get),
                _ => throw new InvalidDataException("Unsupported node in evaluator: " + node.GetType().Name)
            };
            result = Mask(result, node switch { HwConstantNode c => c.Type.Width, HwMuxNode m => m.ResultType.Width, HwOperationNode o => o.ResultType.Width, _ => 64 });
            values.Add(id, result);
            return result;
        }
        return drives.ToDictionary(pair => pair.Key, pair => Get(pair.Value), StringComparer.Ordinal);

        static ulong Apply(HwOperationNode op, Func<string, ulong> get)
        {
            ulong a = get(op.Operands[0]);
            ulong b = op.Operands.Count > 1 ? get(op.Operands[1]) : 0;
            return op.Opcode switch
            {
                "and" => a & b,
                "or" => a | b,
                "xor" => a ^ b,
                "eq" => a == b ? 1UL : 0UL,
                "ult64" => a < b ? 1UL : 0UL,
                "zext" => a,
                "add" => unchecked(a + b),
                "mux" => a != 0 ? b : get(op.Operands[2]),
                _ => throw new InvalidDataException("Unsupported opcode in evaluator: " + op.Opcode)
            };
        }
    }

    private static ulong Mask(ulong value, int width) => width == 64 ? value : value & ((1UL << width) - 1);
}
