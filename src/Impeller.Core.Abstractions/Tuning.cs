using Impeller.Core.Abstractions.Configuration;

namespace Impeller.Core.Abstractions;

/// <summary>Which measuring procedure a run is.</summary>
public enum TuningKind
{
    /// <summary>Measuring one fan's duty-to-speed table and its start and stop thresholds.</summary>
    Calibration = 0,

    /// <summary>Working out which tacho belongs to which control.</summary>
    Pairing,
}

/// <summary>Where a tuning run has got to, for a progress display to render.</summary>
/// <param name="Kind">Which procedure.</param>
/// <param name="Stage">What it is doing, in a few words.</param>
/// <param name="Detail">The specifics — which fan, at what duty, reading what.</param>
/// <param name="Completed">How many controls are done.</param>
/// <param name="Total">How many there are.</param>
/// <param name="Finished">Whether this is the last report.</param>
/// <remarks>
/// Sent once a second for several minutes, and the detail line is most of why anyone watches: a
/// procedure that takes this long without saying what it is doing is indistinguishable from one
/// that has hung.
/// </remarks>
public sealed record TuningProgress(
    TuningKind Kind,
    string Stage,
    string Detail,
    int Completed,
    int Total,
    bool Finished = false);

/// <summary>What a run worked out about one control.</summary>
/// <param name="ControlId">Which control.</param>
/// <param name="ControlName">What it is called, so a report reads without a second lookup.</param>
/// <param name="Succeeded">Whether anything usable came out.</param>
/// <param name="Message">What happened, in a sentence.</param>
public sealed record TuningOutcome(
    SensorId ControlId,
    string ControlName,
    bool Succeeded,
    string Message);

/// <summary>Everything a finished run has to say.</summary>
/// <param name="Kind">Which procedure ran.</param>
/// <param name="Outcomes">One entry per control it touched.</param>
/// <param name="Cancelled">Whether the user stopped it early.</param>
/// <param name="Saved">Whether the results were written into the configuration.</param>
/// <remarks>
/// <see paramref="Cancelled"/> and <see paramref="Saved"/> are independent. A run stopped after six
/// of eight fans still keeps and saves those six, because throwing away twenty minutes of
/// measurement over an early exit would be the worst possible answer to an impatient user.
/// </remarks>
public sealed record TuningReport(
    TuningKind Kind,
    EquatableArray<TuningOutcome> Outcomes,
    bool Cancelled = false,
    bool Saved = false);
