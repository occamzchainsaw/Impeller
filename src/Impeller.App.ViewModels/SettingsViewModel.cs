using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Impeller.App.ViewModels.Engine;
using Impeller.App.ViewModels.Notifications;
using Impeller.App.ViewModels.Tuning;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Ipc.Contracts;

namespace Impeller.App.ViewModels;

/// <summary>
/// Configurations, importing, measuring the machine, and the diagnostic bundle.
/// </summary>
/// <remarks>
/// The page nobody visits until something is wrong, which is the argument for everything on it
/// being one click: switching configuration, bringing one across from FanControl, measuring the
/// fans, and producing the document that goes on a bug report.
/// </remarks>
public sealed partial class SettingsViewModel : EnginePageViewModel
{
    private bool _disposed;
    private bool _readingAutostart;

    public SettingsViewModel(EngineConnection connection, NotificationCenter notifications)
        : base(connection, notifications) => Tuning = new TuningViewModel(connection);

    /// <inheritdoc />
    public override string Title => "Settings";

    /// <summary>The two procedures that measure the fans.</summary>
    public TuningViewModel Tuning { get; }

    /// <summary>Every saved configuration.</summary>
    public ObservableCollection<string> Configurations { get; } = [];

    /// <summary>Everything the last import wanted the user to know.</summary>
    public ObservableCollection<ImportNote> ImportNotes { get; } = [];

    /// <summary>The one the engine is running.</summary>
    [ObservableProperty]
    public partial string CurrentConfiguration { get; private set; } = string.Empty;

    /// <summary>The one selected in the list.</summary>
    [ObservableProperty]
    public partial string? Selected { get; set; }

    /// <summary>What to call the configuration when saving a copy.</summary>
    [ObservableProperty]
    public partial string NewName { get; set; } = string.Empty;

    /// <summary>Where the engine keeps its state, so a bug report names the right folder.</summary>
    [ObservableProperty]
    public partial string ConfigurationRoot { get; private set; } = string.Empty;

    /// <summary>The diagnostic bundle as text, once it has been fetched.</summary>
    [ObservableProperty]
    public partial string? Diagnostics { get; private set; }

    /// <summary>Whether a request is in flight.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>
    /// Whether the window comes back when the user logs in.
    /// </summary>
    /// <remarks>
    /// About the window and its tray icon, and nothing else. The engine is a service and starts
    /// with the machine whether or not anybody logs in, which is the point of it being one — the
    /// fans on a machine sitting at the login screen still need managing. Switching this off costs
    /// nothing but the tray icon.
    /// </remarks>
    [ObservableProperty]
    public partial bool StartsWithWindows { get; set; }

    /// <summary>
    /// Whether that log-in start leaves the window in the notification area.
    /// </summary>
    /// <remarks>
    /// Only meaningful alongside <see cref="StartsWithWindows"/>, because it is stored as an
    /// argument on the same registry value — switch autostart off and there is nothing left to
    /// carry it. The page greys it out to say so, rather than offering a switch that quietly
    /// remembers nothing. Launching Impeller by hand is unaffected either way: that is somebody
    /// asking for the window, and they should get it.
    /// </remarks>
    [ObservableProperty]
    public partial bool StartsMinimised { get; set; }

    /// <summary>
    /// Asks the user for a FanControl configuration file.
    /// </summary>
    /// <remarks>
    /// Supplied by the shell, because a file picker needs a window handle and this project has no
    /// UI framework. There is deliberately no search of likely folders: FanControl keeps its
    /// configuration beside its own executable, wherever that was unzipped, and a list of places it
    /// might be would be a list of guesses.
    /// </remarks>
    public Func<Task<string?>>? PickLegacyFile { get; set; }

    /// <summary>Puts the diagnostic bundle on the clipboard. Supplied by the shell.</summary>
    public Action<string>? CopyToClipboard { get; set; }

    /// <summary>Reads whether the shell is registered to start with Windows. Supplied by the shell.</summary>
    /// <remarks>
    /// Delegates for the same reason the file picker is one: this project targets no platform, and
    /// the answer lives in the Windows registry. Absent, the section reports off and does nothing,
    /// which is the honest reading on a machine where it cannot be set.
    /// </remarks>
    public Func<bool>? ReadAutostart { get; set; }

    /// <summary>Reads whether that registration asks for the tray. Supplied by the shell.</summary>
    public Func<bool>? ReadStartMinimised { get; set; }

    /// <summary>Registers or unregisters it. Supplied by the shell.</summary>
    /// <remarks>
    /// Both facts at once, because both live in one registry value. A separate "write minimised"
    /// would have to read the value back to find out what the rest of it should say, and two halves
    /// of one string written from two places is exactly where a half-set state comes from.
    /// </remarks>
    public Func<bool, bool, bool>? WriteAutostart { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// The autostart flag is read here rather than in the constructor: the page wires its delegates
    /// after building the view model, so a constructor would ask before there was anything to ask.
    /// </remarks>
    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await base.LoadAsync(cancellationToken).ConfigureAwait(true);

        // Assigned through the flag, because the setter is what writes to the registry and reading
        // the current state must not count as changing it.
        _readingAutostart = true;
        StartsWithWindows = ReadAutostart?.Invoke() ?? false;
        StartsMinimised = ReadStartMinimised?.Invoke() ?? false;
        _readingAutostart = false;
    }

    /// <summary>Registers or unregisters the shell.</summary>
    partial void OnStartsWithWindowsChanged(bool value) =>
        WriteRegistration(value, StartsMinimised, () => StartsWithWindows = !value);

    /// <summary>
    /// Rewrites the registration to launch hidden, or not.
    /// </summary>
    /// <remarks>
    /// Silent while autostart is off, because there is no value to write the switch onto. The
    /// answer is kept in the property so that turning autostart on afterwards carries it, and the
    /// page has the switch greyed out meanwhile so nobody sets it expecting otherwise.
    /// </remarks>
    partial void OnStartsMinimisedChanged(bool value)
    {
        if (StartsWithWindows)
        {
            WriteRegistration(true, value, () => StartsMinimised = !value);
        }
    }

    /// <summary>
    /// Writes the log-in registration, and tells the truth if it could not.
    /// </summary>
    /// <remarks>
    /// A toggle that silently springs back is worse than one that is missing, so a refusal - a
    /// policy-managed registry, most likely - is announced and the switch is put back where it was
    /// rather than left showing a state that is not real.
    /// </remarks>
    private void WriteRegistration(bool enabled, bool minimised, Action revert)
    {
        if (_readingAutostart || WriteAutostart is not { } write)
        {
            return;
        }

        if (write(enabled, minimised))
        {
            return;
        }

        Notify.Error(
            "Windows would not save that.",
            "Impeller could not change how it starts with Windows. You can set it by hand in "
                + "Task Manager, on the Startup apps tab.");

        _readingAutostart = true;
        revert();
        _readingAutostart = false;
    }

    /// <inheritdoc />
    protected override void OnSnapshot(EngineSnapshot snapshot)
    {
        var previous = Selected;

        Configurations.Clear();

        foreach (var name in snapshot.AvailableConfigurations)
        {
            Configurations.Add(name);
        }

        CurrentConfiguration = snapshot.ConfigurationName;
        ConfigurationRoot = snapshot.Status.ConfigurationRoot;

        Selected = previous is not null && Configurations.Contains(previous)
            ? previous
            : snapshot.ConfigurationName;
    }

    /// <summary>Loads the selected configuration and applies it.</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        if (Selected is not { } name)
        {
            return;
        }

        await SendAsync(async engine =>
        {
            var result = await engine.LoadConfigurationAsync(name).ConfigureAwait(true);

            Report(
                result.Applied,
                result.Applied ? $"Now running '{result.Name}'." : $"'{name}' could not be applied.",
                Errors(result.Validation));
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Saves the running configuration under a new name and switches to it.
    /// </summary>
    /// <remarks>
    /// Applying the copy rather than only writing it is what makes this useful: the point of saving
    /// a copy is almost always to start editing it without touching the one that works.
    /// </remarks>
    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (string.IsNullOrWhiteSpace(NewName) || Snapshot is not { } snapshot)
        {
            return;
        }

        var name = NewName.Trim();

        await SendAsync(async engine =>
        {
            var result = await engine
                .ApplyConfigurationAsync(snapshot.Configuration with { Name = name })
                .ConfigureAwait(true);

            Report(
                result.Applied,
                result.Applied ? $"Saved and switched to '{name}'." : "Could not save.",
                Errors(result.Validation));

            if (result.Applied)
            {
                NewName = string.Empty;
            }
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Deletes the selected configuration.
    /// </summary>
    /// <remarks>
    /// Refused for the one in force. Deleting the running configuration would leave the engine
    /// driving fans from a file that no longer exists, which is a state with no good way back.
    /// </remarks>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } name)
        {
            return;
        }

        if (string.Equals(name, CurrentConfiguration, StringComparison.OrdinalIgnoreCase))
        {
            Report(false, "Switch to another configuration before deleting this one.");
            return;
        }

        await SendAsync(async engine =>
        {
            var deleted = await engine.DeleteConfigurationAsync(name).ConfigureAwait(true);
            Report(deleted, deleted ? $"Deleted '{name}'." : $"'{name}' was not there.");

            if (deleted)
            {
                await Connection.RefreshAsync().ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Brings a FanControl configuration across.
    /// </summary>
    /// <remarks>
    /// The result is saved, not applied. The notes are the reason for importing rather than
    /// rebuilding by hand, and they would be worth nothing shown after the fans had already changed
    /// behaviour.
    /// </remarks>
    [RelayCommand]
    private async Task ImportAsync()
    {
        if (PickLegacyFile is not { } pick)
        {
            return;
        }

        var path = await pick().ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await SendAsync(async engine =>
        {
            var summary = await engine.ImportConfigurationAsync(path).ConfigureAwait(true);

            ImportNotes.Clear();

            foreach (var note in summary.Notes)
            {
                ImportNotes.Add(note);
            }

            if (!summary.Succeeded)
            {
                Report(false, summary.Failure ?? "The file could not be read.");
                return;
            }

            var attention = summary.Notes.Count(note =>
                note.Outcome is ImportOutcome.Unresolved or ImportOutcome.Skipped);

            Report(
                true,
                $"Imported as '{summary.Name}'.",
                $"{summary.Curves} curves, {summary.Controls} controls, "
                + $"{summary.CustomSensors} computed sensors."
                + (attention == 0
                    ? " Load it when you are ready."
                    : $" {attention} thing(s) need a look before you load it."));

            await Connection.RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    /// <summary>Fetches the attachable diagnostic bundle.</summary>
    [RelayCommand]
    private async Task CollectDiagnosticsAsync() =>
        await SendAsync(async engine =>
        {
            var report = await engine.GetDiagnosticReportAsync().ConfigureAwait(true);
            Diagnostics = report.Render();
            Report(true, "Collected. Copy it into your bug report.");
        }).ConfigureAwait(true);

    /// <summary>Puts the bundle on the clipboard.</summary>
    [RelayCommand]
    private void CopyDiagnostics()
    {
        if (Diagnostics is { } text && CopyToClipboard is { } copy)
        {
            copy(text);
            Report(true, "Copied.");
        }
    }

    /// <summary>Measures every control the engine can see.</summary>
    [RelayCommand]
    private async Task CalibrateAllAsync()
    {
        if (Snapshot is { } snapshot)
        {
            await Tuning.CalibrateAsync(snapshot.Controls.Select(control => control.Id)).ConfigureAwait(true);
        }
    }

    /// <summary>Works out which tacho belongs to which control.</summary>
    [RelayCommand]
    private async Task PairAllAsync()
    {
        if (Snapshot is { } snapshot)
        {
            await Tuning.PairAsync(snapshot.Controls.Select(control => control.Id)).ConfigureAwait(true);
        }
    }

    /// <inheritdoc />
    protected override void OnConfigurationChanged(ConfigurationResult result) =>
        CurrentConfiguration = result.Name;

    private async Task SendAsync(Func<IEngineControl, Task> send)
    {
        if (Connection.Engine is not { } engine)
        {
            Report(false, "Not connected to the engine.");
            return;
        }

        IsBusy = true;

        try
        {
            await send(engine).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Report(false, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Report(bool succeeded, string headline, string? detail = null)
    {
        if (succeeded)
        {
            Notify.Success(headline, detail);
        }
        else
        {
            Notify.Error(headline, detail);
        }
    }

    /// <summary>
    /// The reasons behind a refusal, or null when there were none to give.
    /// </summary>
    /// <remarks>
    /// "Could not be applied" on its own sends someone to the log. The validator already knows
    /// exactly which curve points at what, and that is what the user needs to read.
    /// </remarks>
    private static string? Errors(ConfigurationValidation validation)
    {
        var issues = validation.Issues
            .Where(issue => issue.Severity == ConfigurationSeverity.Error)
            .Select(issue => issue.Message)
            .ToList();

        return issues.Count == 0 ? null : string.Join(" ", issues);
    }

    /// <inheritdoc />
    protected override void OnDisposing()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Tuning.Dispose();
    }
}
