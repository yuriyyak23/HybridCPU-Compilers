namespace HybridCPU.Compiler.Core.IR.Telemetry;

/// <summary>
/// Advisory eligibility snapshot exported from the runtime scheduler.
/// Compiler consumers may inspect it for diagnostics or future bounded heuristics,
/// but it is never an authority over runtime wait/wake state.
/// </summary>
public readonly record struct EligibilityTelemetrySnapshot(
    byte RequestedMask,
    byte NormalizedMask,
    byte ReadyPortMask,
    byte VisibleReadyMask,
    byte MaskedReadyMask);

public sealed partial class TelemetryProfileReader
{
    /// <summary>
    /// Whether explicit scheduler-eligibility telemetry is present in the loaded profile.
    /// Older profiles simply omit these fields.
    /// </summary>
    public bool HasEligibilityTelemetry =>
        _profile?.EligibilityMaskedCycles.HasValue == true ||
        _profile?.EligibilityMaskedReadyCandidates.HasValue == true ||
        _profile?.LastEligibilityRequestedMask.HasValue == true ||
        _profile?.LastEligibilityNormalizedMask.HasValue == true ||
        _profile?.LastEligibilityReadyPortMask.HasValue == true ||
        _profile?.LastEligibilityVisibleReadyMask.HasValue == true ||
        _profile?.LastEligibilityMaskedReadyMask.HasValue == true;

    /// <summary>
    /// Returns the raw count of scheduler cycles where ready SMT candidates were hidden by the eligibility gate.
    /// Returns 0 when the profile is absent or predates explicit eligibility export.
    /// </summary>
    public long GetEligibilityMaskedCycles()
    {
        return _profile?.EligibilityMaskedCycles ?? 0;
    }

    /// <summary>
    /// Returns the raw count of ready SMT candidates hidden by the eligibility gate.
    /// Returns 0 when the profile is absent or predates explicit eligibility export.
    /// </summary>
    public long GetEligibilityMaskedReadyCandidates()
    {
        return _profile?.EligibilityMaskedReadyCandidates ?? 0;
    }

    /// <summary>
    /// Returns the average number of hidden ready candidates among cycles where eligibility masking occurred.
    /// Returns 0.0 when no explicit eligibility telemetry is available.
    /// </summary>
    public double GetEligibilityMaskedReadyCandidatesPerMaskedCycle()
    {
        long maskedCycles = GetEligibilityMaskedCycles();
        return maskedCycles > 0
            ? (double)GetEligibilityMaskedReadyCandidates() / maskedCycles
            : 0.0;
    }

    /// <summary>
    /// Returns the last scheduler eligibility snapshot when present.
    /// Returns <c>false</c> for older profiles that do not export the explicit snapshot payload.
    /// </summary>
    public bool TryGetLastEligibilitySnapshot(out EligibilityTelemetrySnapshot snapshot)
    {
        if (_profile?.LastEligibilityRequestedMask is not byte requestedMask ||
            _profile.LastEligibilityNormalizedMask is not byte normalizedMask ||
            _profile.LastEligibilityReadyPortMask is not byte readyPortMask ||
            _profile.LastEligibilityVisibleReadyMask is not byte visibleReadyMask ||
            _profile.LastEligibilityMaskedReadyMask is not byte maskedReadyMask)
        {
            snapshot = default;
            return false;
        }

        snapshot = new EligibilityTelemetrySnapshot(
            requestedMask,
            normalizedMask,
            readyPortMask,
            visibleReadyMask,
            maskedReadyMask);
        return true;
    }
}
