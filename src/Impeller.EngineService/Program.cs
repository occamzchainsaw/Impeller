using Impeller.Core.Abstractions;
using Impeller.Core.Engine;
using Impeller.Core.Engine.Configuration;
using Impeller.Core.Engine.Sensors;
using Impeller.Core.Engine.Tuning;
using Impeller.Core.Persistence;
using Impeller.EngineService;
using Impeller.EngineService.Ipc;
using Impeller.Hardware.Lhm;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

// Management verbs short-circuit before any hosting is built: installing a service should not
// open hardware, and opening hardware needs privileges that "status" has no business requiring.
if (ServiceCommandLine.TryHandle(args) is { } exitCode)
{
    return exitCode;
}

var builder = Host.CreateApplicationBuilder(args);

// Lets one binary run as a console app while developing and as a Windows service in production,
// with no separate entry point.
builder.Services.AddWindowsService(options => options.ServiceName = "Impeller Engine");

builder.Services.Configure<EngineOptions>(
    builder.Configuration.GetSection(EngineOptions.SectionName));

// Resolved once, up front, rather than per-consumer: the probe writes a file, and two components
// disagreeing about where state lives is a class of bug worth making impossible.
var engineOptions = builder.Configuration
    .GetSection(EngineOptions.SectionName)
    .Get<EngineOptions>() ?? new EngineOptions();

var configurationRoot = StateLocation.Resolve(engineOptions.ConfigurationPath, out var portable);

var statePaths = new EngineStatePaths(configurationRoot, portable);
builder.Services.AddSingleton(statePaths);

// A file log, because the Event Log is the wrong place to read a tick loop from: it is awkward to
// scroll, awkward to attach to a bug report, and it drops anything below Information by default —
// which is exactly the detail that explains why a fan did something unexpected.
builder.Services.AddSerilog((_, configuration) => configuration
    .MinimumLevel.Is(LogEventLevel.Debug)

    // The transport is chatty at debug and none of it is ours.
    .MinimumLevel.Override("StreamJsonRpc", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.File(
        Path.Combine(statePaths.LogRoot, "engine-.log"),
        rollingInterval: RollingInterval.Day,

        // Capped in both directions. A machine left running for a year should not accumulate a
        // year of logs, and a provider failing every tick should not fill the disk in an
        // afternoon -- an engine that runs out of disk stops controlling fans.
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 32L * 1024 * 1024,
        rollOnFileSizeLimit: true,

        // Flushed on an interval rather than per line. A crash can cost the last second of log,
        // which is a fair trade against a synchronous disk write on every tick.
        buffered: true,
        flushToDiskInterval: TimeSpan.FromSeconds(2),
        outputTemplate:
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

// The Event Log stays, narrowed to what someone opens Event Viewer to find: whether the service
// started, whether it stopped, and anything that went wrong badly enough to matter. Everything
// else now has a better home, and leaving it here as well would bury those three.
builder.Logging.AddFilter<EventLogLoggerProvider>(null, LogLevel.Warning);
builder.Logging.AddFilter<EventLogLoggerProvider>(EngineLifecycle.Category, LogLevel.Information);

// Injected rather than read from DateTimeOffset.UtcNow, so ticks, ramp limiting and every
// curve's response timing can be driven deterministically in tests.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton<AggregatingSensorRegistry>();
builder.Services.AddSingleton<ISensorRegistry>(
    sp => sp.GetRequiredService<AggregatingSensorRegistry>());
builder.Services.AddSingleton<ControlOwnershipRegistry>();
builder.Services.AddSingleton<ControlLoop>();

// No migrations yet: this is the first shipped schema. Each future schema change adds one
// IConfigMigration to this list and bumps the current version.
builder.Services.AddSingleton(
    new ConfigStore(configurationRoot, new MigrationRunner([], currentVersion: 0)));

// Which configuration is in force, remembered across restarts. Without it the engine comes back on
// whatever the settings file names, and switching configuration silently undoes itself at the next
// reboot.
builder.Services.AddSingleton(new SelectedConfiguration(statePaths.SelectionPath));

// The one thing that turns a stored document into a configured tick loop. Everything about a
// configuration's life — reading, validating, materialising, saving — goes through it.
builder.Services.AddSingleton<ConfigurationCoordinator>();

// Machine state, not user state: it records which synthetic id this PC assigned to which physical
// sensor. Deliberately not part of a configuration, so copying a config between machines carries
// the curves without carrying one machine's hardware identity.
builder.Services.AddSingleton<ISensorIdentityMap>(
    _ => new JsonSensorIdentityMap(statePaths.IdentityMapPath));

builder.Services.Configure<LhmOptions>(builder.Configuration.GetSection(LhmOptions.SectionName));
builder.Services.AddSingleton<ISensorProvider, LhmSensorProvider>();

// The engine's own computed sensors, exposed through the same interface as the hardware backends
// so that nothing downstream has to know the difference. Registered last so it refreshes after
// the hardware it reads — the registry enforces that, but the order here says why.
builder.Services.AddSingleton(sp =>
{
    var provider = new CustomSensorProvider(sp.GetRequiredService<TimeProvider>())
    {
        Registry = sp.GetRequiredService<ISensorRegistry>(),
    };

    return provider;
});

builder.Services.AddSingleton<ISensorProvider>(sp => sp.GetRequiredService<CustomSensorProvider>());

// The seam between the engine and anything watching it, plus the two counters the RPC layer reads
// to answer "is this thing alive" without a reference to a hosted service.
builder.Services.AddSingleton<EngineNotifications>();
builder.Services.AddSingleton<EngineWorkerState>();
builder.Services.AddSingleton<EngineReadiness>();
builder.Services.AddSingleton<TuningCoordinator>();
builder.Services.AddSingleton<EngineRpcService>();

builder.Services.AddHostedService<EngineWorker>();

// Registered after the worker so the first client to connect finds a configured engine rather
// than one still enumerating hardware.
builder.Services.AddHostedService<EngineRpcHost>();

var host = builder.Build();

var lifecycle = host.Services
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger(EngineLifecycle.Category);

EngineLifecycle.Starting(lifecycle, statePaths.ConfigurationRoot, statePaths.LogRoot);

try
{
    await host.RunAsync();
}
catch (Exception ex)
{
    // The one thing worth putting in the Event Log at error level: the service did not merely
    // stop, it fell over, and whoever is looking has no log file to find yet because the failure
    // may well be why there is none.
    EngineLifecycle.Faulted(lifecycle, ex);
    throw;
}
finally
{
    EngineLifecycle.Stopped(lifecycle);
}

return 0;
