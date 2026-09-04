using Impeller.App.ViewModels;
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

        // Two things the view models cannot do for themselves without a UI framework: a file picker
        // needs a window handle, and the clipboard is a shell service.
        ViewModel.PickLegacyFile = PickLegacyFileAsync;
        ViewModel.CopyToClipboard = Copy;

        Loaded += async (_, _) => await ViewModel.LoadAsync().ConfigureAwait(true);
        Unloaded += (_, _) => ViewModel.Dispose();
    }

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
