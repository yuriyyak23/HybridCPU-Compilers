using System.Text.Json;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core.Registers;

if (args.Length != 2) throw new ArgumentException("Usage: RuntimeOracle <csr-register-inventory.json> <output.json>");
using JsonDocument inventoryDoc = JsonDocument.Parse(await File.ReadAllBytesAsync(args[0]));
var addresses = inventoryDoc.RootElement.EnumerateArray().Select(x =>
    (Name: x.GetProperty("AddressMember").GetString()!, Address: x.GetProperty("Address").GetUInt16())).ToArray();
var schedule = new List<(int Valid, ushort Address, uint Privilege, int Operation, ulong Data)>();
foreach (var item in addresses)
foreach (uint privilege in new uint[] { 3, 1, 0, 0x80000000 })
foreach (int operation in new[] { 1, 2, 0, 3, 4 })
    schedule.Add((schedule.Count % 19 == 7 ? 0 : 1, item.Address, privilege, operation,
        unchecked(0x9E3779B97F4A7C15UL * (ulong)(schedule.Count + 1))));
foreach (ushort address in new ushort[] { 0x0fff, 0xffff })
foreach (int operation in Enumerable.Range(0, 5))
    schedule.Add((schedule.Count % 19 == 7 ? 0 : 1, address, 3, operation,
        unchecked(0x9E3779B97F4A7C15UL * (ulong)(schedule.Count + 1))));
schedule.Add((1, 0x0300, 3, 7,
    unchecked(0x9E3779B97F4A7C15UL * (ulong)(schedule.Count + 1))));
schedule.Add((1, 0x0c00, 0, 7,
    unchecked(0x9E3779B97F4A7C15UL * (ulong)(schedule.Count + 1))));

var callbackLog = new List<string>();
#if ACTIVE_MODEL
var file = new CsrFile(kind =>
{
    string name = kind.ToString();
    callbackLog.Add("acquire:" + name);
    return new AccessLease(() => callbackLog.Add("dispose:" + name));
});
#else
var file = new CsrFile();
#endif
var results = new List<object>(schedule.Count);
for (int index = 0; index < schedule.Count; index++)
{
    var e = schedule[index];
    object? value = null;
    object? fault = null;
    bool skipped = e.Valid == 0 || e.Operation is < 0 or > 4;
    int callbacksStart = callbackLog.Count;
    if (!skipped)
    {
        try
        {
            PrivilegeLevel privilege = unchecked((PrivilegeLevel)(int)e.Privilege);
            switch (e.Operation)
            {
                case 0: value = file.Read(e.Address, privilege); break;
                case 1: file.Write(e.Address, e.Data, privilege); break;
                case 2: file.HardwareWrite(e.Address, e.Data); break;
                case 3: value = file.DirectRead(e.Address); break;
                case 4: value = file.IsRegistered(e.Address); break;
            }
        }
        catch (CsrPrivilegeFaultException ex)
        {
            fault = new { kind = "Privilege", address = ex.CsrAddress, required = (int)ex.RequiredLevel,
                actual = unchecked((uint)(int)ex.ActualLevel), isWrite = ex.IsWrite };
        }
        catch (CsrUnknownAddressException ex)
        {
            fault = new { kind = "UnknownAddress", address = ex.CsrAddress };
        }
    }
    string[] operationCallbacks = callbackLog.Skip(callbacksStart).ToArray();
    var state = new Dictionary<string, ulong>(StringComparer.Ordinal);
    foreach (var item in addresses) state[item.Name] = file.DirectRead(item.Address);
    callbackLog.Clear();
    results.Add(new { index, valid = e.Valid, address = e.Address, privilege = e.Privilege,
        operation = e.Operation, writeData = e.Data, skipped, value, fault,
        reservationCallbacks = operationCallbacks, state });
}
var output = new
{
    schema = "circuitweave.csr-active-runtime-chain/v1",
#if ACTIVE_MODEL
    model = "ACTIVE",
#else
    model = "GOLDEN",
#endif
    eventCount = results.Count,
    events = results
};
await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"PASS exact CsrFile runtime model={output.model} events={results.Count}");

sealed class AccessLease(Action dispose) : IDisposable
{
    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) throw new InvalidOperationException("Reservation disposed twice.");
        _disposed = true;
        dispose();
    }
}
