using System.Security.Cryptography;
using System.Text.Json;

const string ModelRoot = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE";
const string BridgeRoot = @"C:\Users\Yuriy\Desktop\HybridCPU Compilers\Compilers\HybridCPU_Compiler\HardwareBridge";
const int Ways = 4;
const string AdmissionRel = @"CloseToHSL\Core\Pipeline\Scheduling\Admission\MicroOpScheduler.Admission.cs";
const string SmtRel = @"CloseToHSL\Core\Pipeline\Scheduling\Smt\MicroOpScheduler.SMT.cs";
const string SchedulerRel = @"CloseToHSL\Core\Pipeline\Scheduling\MicroOpScheduler.cs";
const string AssistRel = @"CloseToHSL\Core\Pipeline\Assist\Runtime\MicroOpScheduler.Assist.cs";
var expected = new Dictionary<string, (string RelativePath, string Sha256)>
{
    ["admission"] = (AdmissionRel, "6cdd37f43a0a58c0b887f9e8bffe9d2d17abb5d404f7ace70fead05db4edbe25"),
    ["smt"] = (SmtRel, "4ecfa34a01d71033b9d6c0b3f2ce752cb50ff8d6004b3a2ba3d6f1c5710e340d"),
    ["scheduler"] = (SchedulerRel, "c7e675d76bb4eed9ecccb0b20c13c973895cce556e93c21457d966add731d8bf"),
    ["assist"] = (AssistRel, "8515039c3d770382fd448439d2453c01bd42397f3b525b413d411161bf8035a8"),
};

if (args.Length != 1) throw new ArgumentException("Usage: SmtRankTailReference <existing-output-directory-inside-HardwareBridge>");
string output = Path.GetFullPath(args[0]);
if (!output.StartsWith(BridgeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(output))
    throw new InvalidOperationException("Output must be an existing directory under HardwareBridge.");

var sources = new Dictionary<string, object>();
var sourceBytes = new Dictionary<string, byte[]>();
foreach (var (name, (relative, sha)) in expected)
{
    string path = Path.Combine(ModelRoot, relative);
    byte[] bytes = File.ReadAllBytes(path);
    string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    if (actual != sha) throw new InvalidDataException($"Pinned {name} bytes changed: expected {sha}, got {actual}");
    sourceBytes[name] = bytes;
    sources[name] = new { path, bytes = bytes.Length, sha256 = actual };
}

string admission = Normalize(System.Text.Encoding.UTF8.GetString(sourceBytes["admission"]));
string smt = Normalize(System.Text.Encoding.UTF8.GetString(sourceBytes["smt"]));
string scheduler = Normalize(System.Text.Encoding.UTF8.GetString(sourceBytes["scheduler"]));
string assist = Normalize(System.Text.Encoding.UTF8.GetString(sourceBytes["assist"]));
Require(admission.Contains(Normalize("if (vt == ownerVirtualThreadId) continue; ranked[count++] = vt;"), StringComparison.Ordinal), "rank method writes every non-owner in the [0..3] loop");
Require(smt.Contains(Normalize("Span<int> vtOrder = stackalloc int[SMT_WAYS];"), StringComparison.Ordinal) &&
        smt.Contains(Normalize("GetCreditRankedOrder(ownerVirtualThreadId, vtOrder);"), StringComparison.Ordinal) &&
        smt.Contains(Normalize("for (int i = 0; i < SMT_WAYS; i++) { int vt = vtOrder[i];"), StringComparison.Ordinal), "main caller stackalloc and four-entry read");
Require(scheduler.Contains(Normalize("if ((uint)virtualThreadId >= SMT_WAYS) return false;"), StringComparison.Ordinal) &&
        scheduler.Contains(Normalize("return virtualThreadId != ownerVirtualThreadId && IsReadyCandidate(virtualThreadId);"), StringComparison.Ordinal), "main candidate range and owner filter");
Require(assist.Contains(Normalize("Span<int> vtOrder = stackalloc int[SMT_WAYS];"), StringComparison.Ordinal) &&
        assist.Contains(Normalize("int vtId = vtOrder[orderIndex]; if (vtId == ownerVirtualThreadId || !_smtAssistPortValid[vtId] || _smtAssistPorts[vtId] == null)"), StringComparison.Ordinal), "assist caller directly indexes port arrays after owner test");

uint[] tails = [0, 1, 2, 3, 4, uint.MaxValue, 0x80000000, 0xFFFFFFFE];
long cases = 0, mainDuplicateVectors = 0, mainInvalidTailFilteredVectors = 0;
long assistOutOfRangeVectors = 0, assistDuplicateProbeVectors = 0;
long assistInvalidShortCircuits = 0, assistOwnerShortCircuits = 0;
var witnesses = new Dictionary<string, object?>();
foreach (int owner in Enumerable.Range(0, Ways))
foreach (uint tail in tails)
for (int packedCredits = 0; packedCredits < 256; packedCredits++)
for (int readyMask = 0; readyMask < 16; readyMask++)
for (int validMask = 0; validMask < 16; validMask++)
for (int presentMask = 0; presentMask < 16; presentMask++)
{
    int[] ranks = CreditRanks(owner, packedCredits);
    uint rank3 = tail;
    int[] order = [ranks[0], ranks[1], ranks[2], unchecked((int)rank3)];
    int[] mainAttempts = order.Where(vt => (uint)vt < Ways && vt != owner && (readyMask & (1 << vt)) != 0).ToArray();
    bool hasMainDuplicate = mainAttempts.Distinct().Count() != mainAttempts.Length;
    bool invalidTailFiltered = (uint)order[3] >= Ways;

    int assistReads = 0;
    int assistPortProbes = 0;
    bool assistOob = false;
    bool assistOwnerShort = false;
    bool assistInvalidShort = false;
    int[] assistEligible = [];
    var eligible = new List<int>();
    foreach (int vtId in order)
    {
        if (vtId == owner) { assistOwnerShort = true; continue; }
        assistReads++;
        if ((uint)vtId >= Ways) { assistOob = true; break; }
        if ((validMask & (1 << vtId)) == 0) { assistInvalidShort = true; continue; }
        assistPortProbes++;
        if ((presentMask & (1 << vtId)) == 0) continue;
        eligible.Add(vtId);
    }
    assistEligible = eligible.ToArray();
    bool assistDuplicate = assistEligible.Distinct().Count() != assistEligible.Length;

    cases++;
    if (hasMainDuplicate) mainDuplicateVectors++;
    if (invalidTailFiltered) mainInvalidTailFilteredVectors++;
    if (assistOob) assistOutOfRangeVectors++;
    if (assistDuplicate) assistDuplicateProbeVectors++;
    if (assistInvalidShort) assistInvalidShortCircuits++;
    if (assistOwnerShort) assistOwnerShortCircuits++;

    if (hasMainDuplicate && !witnesses.ContainsKey("mainDuplicateValidTail"))
        witnesses["mainDuplicateValidTail"] = new { owner, tail, credits = UnpackCredits(packedCredits), readyMask, ranks, attempts = mainAttempts };
    if (assistOob && !witnesses.ContainsKey("assistOutOfRangeTail"))
        witnesses["assistOutOfRangeTail"] = new { owner, tail, credits = UnpackCredits(packedCredits), validMask, presentMask, ranks, order };
    if (assistDuplicate && !witnesses.ContainsKey("assistDuplicatePresentTail"))
        witnesses["assistDuplicatePresentTail"] = new { owner, tail, credits = UnpackCredits(packedCredits), validMask, presentMask, ranks, candidates = assistEligible };
    if (invalidTailFiltered && !witnesses.ContainsKey("mainOutOfRangeTailFiltered"))
        witnesses["mainOutOfRangeTailFiltered"] = new { owner, tail, credits = UnpackCredits(packedCredits), readyMask, ranks, attempts = mainAttempts };
}

var receipt = new
{
    schema = "circuitweave.smt-rank-tail-reference/v1",
    classification = "source-pinned C# reference-vector exploration; not RTL, not a processor equivalence result",
    modelCommit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f",
    goldenCommit = "6946ccab9d888f7dae0a4d8e18480f436138acd9",
    smtWays = Ways,
    sources,
    sourceContracts = new
    {
        rank = "Credit descending, VT id ascending on ties; owner omitted; 3 entries written for valid owner 0..3.",
        main = "4 rank entries read; IsReadyNonOwnerCandidate rejects OOR/owner and checks ready bit; duplicate valid VT entries remain separate legalVts attempts.",
        assist = "4 rank entries read; owner short-circuits before indexing; then valid[] and ports[] are indexed without OOR guard; valid & present entries reach candidate probe.",
        limitations = "Does not model later typed-slot legality, lane assignment, injection quota/state changes, stack bytes, clock/reset or RTL. Assist OOB counts are reachable outcomes when the loop reaches the out-of-range rank with quota still available; earlier successful injections could stop the loop. Credits are exhaustively sampled over 0..3 to cover ordering/tie permutations, not their full source int domain."
    },
    coverage = new
    {
        cases,
        owners = new[] { 0, 1, 2, 3 },
        tailValues = tails.Select(v => new { value = v, classification = v < Ways ? "valid" : "out-of-range" }),
        creditsPerVt = new[] { 0, 1, 2, 3 },
        readinessMasks = 16,
        assistValidMasks = 16,
        assistPresentMasks = 16,
        mainDuplicateVectors,
        mainInvalidTailFilteredVectors,
        assistOutOfRangeReachableIfQuotaAvailableVectors = assistOutOfRangeVectors,
        assistDuplicateProbeVectors,
        assistInvalidShortCircuits,
        assistOwnerShortCircuits
    },
    witnesses
};
string json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });
string receiptPath = Path.Combine(output, "smt-rank-tail-reference-v1.json");
File.WriteAllText(receiptPath, json + Environment.NewLine);
Console.WriteLine($"PASS: {cases:N0} source-pinned SMT rank-tail reference vectors");
Console.WriteLine($"main duplicate-attempt vectors: {mainDuplicateVectors:N0}; assist out-of-range outcomes: {assistOutOfRangeVectors:N0}; assist duplicate-probe vectors: {assistDuplicateProbeVectors:N0}");
Console.WriteLine(receiptPath);

static void Require(bool condition, string description)
{
    if (!condition) throw new InvalidDataException("Source contract check failed: " + description);
}

static string Normalize(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

static int[] CreditRanks(int owner, int packedCredits)
{
    int[] entries = Enumerable.Range(0, Ways).Where(vt => vt != owner).ToArray();
    for (int i = 0; i < entries.Length - 1; i++)
    for (int j = i + 1; j < entries.Length; j++)
    {
        int a = entries[i], b = entries[j];
        int ca = Credit(packedCredits, a), cb = Credit(packedCredits, b);
        if (cb > ca || (cb == ca && b < a)) (entries[i], entries[j]) = (entries[j], entries[i]);
    }
    return entries;
}

static int Credit(int packed, int vt) => (packed >> (vt * 2)) & 3;
static int[] UnpackCredits(int packed) => Enumerable.Range(0, Ways).Select(vt => Credit(packed, vt)).ToArray();
