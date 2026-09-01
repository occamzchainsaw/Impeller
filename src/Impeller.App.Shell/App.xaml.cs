using Impeller.App.ViewModels;
using Impeller.App.ViewModels.Engine;
using Impeller.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace Impeller.App.Shell;

/// <summary>
/// Application entry point and composition root.
/// </summary>
/// <remarks>
/// <para>
/// The container is exposed statically because WinUI constructs pages itself, through a
/// parameterless constructor, and offers no hook for injecting them. Pages therefore pull their
/// view model rather than receiving it. The alternative — a custom <c>IXamlType</c> provider —
/// buys textbook constructor injection at the cost of a large amount of fragile plumbing.
/// </para>
/// <para>
/// Everything below the view models is registered elsewhere: the shell talks to the engine over
/// IPC and deliberately holds no engine types of its own.
/// </para>
/// </remarks>
public partial class App : Application
{
    private Window? _window;

    /// <summary>The application's service provider.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
    }

    /// <summary>Resolves a required service. Shorthand for the pull-style page constructors.</summary>
    public static T GetService<T>()
        where T : notnull => Services.GetRequiredService<T>();

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // One connection for the whole app: the main window and the tray icon are two views of the
        // same engine, not two clients competing for it.
        services.AddSingleton(_ => new EngineConnection
        {
            // Lets the shell tell "the engine is not installed" apart from "it is installed and
            // not running". Only the first of those needs the user to do something, and a first
            // run that reports the wrong one sends people looking for a service that was never
            // there. Querying the service's status needs no elevation; installing it does.
            IsEngineInstalled = ServiceControlManager.IsInstalled,
        });

        // Transient: a page navigated away from is discarded along with its view model, so
        // returning to it starts from a clean state rather than one the user last left behind.
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<CurvesViewModel>();
        services.AddTransient<SensorsViewModel>();
        services.AddTransient<PluginsViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider();
    }

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Started before the window so the first page to open finds a connection already in
        // progress rather than one that begins when it happens to be looked at.
        GetService<EngineConnection>().Start();

        _window = new MainWindow();
        _window.Activate();
    }
}
