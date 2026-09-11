using Impeller.App.ViewModels;
using Impeller.App.ViewModels.Engine;
using Impeller.App.ViewModels.Notifications;
using Impeller.App.ViewModels.Shell;
using Impeller.App.ViewModels.Updates;
using Impeller.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT.Interop;

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
public partial class App : Application, IDisposable
{
    private Window? _window;
    private TrayIcon? _tray;
    private AdlxGpuPlugin? _adlx;

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 50,
            Level = LogLevel.Information,
            Message = "Impeller shell starting. Logs in {LogRoot}.")]
        public static partial void Starting(ILogger logger, string logRoot);

        [LoggerMessage(EventId = 51, Level = LogLevel.Critical, Message = "Unhandled exception in the shell.")]
        public static partial void Crashed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 52,
            Level = LogLevel.Information,
            Message = "Started in the notification area. The window stays hidden until it is asked for.")]
        public static partial void StartedHidden(ILogger logger);
    }

    /// <summary>The application's service provider.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// The main window's handle.
    /// </summary>
    /// <remarks>
    /// An unpackaged app has no identity for a file picker to hang off, so the handle has to be
    /// supplied to one explicitly. Without it the dialog throws rather than appearing.
    /// </remarks>
    public static IntPtr MainWindowHandle { get; private set; }

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

        // Registered first, so anything constructed below can take a logger.
        services.AddShellLogging();

        // One connection for the whole app: the main window and the tray icon are two views of the
        // same engine, not two clients competing for it.
        services.AddSingleton(sp => new EngineConnection
        {
            Logger = sp.GetRequiredService<ILogger<EngineConnection>>(),

            // Lets the shell tell "the engine is not installed" apart from "it is installed and
            // not running". Only the first of those needs the user to do something, and a first
            // run that reports the wrong one sends people looking for a service that was never
            // there. Querying the service's status needs no elevation; installing it does.
            IsEngineInstalled = ServiceControlManager.IsInstalled,
        });

        // One for the window, not one per page: the whole point is that a message outlives the
        // page that raised it, and a centre owned by a page would be discarded with it.
        services.AddSingleton<NotificationCenter>();

        // Shared between the startup check and the Settings page's button, so one HttpClient serves
        // both rather than each page building its own.
        services.AddSingleton(_ => UpdateWatch.Feed());
        services.AddSingleton(_ => new ShellSettingsStore(ShellState.SettingsFile));

        // Singleton for the same reason - it is the strip at the foot of the window, which does not
        // belong to any page and must not be rebuilt when one is navigated away from.
        services.AddSingleton(sp => new ShellStatusViewModel(
            sp.GetRequiredService<EngineConnection>(),
            sp.GetRequiredService<NotificationCenter>()));

        // The AMD GPU fan control this shell hands the engine over the plugin pipe — see
        // AdlxGpuPlugin's own remarks for why it lives here rather than in the engine service.
        services.AddSingleton<AdlxGpuPlugin>();

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
        var logger = GetService<ILoggerFactory>().CreateLogger<App>();
        Log.Starting(logger, ShellLogging.LogRoot);

        // Anything that escapes a handler would otherwise take the window down with nothing said.
        UnhandledException += (_, e) => Fatal(logger, e.Exception);

        // The XAML handler above sees only what reaches the dispatcher. A fault on a background
        // thread - a timer callback, a continuation off the transport - takes the process down
        // without passing through it at all.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception fault)
            {
                Fatal(logger, fault);
            }
        };

        // Ticks arrive on a transport thread and everything downstream of them is bound to XAML,
        // which throws rather than merely disliking being touched from elsewhere. Handed over
        // before the connection starts, so the very first tick is already marshalled.
        var dispatcher = new ShellDispatcher(DispatcherQueue.GetForCurrentThread());

        var connection = GetService<EngineConnection>();
        connection.Dispatcher = dispatcher;

        // A view model can post from a continuation on the transport thread, and the history is
        // bound to a list that throws if it is touched from anywhere else.
        GetService<NotificationCenter>().Dispatcher = dispatcher;

        // Started before the window so the first page to open finds a connection already in
        // progress rather than one that begins when it happens to be looked at.
        connection.Start();

        // Fire-and-forget: opening ADLX and declaring the GPU fan takes a moment and must never
        // hold up the window. A machine with no AMD GPU costs nothing beyond the attempt.
        _adlx = GetService<AdlxGpuPlugin>();
        _ = _adlx.StartAsync();

        // After the connection and before the window, but it waits twenty seconds before doing
        // anything, so it is behind both.
        UpdateWatch.Start(
            GetService<IReleaseFeed>(),
            GetService<ShellSettingsStore>(),
            GetService<NotificationCenter>(),
            typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            logger);

        try
        {
            _window = new MainWindow();
            MainWindowHandle = WindowNative.GetWindowHandle(_window);

            // The icon outlives the window: closing hides rather than exits, so there is still
            // something on screen saying the fans are being managed.
            _tray = new TrayIcon(_window, connection);

            // Reached only on a real exit: the tray handles an ordinary close by hiding the window
            // and cancelling it, so this runs once, when the user has actually chosen to quit.
            _window.Closed += (_, _) => Dispose();

            if (ShellStartup.StartsMinimised(Environment.GetCommandLineArgs()))
            {
                // Hidden, not merely left unactivated. Restoring a window that was maximised last
                // time calls Maximize on its presenter, and that shows it — so declining to
                // activate is not on its own enough to keep it off the screen.
                _window.AppWindow.Hide();
                Log.StartedHidden(logger);
            }
            else
            {
                _window.Activate();
            }
        }
        catch (Exception ex)
        {
            // Bringing the window up is the one stretch where a failure does not reliably reach
            // either handler above: a XAML fault here surfaces as a stowed WinRT exception and the
            // process is gone before anything has been written.
            Fatal(logger, ex);
            throw;
        }
    }

    /// <summary>
    /// Records a fault that is about to take the process with it.
    /// </summary>
    /// <remarks>
    /// Written straight to a file as well as logged, because the log is buffered and a process that
    /// is going down does not always get to flush it. A crash nobody can read anything about is one
    /// nobody can fix — and the shell has had exactly that twice, both times leaving nothing behind
    /// but a Windows Error Reporting entry naming a system DLL.
    /// </remarks>
    private static void Fatal(ILogger logger, Exception exception)
    {
        Log.Crashed(logger, exception);

        try
        {
            File.WriteAllText(
                Path.Combine(ShellLogging.LogRoot, "crash.log"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}");
        }
        catch (IOException)
        {
            // Nothing left to try. The log may still have caught it.
        }
        catch (UnauthorizedAccessException)
        {
        }

        ShellLogging.Close();
    }

    /// <summary>Releases the tray icon and flushes the log on the way out.</summary>
    public void Dispose()
    {
        _tray?.Dispose();
        _tray = null;
        _ = _adlx?.DisposeAsync().AsTask();
        _adlx = null;
        ShellLogging.Close();
        GC.SuppressFinalize(this);
    }
}
