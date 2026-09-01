using System.Text.Json;
using System.Text.Json.Nodes;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Core.Persistence;

namespace Impeller.Core.Engine.Configuration;

/// <summary>What changed, for anyone watching the current configuration.</summary>
/// <param name="Name">The configuration now in force.</param>
/// <param name="Configuration">Its contents.</param>
/// <param name="Validation">What checking it turned up, warnings included.</param>
public sealed record ConfigurationChanged(
    string Name,
    ImpellerConfiguration Configuration,
    ConfigurationValidation Validation);

/// <summary>
/// Owns which configuration is in force, and is the only thing that hands one to the tick loop.
/// </summary>
/// <remarks>
/// <para>
/// Everything about a configuration's life passes through here: reading it, checking it, turning
/// its definitions into live curves, giving those to the loop, and writing it back. Keeping that
/// in one place is what lets the shell edit a configuration over a wire and get exactly the same
/// validation and the same failure the engine would have applied locally.
/// </para>
/// <para>
/// Applying is all-or-nothing. A configuration with errors never reaches the loop, so a bad edit
/// leaves the fans running on the last good one rather than half-switching to a broken state.
/// </para>
/// </remarks>
public sealed class ConfigurationCoordinator(
    ConfigStore store,
    ControlLoop loop,
    ISensorRegistry registry)
{
    private readonly ConfigStore _store = store;
    private readonly ControlLoop _loop = loop;
    private readonly ISensorRegistry _registry = registry;

    /// <summary>The default configuration name, used when nothing else has been chosen.</summary>
    public const string DefaultName = "Default";

    /// <summary>Raised after a configuration has been applied to the loop.</summary>
    public event EventHandler<ConfigurationChanged>? Changed;

    /// <summary>The configuration currently driving the engine.</summary>
    public ImpellerConfiguration Current { get; private set; } = new();

    /// <summary>The name it was loaded from.</summary>
    public string CurrentName { get; private set; } = DefaultName;

    /// <summary>Whatever the last apply turned up, so the UI can show it without re-validating.</summary>
    public ConfigurationValidation LastValidation { get; private set; } = ConfigurationValidation.Clean;

    /// <summary>Every configuration available to load.</summary>
    public IReadOnlyList<ConfigEntry> List() => _store.List();

    /// <summary>Whether a configuration of this name exists.</summary>
    public bool Exists(string name) => _store.Exists(name);

    /// <summary>Deletes a stored configuration. Returns false if it was not there.</summary>
    public bool Delete(string name) => _store.Delete(name);

    /// <summary>
    /// Brings the engine up on whichever configuration should be running.
    /// </summary>
    /// <remarks>
    /// Falls back to creating one from the hardware actually present when the named configuration
    /// does not exist yet, which is the ordinary first-run path rather than an error. Every control
    /// in a generated configuration starts disabled: the engine has no business deciding how a
    /// stranger's fans should behave, and silence until asked is the only honest default.
    /// </remarks>
    public ConfigurationValidation Start(string? name = null)
    {
        var target = string.IsNullOrWhiteSpace(name) ? DefaultName : name;

        if (!_store.Exists(target))
        {
            var generated = Describe(target);
            return Apply(generated, save: true);
        }

        return Load(target);
    }

    /// <summary>
    /// Loads a stored configuration and applies it.
    /// </summary>
    /// <exception cref="FileNotFoundException">No configuration of that name exists.</exception>
    /// <exception cref="ConfigMigrationException">The stored document could not be read.</exception>
    public ConfigurationValidation Load(string name)
    {
        var (document, _) = _store.Load(name);

        ImpellerConfiguration configuration;

        try
        {
            configuration = document.Deserialize<ImpellerConfiguration>(ImpellerJson.Options)
                ?? throw new ConfigMigrationException($"'{name}' contains no configuration.");
        }
        catch (JsonException ex)
        {
            throw new ConfigMigrationException(
                $"'{name}' could not be read as a configuration: {ex.Message}", ex);
        }

        // The name on disk wins over whatever the document remembers itself as. Renaming a file is
        // a reasonable thing to do and should not leave the app calling it something else.
        return Apply(configuration with { Name = name }, save: false);
    }

    /// <summary>
    /// Checks a configuration and, if it is sound, hands it to the tick loop.
    /// </summary>
    /// <param name="configuration">The configuration to apply.</param>
    /// <param name="save">Whether to write it to disk once applied.</param>
    /// <returns>
    /// What validation found. When <see cref="ConfigurationValidation.HasErrors"/> is set nothing
    /// was applied and nothing was saved — the engine carries on with what it had.
    /// </returns>
    public ConfigurationValidation Apply(ImpellerConfiguration configuration, bool save = true)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var validation = ConfigurationValidator.Validate(configuration, _registry);

        if (validation.HasErrors)
        {
            return validation;
        }

        var curves = configuration.Curves.Select(CurveFactory.Create).ToList();
        var bindings = configuration.Controls.Select(CurveFactory.CreateBinding).ToList();

        _loop.Configure(curves, bindings);

        Current = configuration;
        CurrentName = configuration.Name;
        LastValidation = validation;

        if (save)
        {
            Save(configuration);
        }

        Changed?.Invoke(this, new ConfigurationChanged(CurrentName, Current, validation));
        return validation;
    }

    /// <summary>Writes a configuration to disk without applying it.</summary>
    public void Save(ImpellerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var node = JsonSerializer.SerializeToNode(configuration, ImpellerJson.Options)
            as JsonObject
            ?? throw new InvalidOperationException("A configuration did not serialize to an object.");

        _store.Save(configuration.Name, node);
    }

    /// <summary>
    /// Builds a configuration describing the hardware present, with nothing enabled.
    /// </summary>
    /// <remarks>
    /// Every discovered control gets an entry so the UI has something to show and the user has
    /// something to switch on, but none of them are driven and no curves exist. A first run should
    /// leave the machine exactly as it found it.
    /// </remarks>
    public ImpellerConfiguration Describe(string name) => new()
    {
        Name = name,
        Curves = [],
        Controls =
        [
            .. _registry.Controls.Select(control => new ControlBindingDefinition
            {
                ControlId = control.Id,
                Enabled = false,
            }),
        ],
        CustomSensors = [],
    };
}
