using Impeller.App.ViewModels;
using Impeller.App.ViewModels.Shell;
using Impeller.Platform.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Impeller.App.Shell.Pages;

/// <summary>
/// Configurations, importing, measuring the fans, and the diagnostic bundle.
/// </summary>
/// <remarks>
/// The page nobody visits until something is wrong, which is the argument for everything on it
/// being one press.
/// </remarks>
public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();

        // Things the view models cannot do for themselves without a UI framework or a platform: a
        // file picker needs a window handle, the clipboard is a shell service, and autostart is a
        // registry value on a project that targets no operating system.
        ViewModel.PickLegacyFile = PickLegacyFileAsync;
        ViewModel.CopyToClipboard = Copy;
        ViewModel.ReadAutostart = () => StartupRegistration.IsEnabled(AutostartName);
        ViewModel.ReadStartMinimised = () =>
            StartupRegistration.HasArgument(AutostartName, ShellStartup.MinimisedSwitch);

        // The switch goes on the Run value's command line rather than into a file of the shell's
        // own, so it applies to the log-in launch and to nothing else. Double-clicking Impeller is
        // somebody asking for the window; they should get it.
        ViewModel.WriteAutostart = (enabled, minimised) => StartupRegistration.Set(
            AutostartName,
            enabled,
            minimised ? ShellStartup.MinimisedSwitch : null);

        Loaded += async (_, _) => await ViewModel.LoadAsync().ConfigureAwait(true);
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    /// <summary>
    /// What the Run value is called, and therefore what Task Manager's Startup tab shows.
    /// </summary>
    /// <remarks>
    /// The product name rather than the executable's, because this is a label a person reads in a
    /// list of programs they may not remember installing.
    /// </remarks>
    private const string AutostartName = "Impeller";

    /// <summary>The page's view model, pulled from the container because WinUI builds pages itself.</summary>
    public SettingsViewModel ViewModel { get; } = App.GetService<SettingsViewModel>();

    /// <summary>Whether there is anything to show, for a button that is dead until there is.</summary>
    public static bool HasText(string? text) => !string.IsNullOrWhiteSpace(text);

    /// <summary>The same question, as a visibility.</summary>
    public static Visibility HasTextVisibility(string? text) =>
        string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Inverts a flag, which x:Bind cannot do on its own.</summary>
    public static bool Not(bool value) => !value;

    /// <summary>Shows something only while a flag is set.</summary>
    public static Visibility WhenTrue(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Where the configurations are kept, said rather than merely shown.
    /// </summary>
    /// <remarks>
    /// A bare path under a row of buttons is a fact with no question attached to it. It is here
    /// because a portable install keeps its files beside the executable and an installed one does
    /// not, and the answer to "where did my configurations go" should not need reasoning about.
    /// </remarks>
    public static string SavedIn(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : $"Kept in {path}";

    /// <summary>Which configuration is in force, spelled out.</summary>
    public static string Running(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Nothing loaded yet." : $"Running '{name}'.";

    /// <summary>
    /// Asks for a FanControl configuration file.
    /// </summary>
    /// <remarks>
    /// An unpackaged app has no identity for the picker to hang off, so the window handle has to be
    /// supplied explicitly. Without it the dialog throws rather than appearing.
    /// </remarks>
    private static async Task<string?> PickLegacyFileAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };

        picker.FileTypeFilter.Add(".json");

        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private static void Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
