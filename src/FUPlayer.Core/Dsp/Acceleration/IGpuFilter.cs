namespace FUPlayer.Core.Dsp.Acceleration;

/// <summary>
/// A filter uploaded to a device, and what checking it against the processor found. Both convolutions have one,
/// so the accelerator can prepare either without knowing which it has.
/// </summary>
internal interface IGpuFilter : IDisposable
{
    /// <summary>Largest disagreement with the processor during verification, relative to the loudest sample.</summary>
    double WorstDeviation { get; }

    /// <summary>
    /// Why the device's answer could not be compared with the processor's, or null when it was. A filter that
    /// cannot be checked is only used when the device has been forced, and then this says as much.
    /// </summary>
    /// <remarks>
    /// This is not a disagreement: a device that answers differently is never used, forced or not. It is the
    /// case where the comparison itself is impossible, on a filter so long that the part of it the check can
    /// reach produces nothing measurable. There the choice is between the user's setting and no acceleration.
    /// </remarks>
    string? Unchecked { get; }
}
