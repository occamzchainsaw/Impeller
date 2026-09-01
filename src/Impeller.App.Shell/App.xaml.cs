using Impeller.App.ViewModels;
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
        _window = new MainWindow();
        _window.Activate();
    }
}
