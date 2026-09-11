using Impeller.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Impeller.App.Shell.Pages;

/// <summary>
/// Every program that has asked to drive this machine's fans.
/// </summary>
/// <remarks>
/// A page a user visits once per plugin and then forgets, which is exactly what it should be. Its
/// job is to keep the four verbs — approve, revoke a fan, switch off, forget — obviously different
/// from one another, because reaching for the wrong one is how somebody ends up with a plugin they
/// have forgotten they disabled and cannot work out why it never connects.
/// </remarks>
public sealed partial class PluginsPage : Page
{
    public PluginsPage()
    {
        InitializeComponent();

        Loaded += async (_, _) => await ViewModel.LoadAsync().ConfigureAwait(true);
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    /// <summary>The page's view model, pulled from the container because WinUI builds pages itself.</summary>
    public PluginsViewModel ViewModel { get; } = App.GetService<PluginsViewModel>();

    /// <summary>Whether the card has an id at all, which is what makes Apply meaningful.</summary>
    public static bool HasText(string? text) => !string.IsNullOrWhiteSpace(text);

    /// <summary>Whether Apply should be pressable: an id to apply to, and something for it to do.</summary>
    public static bool CanApply(bool canApply, string? id) => canApply && HasText(id);

    /// <summary>Shown when the condition holds.</summary>
    public static Visibility When(bool condition) =>
        condition ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shown when it does not.</summary>
    public static Visibility WhenNot(bool condition) =>
        condition ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The heading, which says whether anything is waiting on the user.</summary>
    public static string Waiting(int count) => count switch
    {
        0 => "Nothing is waiting for you.",
        1 => "One plugin is waiting for you.",
        _ => $"{count} plugins are waiting for you.",
    };

    /// <summary>The label on the on/off button, which names the outcome rather than the state.</summary>
    public static string EnableLabel(bool enabled) => enabled ? "Switch off" : "Switch on";

    /// <summary>Said in words as well as by a dot, because a dot alone is not readable.</summary>
    public static string ConnectedLabel(bool connected) => connected ? "Connected" : "Not running";

    /// <summary>A fan's tick box carries its obstacle as a tooltip when it has one.</summary>
    public static string FanTooltip(string? obstacle) => obstacle ?? "Let this plugin drive this fan.";
}
