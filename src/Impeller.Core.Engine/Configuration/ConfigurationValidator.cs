using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;

namespace Impeller.Core.Engine.Configuration;

/// <summary>
/// Checks a configuration before the engine is asked to run it.
/// </summary>
/// <remarks>
/// <para>
/// The line between an error and a warning is the whole design here, and it is drawn at "can the
/// engine still do its job". A dependency cycle is an error because there is no evaluation order
/// to run. A curve pointing at a sensor that is not present right now is only a warning, because
/// absent hardware is a normal state on this design: the id survives, the curve produces no value,
/// and the fan holds. Refusing to load the configuration would take fan control away from someone
/// because they unplugged a USB fan hub, which is a far worse outcome than a fan holding steady.
/// </para>
/// <para>
/// Every check is on the stored form, not on live curve objects, so the shell can validate an edit
/// before sending it and get exactly the answer the engine would have given.
/// </para>
/// </remarks>
public static class ConfigurationValidator
{
    /// <summary>
    /// Checks a configuration.
    /// </summary>
    /// <param name="configuration">The configuration to check.</param>
    /// <param name="registry">
    /// The live hardware, so references to absent sensors and controls can be reported. Pass
    /// <see langword="null"/> to check the document's internal consistency alone, which is what
    /// an editor validating an unsaved change wants.
    /// </param>
    public static ConfigurationValidation Validate(
        ImpellerConfiguration configuration,
        ISensorRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var issues = new List<ConfigurationIssue>();

        CheckDuplicates(configuration, issues);
        CheckCurves(configuration, registry, issues);
        CheckCustomSensors(configuration, registry, issues);
        CheckBindings(configuration, registry, issues);
        CheckOrdering(configuration, issues);

        return new ConfigurationValidation([.. issues]);
    }

    private static void CheckDuplicates(ImpellerConfiguration configuration, List<ConfigurationIssue> issues)
    {
        foreach (var duplicate in configuration.Curves
            .GroupBy(curve => curve.Id)
            .Where(group => group.Count() > 1))
        {
            issues.Add(new ConfigurationIssue(
                ConfigurationSeverity.Error,
                "duplicate-curve-id",
                $"Two or more curves share the id {duplicate.Key}: "
                + string.Join(", ", duplicate.Select(curve => $"'{curve.Name}'"))
                + ". Which one a control refers to would be arbitrary."));
        }

        foreach (var duplicate in configuration.Controls
            .GroupBy(binding => binding.ControlId)
            .Where(group => group.Count() > 1))
        {
            issues.Add(new ConfigurationIssue(
                ConfigurationSeverity.Error,
                "duplicate-control",
                $"Control {duplicate.Key} is configured {duplicate.Count()} times. "
                + "Only one of them could ever take effect."));
        }

        foreach (var duplicate in configuration.CustomSensors
            .GroupBy(sensor => sensor.Id)
            .Where(group => group.Count() > 1))
        {
            issues.Add(new ConfigurationIssue(
                ConfigurationSeverity.Error,
                "duplicate-custom-sensor",
                $"Two or more custom sensors share the id {duplicate.Key}."));
        }
    }

    private static void CheckCurves(
        ImpellerConfiguration configuration,
        ISensorRegistry? registry,
        List<ConfigurationIssue> issues)
    {
        var curveIds = configuration.Curves.Select(curve => curve.Id).ToHashSet();

        foreach (var curve in configuration.Curves)
        {
            switch (curve)
            {
                case GraphCurveDefinition { Points.Count: 0 }:
                    issues.Add(Warning(
                        "empty-graph",
                        $"Graph curve '{curve.Name}' has no points, so it commands nothing."));
                    break;

                case MixCurveDefinition { Sources.Count: 0 }:
                    issues.Add(Warning(
                        "empty-mix",
                        $"Mix curve '{curve.Name}' combines no curves."));
                    break;

                case MixCurveDefinition mix:
                    foreach (var missing in mix.Sources.Where(source => !curveIds.Contains(source)))
                    {
                        issues.Add(Warning(
                            "missing-mix-source",
                            $"Mix curve '{curve.Name}' refers to curve {missing}, which no longer exists."));
                    }

                    break;

                case SyncCurveDefinition { SourceKind: SyncSourceKind.None }:
                    issues.Add(Warning(
                        "sync-without-source",
                        $"Sync curve '{curve.Name}' has nothing selected to mirror."));
                    break;

                case SyncCurveDefinition { SourceKind: SyncSourceKind.Curve } sync
                    when !curveIds.Contains(sync.SourceCurve):
                    issues.Add(Warning(
                        "missing-sync-source",
                        $"Sync curve '{curve.Name}' mirrors curve {sync.SourceCurve}, which no longer exists."));
                    break;
            }

            foreach (var sensorId in SensorsRead(curve))
            {
                CheckSensorPresent(
                    sensorId,
                    configuration,
                    registry,
                    issues,
                    $"Curve '{curve.Name}'");
            }
        }
    }

    private static void CheckCustomSensors(
        ImpellerConfiguration configuration,
        ISensorRegistry? registry,
        List<ConfigurationIssue> issues)
    {
        foreach (var sensor in configuration.CustomSensors)
        {
            if (sensor.Kind != CustomSensorKind.File && sensor.Sources.Count == 0)
            {
                issues.Add(Warning(
                    "custom-sensor-without-source",
                    $"Custom sensor '{sensor.Name}' reads nothing."));
            }

            if (sensor.Kind == CustomSensorKind.File && string.IsNullOrWhiteSpace(sensor.Path))
            {
                issues.Add(Warning(
                    "file-sensor-without-path",
                    $"File sensor '{sensor.Name}' has no file to read."));
            }

            foreach (var source in sensor.Sources)
            {
                CheckSensorPresent(source, configuration, registry, issues, $"Custom sensor '{sensor.Name}'");
            }
        }

        CheckCustomSensorCycles(configuration, issues);
    }

    /// <summary>
    /// Reports custom sensors that read themselves, directly or through others.
    /// </summary>
    /// <remarks>
    /// An error rather than a warning: unlike a missing sensor, there is no defensible value to
    /// produce. The provider drops anything caught in a loop, so letting this through would leave
    /// curves silently reading nothing and no explanation anywhere.
    /// </remarks>
    private static void CheckCustomSensorCycles(
        ImpellerConfiguration configuration,
        List<ConfigurationIssue> issues)
    {
        var byId = new Dictionary<SensorId, CustomSensorDefinition>();

        foreach (var sensor in configuration.CustomSensors)
        {
            byId.TryAdd(sensor.Id, sensor);
        }

        var finished = new Dictionary<SensorId, bool>();
        var path = new List<SensorId>();
        var cyclic = new HashSet<SensorId>();

        foreach (var id in byId.Keys)
        {
            Walk(id, byId, finished, path, cyclic);
        }

        foreach (var id in cyclic)
        {
            issues.Add(new ConfigurationIssue(
                ConfigurationSeverity.Error,
                "custom-sensor-cycle",
                $"Custom sensor '{byId[id].Name}' ends up reading itself, so it has no value to "
                + "report."));
        }
    }

    private static void Walk(
        SensorId id,
        Dictionary<SensorId, CustomSensorDefinition> byId,
        Dictionary<SensorId, bool> finished,
        List<SensorId> path,
        HashSet<SensorId> cyclic)
    {
        if (finished.TryGetValue(id, out var done))
        {
            if (done)
            {
                return;
            }

            var from = path.IndexOf(id);

            for (var i = from < 0 ? 0 : from; i < path.Count; i++)
            {
                cyclic.Add(path[i]);
            }

            return;
        }

        if (!byId.TryGetValue(id, out var sensor))
        {
            return;
        }

        finished[id] = false;
        path.Add(id);

        foreach (var source in sensor.Sources)
        {
            Walk(source, byId, finished, path, cyclic);
        }

        path.RemoveAt(path.Count - 1);
        finished[id] = true;
    }

    private static void CheckBindings(
        ImpellerConfiguration configuration,
        ISensorRegistry? registry,
        List<ConfigurationIssue> issues)
    {
        var curveIds = configuration.Curves.Select(curve => curve.Id).ToHashSet();

        foreach (var binding in configuration.Controls)
        {
            var name = Describe(registry, binding.ControlId);

            // An enabled control with no curve is a finished state, not a half-made one. A pin is a
            // complete instruction on its own, and a control with neither pin nor curve now rests -
            // handed back to its own firmware, or driven to its failsafe duty. Neither needs
            // reporting, and this used to report both: on this machine it fired on three fans and
            // therefore on every single save. A warning that shows up on correct configurations is
            // one people learn to scroll past, which costs the warnings that matter.
            if (binding.Enabled && !binding.CurveId.IsNone && !curveIds.Contains(binding.CurveId))
            {
                issues.Add(Warning(
                    "missing-binding-curve",
                    $"{name} is driven by a curve that no longer exists."));
            }

            if (binding.MinimumDuty > binding.MaximumDuty)
            {
                issues.Add(new ConfigurationIssue(
                    ConfigurationSeverity.Error,
                    "inverted-duty-limits",
                    $"{name} has a minimum duty above its maximum, "
                    + "which leaves no duty it is allowed to command."));
            }

            if (registry is not null && registry.GetControl(binding.ControlId) is null)
            {
                issues.Add(Warning(
                    "absent-control",
                    $"{name} is not present on this machine right now. "
                    + "Its settings are kept in case it comes back."));
            }
        }
    }

    /// <summary>
    /// What to call a control in a message a person will read.
    /// </summary>
    /// <remarks>
    /// These strings reach the user now that the shell shows them as notifications rather than
    /// burying them in a bar nobody reads. "System Fan #3 is driven by a curve that no longer
    /// exists" is actionable; the same sentence with a GUID in it is not, and the id is in the
    /// diagnostic bundle for anyone who actually needs it.
    /// </remarks>
    private static string Describe(ISensorRegistry? registry, SensorId controlId) =>
        registry?.GetControl(controlId) is { } control && !string.IsNullOrWhiteSpace(control.Name)
            ? control.Name
            : $"Control {controlId}";

    private static void CheckOrdering(ImpellerConfiguration configuration, List<ConfigurationIssue> issues)
    {
        // Building the curves is the only way to ask the real ordering question, and a definition
        // this build cannot construct is itself an error worth reporting rather than throwing on.
        var curves = new List<IFanCurve>(configuration.Curves.Count);

        foreach (var definition in configuration.Curves)
        {
            try
            {
                curves.Add(CurveFactory.Create(definition));
            }
            catch (ArgumentException)
            {
                issues.Add(new ConfigurationIssue(
                    ConfigurationSeverity.Error,
                    "unknown-curve-type",
                    $"Curve '{definition.Name}' is of a kind this version does not understand."));
            }
        }

        var controlCurves = configuration.Controls
            .GroupBy(binding => binding.ControlId)
            .ToDictionary(group => group.Key, group => group.First().CurveId);

        var order = CurveGraph.Sort(curves, controlCurves);

        if (!order.IsValid)
        {
            var names = order.Cycle.Select(id =>
                configuration.Curves.FirstOrDefault(curve => curve.Id == id)?.Name ?? id.ToString());

            issues.Add(new ConfigurationIssue(
                ConfigurationSeverity.Error,
                "curve-cycle",
                "These curves depend on each other in a loop, so there is no order to evaluate "
                + "them in: " + string.Join(" -> ", names)));
        }
    }

    /// <summary>
    /// Reports a sensor reference that resolves to nothing. Custom sensors count as present: they
    /// are produced by the engine, so they exist as soon as the configuration defining them does.
    /// </summary>
    private static void CheckSensorPresent(
        SensorId sensorId,
        ImpellerConfiguration configuration,
        ISensorRegistry? registry,
        List<ConfigurationIssue> issues,
        string subject)
    {
        if (sensorId.IsNone)
        {
            issues.Add(Warning("no-sensor-selected", $"{subject} has no sensor selected."));
            return;
        }

        if (registry is null || configuration.CustomSensors.Any(sensor => sensor.Id == sensorId))
        {
            return;
        }

        if (registry.Sensors.All(sensor => sensor.Id != sensorId))
        {
            issues.Add(Warning(
                "absent-sensor",
                $"{subject} reads sensor {sensorId}, which is not present on this machine right "
                + "now. The curve will hold its fan steady until it returns."));
        }
    }

    private static IEnumerable<SensorId> SensorsRead(CurveDefinition curve) => curve switch
    {
        LinearCurveDefinition linear => [linear.Source],
        GraphCurveDefinition graph => [graph.Source],
        TriggerCurveDefinition trigger => [trigger.Source],
        AutoCurveDefinition auto => [auto.Source],
        _ => [],
    };

    private static ConfigurationIssue Warning(string code, string message) =>
        new(ConfigurationSeverity.Warning, code, message);
}
