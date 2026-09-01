using Impeller.Core.Abstractions;
using Impeller.Core.Engine;
using Impeller.Core.Persistence;
using Impeller.EngineService;
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

// Injected rather than read from DateTimeOffset.UtcNow, so ticks, ramp limiting and every
// curve's response timing can be driven deterministically in tests.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton<AggregatingSensorRegistry>();
builder.Services.AddSingleton<ISensorRegistry>(
    sp => sp.GetRequiredService<AggregatingSensorRegistry>());
builder.Services.AddSingleton<ControlOwnershipRegistry>();
builder.Services.AddSingleton<ControlLoop>();

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<EngineOptions>>().Value;

    // No migrations yet: this is the first shipped schema. Each future schema change adds one
    // IConfigMigration to this list and bumps the current version.
    return new ConfigStore(ResolveConfigurationRoot(options), new MigrationRunner([], currentVersion: 0));
});

// Machine state, not user state: it records which synthetic id this PC assigned to which physical
// sensor. Deliberately not part of a configuration, so copying a config between machines carries
// the curves without carrying one machine's hardware identity.
builder.Services.AddSingleton<ISensorIdentityMap>(sp =>
{
    var options = sp.GetRequiredService<IOptions<EngineOptions>>().Value;
    return new JsonSensorIdentityMap(
        Path.Combine(ResolveConfigurationRoot(options), "sensor-identity.json"));
});

builder.Services.Configure<LhmOptions>(builder.Configuration.GetSection(LhmOptions.SectionName));
builder.Services.AddSingleton<ISensorProvider, LhmSensorProvider>();

builder.Services.AddHostedService<EngineWorker>();

var host = builder.Build();
await host.RunAsync();

return 0;

// Defaults to a folder beside the executable so a portable install keeps its state with it.
static string ResolveConfigurationRoot(EngineOptions options) =>
    options.ConfigurationPath ?? Path.Combine(AppContext.BaseDirectory, "Configurations");
