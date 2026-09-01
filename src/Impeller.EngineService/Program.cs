using Impeller.Core.Abstractions;
using Impeller.Core.Engine;
using Impeller.Core.Engine.Configuration;
using Impeller.Core.Engine.Sensors;
using Impeller.Core.Persistence;
using Impeller.EngineService;
using Impeller.EngineService.Ipc;
using Impeller.Hardware.Lhm;
using Microsoft.Extensions.Options;

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

builder.Services.AddSingleton(new EngineStatePaths(configurationRoot, portable));

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

// The one thing that turns a stored document into a configured tick loop. Everything about a
// configuration's life — reading, validating, materialising, saving — goes through it.
builder.Services.AddSingleton<ConfigurationCoordinator>();

// Machine state, not user state: it records which synthetic id this PC assigned to which physical
// sensor. Deliberately not part of a configuration, so copying a config between machines carries
// the curves without carrying one machine's hardware identity.
builder.Services.AddSingleton<ISensorIdentityMap>(
    _ => new JsonSensorIdentityMap(Path.Combine(configurationRoot, "sensor-identity.json")));

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
builder.Services.AddSingleton<EngineRpcService>();

builder.Services.AddHostedService<EngineWorker>();

// Registered after the worker so the first client to connect finds a configured engine rather
// than one still enumerating hardware.
builder.Services.AddHostedService<EngineRpcHost>();

var host = builder.Build();
await host.RunAsync();

return 0;
