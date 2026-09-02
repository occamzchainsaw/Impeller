using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Impeller.App.ViewModels.Engine;
using Impeller.Core.Abstractions;

namespace Impeller.App.ViewModels.Tuning;

/// <summary>
/// Watches and drives the two procedures that measure the machine.
/// </summary>
/// <remarks>
/// <para>
/// Both take minutes and take over every fan while they run, so the important thing this does is
/// keep saying what is happening. A dialog that shows a spinner for four minutes is
/// indistinguishable from one that has hung, and the user's only recourse is to kill the app
/// halfway through — leaving every fan at whatever duty the procedure last wrote.
/// </para>
/// <para>
/// Progress is followed from the engine's broadcast rather than from the call's own return, so a
/// window opened partway through a run still shows it, and so does a second window.
/// </para>
/// </remarks>
public sealed partial class TuningViewModel : ObservableObject, IDisposable
{
    private readonly EngineConnection _connection;

    private CancellationTokenSource? _cancellation;
    private bool _disposed;

    public TuningViewModel(EngineConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;
        _connection.TuningProgressed += OnProgress;
    }

    /// <summary>Whether a run is going.</summary>
    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>What it is doing, in a few words.</summary>
    [ObservableProperty]
    public partial string Stage { get; private set; } = string.Empty;

    /// <summary>The specifics: which fan, at what duty, reading what.</summary>
    [ObservableProperty]
    public partial string Detail { get; private set; } = string.Empty;

    /// <summary>How far along, from zero to one, for a progress bar.</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    /// <summary>A sentence describing the finished run, or null while none has finished.</summary>
    [ObservableProperty]
    public partial string? Summary { get; private set; }

    /// <summary>What the last run worked out, one line per control.</summary>
    public ObservableCollection<TuningOutcome> Outcomes { get; } = [];

    /// <summary>Measures each of these controls.</summary>
    [RelayCommand]
    public Task CalibrateAsync(IEnumerable<SensorId> controlIds) =>
        RunAsync(TuningKind.Calibration, controlIds);

    /// <summary>Works out which tacho belongs to which of these controls.</summary>
    [RelayCommand]
    public Task PairAsync(IEnumerable<SensorId> controlIds) =>
        RunAsync(TuningKind.Pairing, controlIds);

    /// <summary>
    /// Stops the run.
    /// </summary>
    /// <remarks>
    /// Both the local token and the engine's own cancel are used. The first covers the run this
    /// window started; the second covers one another window started, or one left behind by a window
    /// that crashed, which is the case where every fan is stuck at a baseline duty and someone
    /// needs a way out.
    /// </remarks>
    [RelayCommand]
    public async Task CancelAsync()
    {
        if (_cancellation is { } cancellation)
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
        }

        if (_connection.Engine is { } engine)
        {
            try
            {
                await engine.CancelTuningAsync().ConfigureAwait(true);
            }
            catch (Exception)
            {
                // The connection going is itself an end to the run.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.TuningProgressed -= OnProgress;
        _cancellation?.Dispose();
    }

    private async Task RunAsync(TuningKind kind, IEnumerable<SensorId> controlIds)
    {
        if (_connection.Engine is not { } engine || IsRunning)
        {
            return;
        }

        var targets = controlIds.ToArray();

        if (targets.Length == 0)
        {
            Summary = "Nothing was selected to measure.";
            return;
        }

        Outcomes.Clear();
        Summary = null;
        Progress = 0;
        IsRunning = true;

        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _cancellation, cancellation)?.Dispose();

        try
        {
            var report = kind == TuningKind.Calibration
                ? await engine.CalibrateAsync(targets, cancellation.Token).ConfigureAwait(true)
                : await engine.PairFansAsync(targets, cancellation.Token).ConfigureAwait(true);

            foreach (var outcome in report.Outcomes)
            {
                Outcomes.Add(outcome);
            }

            Summary = Describe(report);
        }
        catch (OperationCanceledException)
        {
            Summary = "Stopped. Anything measured before that was kept.";
        }
        catch (Exception ex)
        {
            Summary = ex.Message;
        }
        finally
        {
            IsRunning = false;
            Stage = string.Empty;
            Detail = string.Empty;
        }
    }

    private void OnProgress(object? sender, TuningProgress progress)
    {
        Stage = Readable(progress.Stage);
        Detail = progress.Detail;
        Progress = progress.Total == 0 ? 0 : (double)progress.Completed / progress.Total;

        if (progress.Finished)
        {
            Progress = 1;
        }
    }

    private static string Describe(TuningReport report)
    {
        var done = report.Outcomes.Count(outcome => outcome.Succeeded);
        var what = report.Kind == TuningKind.Calibration ? "measured" : "paired";

        var stopped = report.Cancelled ? " Stopped early; what had finished was kept." : string.Empty;
        var saved = report.Saved ? string.Empty : " Nothing was saved.";

        return $"{done} of {report.Outcomes.Count} {what}.{stopped}{saved}";
    }

    /// <summary>
    /// Turns a phase name into something a person would say.
    /// </summary>
    /// <remarks>
    /// The engine reports its own state names, which are the right words for a log and the wrong
    /// ones for a dialog someone watches for four minutes.
    /// </remarks>
    private static string Readable(string stage) => stage switch
    {
        "Settling" => "Waiting for the fans to settle",
        "SteppingDown" => "Stepping the fan down",
        "FindingStart" => "Finding what starts it again",
        "Testing" => "Testing one control",
        "Finished" => "Finished",
        _ => stage,
    };
}
