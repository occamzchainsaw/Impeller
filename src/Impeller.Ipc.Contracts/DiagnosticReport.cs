using System.Globalization;
using System.Text;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;

namespace Impeller.Ipc.Contracts;

/// <summary>How one hardware backend got on when it was brought up.</summary>
/// <param name="ProviderId">The backend's stable id.</param>
/// <param name="DisplayName">What to call it in the report.</param>
/// <param name="Succeeded">Whether it came up at all.</param>
/// <param name="SensorCount">How many sensors it enumerated.</param>
/// <param name="ControlCount">How many of those can be written.</param>
/// <param name="FailedGroups">
/// Hardware groups it could not read. A backend can succeed overall while a group fails, and which
/// group is usually the whole answer to "why is my GPU missing".
/// </param>
/// <param name="Error">The failure, when it did not come up.</param>
public sealed record ProviderDiagnostics(
    string ProviderId,
    string DisplayName,
    bool Succeeded,
    int SensorCount,
    int ControlCount,
    EquatableArray<string> FailedGroups,
    string? Error);

/// <summary>
/// Everything worth knowing about a running engine, in one piece.
/// </summary>
/// <remarks>
/// <para>
/// Built to be attached to a bug report. The test of this type is whether someone who was not
/// there can read it and work out what happened — which is why it carries the configuration that
/// was actually in force rather than the one on disk, and the log tail rather than a pointer to
/// where the log lives.
/// </para>
/// <para>
/// It is a snapshot taken on request, not a stream. Nothing subscribes to it and nothing polls it.
/// </para>
/// </remarks>
public sealed record DiagnosticReport
{
    /// <summary>When the report was taken.</summary>
    public DateTimeOffset Taken { get; init; }

    /// <summary>What the engine is and how it is doing.</summary>
    public EngineStatus Status { get; init; } = new("0.0.0", 0, default, false, string.Empty);

    /// <summary>The operating system the engine is running on.</summary>
    public string OperatingSystem { get; init; } = string.Empty;

    /// <summary>The .NET runtime it is running under.</summary>
    public string Runtime { get; init; } = string.Empty;

    /// <summary>The process architecture, since a 32-bit build cannot read 64-bit hardware.</summary>
    public string Architecture { get; init; } = string.Empty;

    /// <summary>Whether the engine is running as a Windows service or in the foreground.</summary>
    public bool RunningAsService { get; init; }

    /// <summary>The identity the engine process is running as.</summary>
    public string Identity { get; init; } = string.Empty;

    /// <summary>Where log files are being written.</summary>
    public string LogRoot { get; init; } = string.Empty;

    /// <summary>How each hardware backend got on.</summary>
    public EquatableArray<ProviderDiagnostics> Providers { get; init; } = [];

    /// <summary>Every sensor the engine can currently see, with its latest reading.</summary>
    public EquatableArray<SensorDescriptor> Sensors { get; init; } = [];

    /// <summary>
    /// Every plugin the engine has seen, with what it was granted and whether it is connected.
    /// </summary>
    /// <remarks>
    /// Without this the first bug report involving a plugin is unanswerable: "my fan went to full
    /// speed" has a completely different cause depending on whether something else was holding that
    /// fan at the time, and nothing else in this report would say so.
    /// </remarks>
    public EquatableArray<PluginSummary> Plugins { get; init; } = [];

    /// <summary>Every control, with what is driving it.</summary>
    public EquatableArray<ControlDescriptor> Controls { get; init; } = [];

    /// <summary>The name of the configuration in force.</summary>
    public string ConfigurationName { get; init; } = string.Empty;

    /// <summary>The configuration in force — the one the engine is running, not the one on disk.</summary>
    public ImpellerConfiguration Configuration { get; init; } = new();

    /// <summary>What validating that configuration turned up.</summary>
    public ConfigurationValidation Validation { get; init; } = ConfigurationValidation.Clean;

    /// <summary>The tail of the current log file.</summary>
    public EquatableArray<string> RecentLog { get; init; } = [];

    /// <summary>
    /// A fixed-width column, shortened from the left when the value does not fit.
    /// </summary>
    /// <remarks>
    /// From the left because hardware names are prefixed with the device and distinguished by the
    /// suffix: a column of "AMD Ryzen 7 9800X3D - Core #..." tells the reader nothing, where the
    /// tail tells them which core. Truncating at all matters because an over-long name otherwise
    /// pushes every later column out of alignment and the readings stop looking like a column of
    /// numbers.
    /// </remarks>
    private static string Column(string value, int width) =>
        value.Length <= width
            ? value.PadRight(width)
            : "…" + value[(value.Length - width + 1)..];

    /// <summary>
    /// Renders the report as plain text, for saving next to a bug report.
    /// </summary>
    /// <remarks>
    /// Rendered here rather than in the shell so the engine and any other front end produce the
    /// same document, and so a report can be written by a command-line invocation that has no UI
    /// at all.
    /// </remarks>
    public string Render()
    {
        var text = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;

        text.AppendLine("Impeller diagnostic report")
            .AppendLine("==========================")
            .Append("Taken            ").AppendLine(Taken.ToString("u", invariant))
            .Append("Engine version   ").AppendLine(Status.Version)
            .Append("Runtime          ").AppendLine(Runtime)
            .Append("Operating system ").AppendLine(OperatingSystem)
            .Append("Architecture     ").AppendLine(Architecture)
            .Append("Running as       ").AppendLine(RunningAsService ? $"service ({Identity})" : $"console ({Identity})")
            .Append("State folder     ").AppendLine(Status.ConfigurationRoot)
            .Append("Log folder       ").AppendLine(LogRoot)
            .AppendLine();

        text.AppendLine("Engine")
            .AppendLine("------")
            .Append("Ticks completed  ").AppendLine(Status.TickCount.ToString(invariant))
            .Append("Last tick        ").AppendLine(Status.LastTickCompleted.ToString("u", invariant))
            .Append("Failsafe engaged ").AppendLine(Status.FailsafeEngaged ? "yes" : "no")
            .Append("Configuration    ").AppendLine(ConfigurationName)
            .AppendLine();

        text.AppendLine("Hardware backends")
            .AppendLine("-----------------");

        foreach (var provider in Providers)
        {
            text.Append("  ").Append(provider.DisplayName)
                .Append(" [").Append(provider.ProviderId).Append(']')
                .Append(provider.Succeeded ? " ok" : " FAILED")
                .Append(": ").Append(provider.SensorCount.ToString(invariant)).Append(" sensor(s), ")
                .Append(provider.ControlCount.ToString(invariant)).AppendLine(" control(s)");

            if (provider.FailedGroups.Count > 0)
            {
                text.Append("      unavailable groups: ")
                    .AppendLine(string.Join(", ", provider.FailedGroups));
            }

            if (provider.Error is { } error)
            {
                text.Append("      error: ").AppendLine(error);
            }
        }

        text.AppendLine();

        if (Validation.Issues.Count > 0)
        {
            text.AppendLine("Configuration issues")
                .AppendLine("--------------------");

            foreach (var issue in Validation.Issues)
            {
                text.Append("  ").Append(issue.Severity.ToString().ToUpperInvariant())
                    .Append(" [").Append(issue.Code).Append("] ")
                    .AppendLine(issue.Message);
            }

            text.AppendLine();
        }

        text.AppendLine("Controls")
            .AppendLine("--------");

        foreach (var control in Controls)
        {
            text.Append("  ").Append(control.Name)
                .Append("  duty ").Append(control.CommandedDuty?.ToString() ?? "not driven")
                .Append("  owner ").Append(control.Owner.ToString())
                .Append("  ").AppendLine(control.HardwarePath);
        }

        text.AppendLine()
            .Append("Sensors (").Append(Sensors.Count.ToString(invariant)).AppendLine(")")
            .AppendLine("----------");

        foreach (var sensor in Sensors)
        {
            text.Append("  ").Append(Column(sensor.Name, 40))
                .Append(Column(sensor.Kind.ToString(), 12))
                .Append((sensor.Value?.ToString("0.#", invariant) ?? "-").PadLeft(8))
                .Append("  ").AppendLine(sensor.HardwarePath);
        }

        text.AppendLine()
            .AppendLine("Recent log")
            .AppendLine("----------");

        foreach (var line in RecentLog)
        {
            text.Append("  ").AppendLine(line);
        }

        return text.ToString();
    }
}
