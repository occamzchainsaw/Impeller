using Impeller.Core.Abstractions;
using Impeller.Core.Engine;
using Impeller.Core.Persistence;
using Impeller.EngineService;
using Microsoft.Extensions.Options;

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
    var root = options.ConfigurationPath
        ?? Path.Combine(AppContext.BaseDirectory, "Configurations");

    // No migrations yet: this is the first shipped schema. Each future schema change adds one
    // IConfigMigration to this list and bumps the current version.
    return new ConfigStore(root, new MigrationRunner([], currentVersion: 0));
});

builder.Services.AddHostedService<EngineWorker>();

var host = builder.Build();
await host.RunAsync();
