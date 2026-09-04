using CommunityToolkit.Mvvm.ComponentModel;

namespace Impeller.App.ViewModels.Notifications;

/// <summary>How much a notification wants to be noticed.</summary>
/// <remarks>
/// Four values rather than three because <em>something worked</em> is a distinct thing to say and
/// the reason this mechanism exists at all: approving a plugin used to complete in silence, which
/// is indistinguishable from it not having worked.
/// </remarks>
public enum NotificationSeverity
{
    /// <summary>Worth saying, nothing is wrong.</summary>
    Informational = 0,

    /// <summary>Something the user asked for happened.</summary>
    Success,

    /// <summary>It went through, but not the way they will have expected.</summary>
    Warning,

    /// <summary>It did not go through.</summary>
    Error,
}

/// <summary>
/// One thing that happened, worth telling the user once.
/// </summary>
/// <remarks>
/// Mutable rather than a record because a repeat updates the entry it repeats instead of adding
/// another: a save failing every second should read "failed to save ×37", not scroll thirty-seven
/// identical lines past the one message that was different.
/// </remarks>
public sealed partial class ShellNotification : ObservableObject
{
    public ShellNotification(
        NotificationSeverity severity,
        string title,
        string? message,
        DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        Severity = severity;
        Title = title;
        Message = message;
        At = at;
    }

    /// <summary>How much it wants to be noticed.</summary>
    public NotificationSeverity Severity { get; }

    /// <summary>The headline, always present.</summary>
    public string Title { get; }

    /// <summary>The detail, where there is any.</summary>
    public string? Message { get; }

    /// <summary>When it last happened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AtText))]
    public partial DateTimeOffset At { get; set; }

    /// <summary>How many times, counting the first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int Repeats { get; set; } = 1;

    /// <summary>The clock time it last happened, for the history list.</summary>
    public string AtText => At.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>A repeat count worth showing, or empty when it only happened once.</summary>
    public string CountText => Repeats > 1
        ? $"×{Repeats.ToString(System.Globalization.CultureInfo.CurrentCulture)}"
        : string.Empty;
}
