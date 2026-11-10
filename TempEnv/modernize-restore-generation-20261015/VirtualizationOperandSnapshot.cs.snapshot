using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace YAKSys_Hybrid_CPU.Core;

internal enum VirtualizationOperandCaptureDecision : byte
{
    Captured = 0,
    MissingInput = 1,
    InvalidE1 = 2,
    NotExactVmCall = 3,
    InvalidRegisterShape = 4,
    InvalidLeafValue = 5,
    LeafHighBitsSet = 6,
    MissingDomainIdentity = 7,
    InvalidSlotIdentity = 8,
    MissingRestoreGeneration = 9,
    DuplicateAttempt = 10,
    OwnerPolicyMismatch = 11,
}

internal readonly record struct VirtualizationOperandCaptureResult(
    VirtualizationOperandCaptureDecision Decision,
    VirtualizationOperandSnapshot? Snapshot,
    string Reason)
{
    internal bool IsCaptured =>
        Decision == VirtualizationOperandCaptureDecision.Captured &&
        Snapshot is not null;
}

internal enum VirtualizationOperandValidationDecision : byte
{
    ValidForE2Input = 0,
    MissingInput = 1,
    CarrierOrE1Mismatch = 2,
    OwnerPolicyMismatch = 3,
    RestoreGenerationMismatch = 4,
    IdentityMismatch = 5,
    DigestMismatch = 6,
}

internal readonly record struct VirtualizationOperandValidationResult(
    VirtualizationOperandValidationDecision Decision,
    string Reason)
{
    internal bool IsValidForE2Input =>
        Decision == VirtualizationOperandValidationDecision.ValidForE2Input;
}

/// <summary>
/// Immutable one-attempt operand identity captured after live E1 validation.
/// It is not serializable authority and cannot authorize execution or publication.
/// </summary>
internal sealed class VirtualizationOperandSnapshot
{
    private VirtualizationOperandSnapshot(
        ulong attemptId,
        ulong e1IssuerGeneration,
        int virtualThreadId,
        int ownerContextId,
        ulong domainTag,
        byte rs1Selector,
        ulong rs1Value,
        byte rs2Selector,
        ulong rs2Value,
        byte rdSelector,
        int sourceSlotId,
        int workingSlotId,
        ulong bundleIdentity,
        ulong replayEpoch,
        ulong carrierIdentityDigest,
        ulong restoreGeneration,
        ulong captureSequence,
        string ownerPolicyDigest,
        string operandDigest)
    {
        AttemptId = attemptId;
        E1IssuerGeneration = e1IssuerGeneration;
        VirtualThreadId = virtualThreadId;
        OwnerContextId = ownerContextId;
        DomainTag = domainTag;
        Rs1Selector = rs1Selector;
        Rs1Value = rs1Value;
        Rs2Selector = rs2Selector;
        Rs2Value = rs2Value;
        RdSelector = rdSelector;
        SourceSlotId = sourceSlotId;
        WorkingSlotId = workingSlotId;
        BundleIdentity = bundleIdentity;
        ReplayEpoch = replayEpoch;
        CarrierIdentityDigest = carrierIdentityDigest;
        RestoreGeneration = restoreGeneration;
        CaptureSequence = captureSequence;
        OwnerPolicyDigest = ownerPolicyDigest;
        OperandDigest = operandDigest;
    }

    internal uint SchemaVersion => 1;
    internal ulong AttemptId { get; }
    internal ulong E1IssuerGeneration { get; }
    internal int VirtualThreadId { get; }
    internal int OwnerContextId { get; }
    internal ulong DomainTag { get; }
    internal byte Rs1Selector { get; }
    internal ulong Rs1Value { get; }
    internal byte Rs2Selector { get; }
    internal ulong Rs2Value { get; }
    internal byte RdSelector { get; }
    internal int SourceSlotId { get; }
    internal int WorkingSlotId { get; }
    internal ulong BundleIdentity { get; }
    internal ulong ReplayEpoch { get; }
    internal ulong CarrierIdentityDigest { get; }
    internal ulong RestoreGeneration { get; }
    internal ulong CaptureSequence { get; }
    internal string OwnerPolicyDigest { get; }
    internal string OperandDigest { get; }

    internal bool BackendExecutionAuthorized => false;
    internal bool CompletionPublicationAuthorized => false;
    internal bool RetirePublicationAuthorized => false;

    internal static VirtualizationOperandSnapshot CreateValidated(
        SafetyVerifier.VirtualizationAdmissionCertificate e1,
        byte rs1Selector,
        ulong rs1Value,
        ulong restoreGeneration,
        ulong captureSequence,
        VirtualizationOperationOwnerSnapshot ownerPolicy)
    {
        string digest = VirtualizationOperandSnapshotDigest.Compute(
            e1.AttemptId,
            e1.IssuerGeneration,
            e1.VirtualThreadId,
            e1.OwnerContextId,
            e1.DomainTag,
            rs1Selector,
            rs1Value,
            rs2Selector: 0,
            rs2Value: 0,
            rdSelector: 0,
            e1.SourceSlotId,
            e1.WorkingSlotId,
            e1.BundleIdentity,
            e1.ReplayEpoch,
            e1.CarrierIdentityDigest,
            restoreGeneration,
            captureSequence,
            ownerPolicy.PolicyDigest);
        return new(
            e1.AttemptId,
            e1.IssuerGeneration,
            e1.VirtualThreadId,
            e1.OwnerContextId,
            e1.DomainTag,
            rs1Selector,
            rs1Value,
            0,
            0,
            0,
            e1.SourceSlotId,
            e1.WorkingSlotId,
            e1.BundleIdentity,
            e1.ReplayEpoch,
            e1.CarrierIdentityDigest,
            restoreGeneration,
            captureSequence,
            ownerPolicy.PolicyDigest,
            digest);
    }
}

internal static class VirtualizationOperandSnapshotDigest
{
    private static readonly byte[] Envelope = Encoding.ASCII.GetBytes("HCPUOP1\0");

    internal static string Compute(
        ulong attemptId,
        ulong issuerGeneration,
        int virtualThreadId,
        int ownerContextId,
        ulong domainTag,
        byte rs1Selector,
        ulong rs1Value,
        byte rs2Selector,
        ulong rs2Value,
        byte rdSelector,
        int sourceSlotId,
        int workingSlotId,
        ulong bundleIdentity,
        ulong replayEpoch,
        ulong carrierIdentityDigest,
        ulong restoreGeneration,
        ulong captureSequence,
        string ownerPolicyDigest)
    {
        using var stream = new MemoryStream();
        stream.Write(Envelope);
        WriteUInt64(stream, attemptId);
        WriteUInt64(stream, issuerGeneration);
        WriteUInt32(stream, unchecked((uint)virtualThreadId));
        WriteUInt32(stream, unchecked((uint)ownerContextId));
        WriteUInt64(stream, domainTag);
        stream.WriteByte(rs1Selector);
        WriteUInt64(stream, rs1Value);
        stream.WriteByte(rs2Selector);
        WriteUInt64(stream, rs2Value);
        stream.WriteByte(rdSelector);
        WriteUInt32(stream, unchecked((uint)sourceSlotId));
        WriteUInt32(stream, unchecked((uint)workingSlotId));
        WriteUInt64(stream, bundleIdentity);
        WriteUInt64(stream, replayEpoch);
        WriteUInt64(stream, carrierIdentityDigest);
        WriteUInt64(stream, restoreGeneration);
        WriteUInt64(stream, captureSequence);
        stream.Write(Convert.FromHexString(ownerPolicyDigest));
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }
}

internal sealed class VirtualizationOperandSnapshotMaterializer
{
    private readonly ConditionalWeakTable<
        SafetyVerifier.VirtualizationAdmissionCertificate,
        VirtualizationOperandSnapshot> _captured = new();
    private ulong _nextCaptureSequence = 1;

    internal VirtualizationOperandCaptureResult CaptureAfterValidatedE1(
        VmxMicroOp? carrier,
        SafetyVerifier.VirtualizationAdmissionCertificate? e1,
        ulong rs1Value,
        ulong restoreGeneration,
        VirtualizationOperationOwnerSnapshot? ownerPolicy)
    {
        if (carrier is null || e1 is null || ownerPolicy is null)
            return Deny(VirtualizationOperandCaptureDecision.MissingInput, "Operand capture requires carrier, live E1 and O1.");

        if (!ReferenceEquals(carrier.VirtualizationAdmission, e1))
            return Deny(VirtualizationOperandCaptureDecision.InvalidE1, "Operand capture requires the E1 attached to this exact carrier.");

        if (e1.Opcode != Processor.CPU_Core.IsaOpcodeValues.VMCALL ||
            e1.Operation != VmxOperationKind.VmCall ||
            !carrier.TryResolveFrozenOperation(out VmxOperationKind operation) ||
            operation != VmxOperationKind.VmCall)
        {
            return Deny(VirtualizationOperandCaptureDecision.NotExactVmCall, "Operand capture accepts only the canonical VMCALL carrier.");
        }

        if (carrier.Rs1 == 0 ||
            carrier.Rs1 >= Registers.RenameMap.ArchRegs ||
            carrier.Rs2 != 0 ||
            carrier.Rd != 0)
        {
            return Deny(VirtualizationOperandCaptureDecision.InvalidRegisterShape, "VMCALL requires Rs1=architectural register, Rs2=x0 and Rd=x0.");
        }

        if ((rs1Value & ~0xFFFFUL) != 0)
            return Deny(VirtualizationOperandCaptureDecision.LeafHighBitsSet, "VMCALL numeric leaf has non-zero bits above the accepted width.");

        if (rs1Value == 0 || rs1Value != ownerPolicy.NumericLeaf)
            return Deny(VirtualizationOperandCaptureDecision.InvalidLeafValue, "VMCALL numeric leaf is zero, adjacent or otherwise not the O1 exact leaf.");

        if (e1.DomainTag == 0)
            return Deny(VirtualizationOperandCaptureDecision.MissingDomainIdentity, "Operand capture requires a non-zero E1 domain tag.");

        if (e1.SourceSlotId != 7 || e1.WorkingSlotId != 7 || carrier.Placement.PinnedLaneId != 7)
            return Deny(VirtualizationOperandCaptureDecision.InvalidSlotIdentity, "Operand capture requires the canonical system-singleton source and working slot.");

        if (restoreGeneration == 0)
            return Deny(VirtualizationOperandCaptureDecision.MissingRestoreGeneration, "Operand capture requires a non-zero restore generation.");

        if (!string.Equals(ownerPolicy.OperationNamespace, VirtualizationDecisionValidatorV2.ExpectedOperationNamespace, StringComparison.Ordinal) ||
            !string.Equals(ownerPolicy.OperationId, VirtualizationDecisionValidatorV2.ExpectedOperationId, StringComparison.Ordinal) ||
            ownerPolicy.NumericLeaf != 1 ||
            !string.Equals(ownerPolicy.PolicyDigest, Phase38VirtualizationOperationOwnerSnapshotRegistry.ExactSnapshot.PolicyDigest, StringComparison.Ordinal))
        {
            return Deny(VirtualizationOperandCaptureDecision.OwnerPolicyMismatch, "Operand capture requires the exact loaded O1 policy.");
        }

        if (_captured.TryGetValue(e1, out _))
            return Deny(VirtualizationOperandCaptureDecision.DuplicateAttempt, "An E1 attempt can materialize operands only once.");

        ulong captureSequence = AllocateCaptureSequence();
        VirtualizationOperandSnapshot snapshot = VirtualizationOperandSnapshot.CreateValidated(
            e1,
            carrier.Rs1,
            rs1Value,
            restoreGeneration,
            captureSequence,
            ownerPolicy);
        _captured.Add(e1, snapshot);
        return new(VirtualizationOperandCaptureDecision.Captured, snapshot, "Canonical Rs1 value captured once after E1 validation.");
    }

    internal static VirtualizationOperandValidationResult ValidateForE2Input(
        VirtualizationOperandSnapshot? snapshot,
        VmxMicroOp? carrier,
        SafetyVerifier.VirtualizationAdmissionCertificate? e1,
        VirtualizationOperationOwnerSnapshot? ownerPolicy,
        ulong currentRestoreGeneration)
    {
        if (snapshot is null || carrier is null || e1 is null || ownerPolicy is null)
            return Invalid(VirtualizationOperandValidationDecision.MissingInput, "Operand validation requires snapshot, carrier, E1 and O1.");

        if (!ReferenceEquals(carrier.VirtualizationAdmission, e1) ||
            !ReferenceEquals(carrier.VirtualizationOperandSnapshot, snapshot))
        {
            return Invalid(VirtualizationOperandValidationDecision.CarrierOrE1Mismatch, "Operand snapshot is not attached to this exact E1 carrier.");
        }

        if (!string.Equals(snapshot.OwnerPolicyDigest, ownerPolicy.PolicyDigest, StringComparison.Ordinal) ||
            !string.Equals(ownerPolicy.PolicyDigest, Phase38VirtualizationOperationOwnerSnapshotRegistry.ExactSnapshot.PolicyDigest, StringComparison.Ordinal))
        {
            return Invalid(VirtualizationOperandValidationDecision.OwnerPolicyMismatch, "Operand snapshot O1 identity is stale or foreign.");
        }

        if (currentRestoreGeneration == 0 || snapshot.RestoreGeneration != currentRestoreGeneration)
            return Invalid(VirtualizationOperandValidationDecision.RestoreGenerationMismatch, "Operand snapshot was invalidated by restore generation change.");

        if (snapshot.AttemptId != e1.AttemptId ||
            snapshot.E1IssuerGeneration != e1.IssuerGeneration ||
            snapshot.VirtualThreadId != e1.VirtualThreadId ||
            snapshot.OwnerContextId != e1.OwnerContextId ||
            snapshot.DomainTag == 0 ||
            snapshot.DomainTag != e1.DomainTag ||
            snapshot.Rs1Selector != carrier.Rs1 ||
            snapshot.Rs1Value != ownerPolicy.NumericLeaf ||
            snapshot.Rs2Selector != 0 ||
            snapshot.Rs2Value != 0 ||
            snapshot.RdSelector != 0 ||
            carrier.Rs2 != 0 ||
            carrier.Rd != 0 ||
            snapshot.SourceSlotId != e1.SourceSlotId ||
            snapshot.WorkingSlotId != e1.WorkingSlotId ||
            snapshot.BundleIdentity != e1.BundleIdentity ||
            snapshot.ReplayEpoch != e1.ReplayEpoch ||
            snapshot.CarrierIdentityDigest != e1.CarrierIdentityDigest ||
            snapshot.CaptureSequence == 0)
        {
            return Invalid(VirtualizationOperandValidationDecision.IdentityMismatch, "Operand snapshot identity no longer matches E1, O1 or the canonical carrier.");
        }

        string digest = VirtualizationOperandSnapshotDigest.Compute(
            snapshot.AttemptId,
            snapshot.E1IssuerGeneration,
            snapshot.VirtualThreadId,
            snapshot.OwnerContextId,
            snapshot.DomainTag,
            snapshot.Rs1Selector,
            snapshot.Rs1Value,
            snapshot.Rs2Selector,
            snapshot.Rs2Value,
            snapshot.RdSelector,
            snapshot.SourceSlotId,
            snapshot.WorkingSlotId,
            snapshot.BundleIdentity,
            snapshot.ReplayEpoch,
            snapshot.CarrierIdentityDigest,
            snapshot.RestoreGeneration,
            snapshot.CaptureSequence,
            snapshot.OwnerPolicyDigest);
        if (!string.Equals(snapshot.OperandDigest, digest, StringComparison.Ordinal))
            return Invalid(VirtualizationOperandValidationDecision.DigestMismatch, "Operand snapshot canonical digest mismatch.");

        return new(
            VirtualizationOperandValidationDecision.ValidForE2Input,
            "Operand snapshot is immutable, current and eligible only as future E2 input.");
    }

    private ulong AllocateCaptureSequence()
    {
        ulong sequence = _nextCaptureSequence++;
        if (sequence == 0)
            sequence = _nextCaptureSequence++;
        return sequence;
    }

    private static VirtualizationOperandCaptureResult Deny(
        VirtualizationOperandCaptureDecision decision,
        string reason) =>
        new(decision, null, reason);

    private static VirtualizationOperandValidationResult Invalid(
        VirtualizationOperandValidationDecision decision,
        string reason) =>
        new(decision, reason);
}

internal sealed class VirtualizationRestoreGenerationOwner
{
    private readonly object _gate = new();
    private ulong _currentGeneration = 1;

    internal ulong CurrentGeneration
    {
        get
        {
            lock (_gate)
                return _currentGeneration;
        }
    }

    internal void AdvanceAfterRestore()
    {
        lock (_gate)
        {
            unchecked
            {
                _currentGeneration++;
                if (_currentGeneration == 0)
                    _currentGeneration = 1;
            }
        }
    }
}
