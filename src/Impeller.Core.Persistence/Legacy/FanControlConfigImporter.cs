using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;

namespace Impeller.Core.Persistence.Legacy;

/// <summary>
/// Reads a FanControl configuration and produces an Impeller one.
/// </summary>
/// <remarks>
/// <para>
/// This exists so nobody has to rebuild a setup they already have. It is the difference between an
/// app someone tries and an app someone switches to.
/// </para>
/// <para>
/// Two rules govern the whole thing. It never writes to the file it read — the original stays intact
/// and working, so an import that goes badly costs nothing. And it never guesses: anything that
/// cannot be carried across exactly is written into the report rather than approximated quietly,
/// because a fan curve that is subtly wrong is harder to notice than one that is obviously missing.
/// </para>
/// </remarks>
public sealed class FanControlConfigImporter(ISensorIdentityMap identityMap, ISensorRegistry? registry = null)
{
    private const string SectionName = "FanControl";

    private readonly ISensorIdentityMap _identityMap =
        identityMap ?? throw new ArgumentNullException(nameof(identityMap));

    /// <summary>
    /// What the machine currently has, used to repair graphics-card references.
    /// </summary>
    /// <remarks>
    /// Optional, and the import works without it — every motherboard reference resolves through the
    /// identity map alone. It is needed only for vendor identifiers, which name a card rather than a
    /// fingerprint and so can only be resolved by looking at what is here.
    /// </remarks>
    private readonly ISensorRegistry? _registry = registry;

    /// <summary>Whether a document looks like a configuration this importer can read.</summary>
    public static bool LooksLegacy(JsonObject? document) =>
        document is not null && document[SectionName] is JsonObject;

    /// <summary>Reads a configuration from a file on disk.</summary>
    /// <param name="path">The legacy file. It is opened for reading and never modified.</param>
    /// <param name="name">What to call the imported configuration.</param>
    /// <exception cref="LegacyImportException">The file is not readable as a configuration.</exception>
    public ImportResult ImportFile(string path, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        JsonObject document;

        try
        {
            using var stream = File.OpenRead(path);
            document = JsonNode.Parse(stream) as JsonObject
                ?? throw new LegacyImportException($"'{path}' does not contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new LegacyImportException($"'{path}' is not valid JSON: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new LegacyImportException($"'{path}' could not be read: {ex.Message}", ex);
        }

        return Import(document, name ?? Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Reads a configuration from an already-parsed document.</summary>
    /// <exception cref="LegacyImportException">The document has no configuration section.</exception>
    public ImportResult Import(JsonObject document, string name)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document[SectionName] is not JsonObject section)
        {
            throw new LegacyImportException(
                $"No '{SectionName}' section: this does not look like a FanControl configuration.");
        }

        var session = new Session(_identityMap, _registry);

        var sensors = session.ReadCustomSensors(Array(section, "CustomSensors"));
        var curves = session.ReadCurves(Array(section, "FanCurves"), Array(section, "Controls"));
        var controls = session.ReadControls(Array(section, "Controls"));

        return new ImportResult
        {
            Configuration = new ImpellerConfiguration
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Imported" : name,
                CustomSensors = [.. sensors],
                Curves = [.. curves],
                Controls = [.. controls],
            },
            Notes = [.. session.Notes],
            Names = session.Names,
        };
    }

    private static JsonArray Array(JsonObject section, string property) =>
        section[property] as JsonArray ?? [];

    /// <summary>
    /// The state one import needs: the notes, and the two lookup tables that let names and paths in
    /// the source become ids in the output.
    /// </summary>
    private sealed class Session(ISensorIdentityMap identityMap, ISensorRegistry? registry)
    {
        private readonly Dictionary<string, SensorId> _customSensorIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CurveId> _curveIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, EquatableArray<CalibrationPointDefinition>> _curveCalibration =
            new(StringComparer.Ordinal);

        public List<ImportNote> Notes { get; } = [];

        // ---------------------------------------------------------------- custom sensors

        /// <summary>
        /// Reads the derived sensors, minting a real id for each.
        /// </summary>
        /// <remarks>
        /// This is the one place where the import makes something strictly better than it was. In the
        /// original a custom sensor's identity is computed from its name, and the setter that would
        /// change it does nothing — so renaming one silently breaks every curve reading it. Here the
        /// id is minted once and the name is free to change afterwards. The old name-shaped
        /// identifier survives only as the lookup key used to rewrite references during this import.
        /// </remarks>
        public List<CustomSensorDefinition> ReadCustomSensors(JsonArray sensors)
        {
            var raw = new List<(JsonObject Node, CustomSensorDefinition Definition)>();

            foreach (var node in sensors.OfType<JsonObject>())
            {
                var identifier = Text(node, "Identifier");
                var nickname = Text(node, "NickName") ?? identifier ?? "Custom sensor";

                var shape = LegacyTypeResolver.ResolveSensor(node);
                if (shape is LegacySensorShape.Unknown or LegacySensorShape.Plain)
                {
                    // A plain entry is settings attached to a hardware sensor, not a sensor of its
                    // own. Nothing to import, and nothing wrong either.
                    continue;
                }

                var id = SensorId.New();
                if (!string.IsNullOrEmpty(identifier))
                {
                    _customSensorIds[identifier] = id;
                }

                raw.Add((node, new CustomSensorDefinition
                {
                    Id = id,
                    Name = nickname,
                    Kind = shape switch
                    {
                        LegacySensorShape.Mix => CustomSensorKind.Mix,
                        LegacySensorShape.TimeAverage => CustomSensorKind.TimeAverage,
                        LegacySensorShape.File => CustomSensorKind.File,
                        _ => CustomSensorKind.Offset,
                    },
                }));
            }

            // Sources resolve in a second pass so a custom sensor may read another one, whichever
            // order they happen to sit in the file.
            var result = new List<CustomSensorDefinition>(raw.Count);

            foreach (var (node, definition) in raw)
            {
                result.Add(definition.Kind switch
                {
                    CustomSensorKind.Mix => definition with
                    {
                        Function = MixFunctionForSensor(Integer(node, "SelectedMixFunction") ?? 0),
                        AllowMissingSource = Flag(node, "AllowMissingSensor") ?? false,
                        Sources = [.. ResolveSensorList(node, "SelectedSensors", definition.Name)],
                    },
                    CustomSensorKind.TimeAverage => definition with
                    {
                        Sources = [.. Single(ResolveTempSource(node, definition.Name))],
                        Window = TimeSpan.FromSeconds(Integer(node, "SelectedTime") ?? 10),
                    },
                    CustomSensorKind.File => definition with
                    {
                        Path = Text(node, "FileFullName"),
                    },
                    _ => definition with
                    {
                        Sources = [.. Single(ResolveTempSource(node, definition.Name))],
                        Offset = (float)(Number(node, "Offset") ?? 0d),
                        Proportional = Flag(node, "Proportional") ?? false,
                    },
                });

                Note(ImportOutcome.Imported, definition.Name, $"Imported as a {definition.Kind} sensor.");
            }

            return result;
        }

        // ---------------------------------------------------------------- curves

        /// <summary>Reads the curves, minting an id for each and resolving what they point at.</summary>
        /// <remarks>
        /// Curves are referenced by name in the source and by id in the output, so this happens in
        /// two passes: names are claimed first, then definitions are built once every name is known
        /// and a mix curve can point at a curve defined below it.
        /// </remarks>
        public List<CurveDefinition> ReadCurves(JsonArray curves, JsonArray controls)
        {
            var raw = new List<(JsonObject Node, LegacyCurveShape Shape, CurveId Id, string Name)>();

            foreach (var node in curves.OfType<JsonObject>())
            {
                var name = Text(node, "Name") ?? "Curve";
                var shape = LegacyTypeResolver.ResolveCurve(node);

                if (shape is LegacyCurveShape.Unknown or LegacyCurveShape.NameOnly)
                {
                    Note(ImportOutcome.Skipped, name, "Could not tell what kind of curve this is.");
                    continue;
                }

                var id = CurveId.New();
                var claimed = name;

                if (!_curveIds.TryAdd(name, id))
                {
                    // The original refuses the whole file on a duplicate name. Renaming the later one
                    // keeps both curves and costs the user a rename rather than a rebuild; references
                    // stay with the first, which is the one they already resolved to.
                    claimed = UniqueName(name);
                    _curveIds[claimed] = id;
                    Note(ImportOutcome.Adjusted, name, $"A second curve shared this name; renamed to '{claimed}'.");
                }

                raw.Add((node, shape, id, claimed));
            }

            IndexCalibrationByCurve(controls);

            var result = new List<CurveDefinition>(raw.Count);

            foreach (var (node, shape, id, name) in raw)
            {
                if (BuildCurve(node, shape, id, name) is { } definition)
                {
                    result.Add(definition);
                }
                else
                {
                    // Dropped by BuildCurve, which has already said why. Forget the name too, so a
                    // control referencing it is reported as unbound rather than pointed at nothing.
                    _curveIds.Remove(name);
                }
            }

            return result;
        }

        private CurveDefinition? BuildCurve(JsonObject node, LegacyCurveShape shape, CurveId id, string name)
        {
            var rpm = IsRpmMode(node);
            var calibration = rpm ? CalibrationFor(name) : default;

            if (rpm && calibration.Count == 0)
            {
                // The numbers in an RPM-mode curve are speeds, not percentages. Read as percentages
                // they would all saturate at full and the fan would simply run flat out, which looks
                // like a working import until someone notices the noise. Without a measured table
                // there is nothing to convert them with, so the curve does not come across.
                Note(
                    ImportOutcome.Skipped,
                    name,
                    "Targets RPM, and the fan it drives has no calibration data to convert those speeds into duties. "
                    + "Calibrate the fan in Impeller and rebuild this curve.");
                return null;
            }

            Duty ToDuty(double value) => rpm
                ? CalibrationTable.DutyForRpm(calibration, (float)value) ?? Duty.Off
                : new Duty((float)value);

            if (rpm)
            {
                Note(
                    ImportOutcome.Adjusted,
                    name,
                    "Targeted RPM; converted to duty using the calibration of the fan it drives.");
            }

            return shape switch
            {
                LegacyCurveShape.Flat => new FlatCurveDefinition
                {
                    Id = id,
                    Name = name,
                    Duty = ToDuty(Number(node, "Percent") ?? 0d),
                },

                LegacyCurveShape.Linear or LegacyCurveShape.LinearLegacy => new LinearCurveDefinition
                {
                    Id = id,
                    Name = name,
                    Source = ResolveTempSource(node, name),
                    MinimumInput = (float)(Number(node, "MinimumTemperature") ?? 0d),
                    MaximumInput = (float)(Number(node, "MaximumTemperature") ?? 100d),
                    MinimumDuty = ToDuty(Number(node, "MinimumFanSpeed") ?? 0d),
                    MaximumDuty = ToDuty(Number(node, "MaximumFanSpeed") ?? 100d),
                    Hysteresis = ReadHysteresis(node, shape == LegacyCurveShape.LinearLegacy, name),
                },

                LegacyCurveShape.Graph or LegacyCurveShape.GraphLegacy => new GraphCurveDefinition
                {
                    Id = id,
                    Name = name,
                    Source = ResolveTempSource(node, name),
                    Points = [.. ReadPoints(node, name, ToDuty)],
                    Hysteresis = ReadHysteresis(node, shape == LegacyCurveShape.GraphLegacy, name),
                },

                LegacyCurveShape.Trigger or LegacyCurveShape.TriggerLegacy => BuildTrigger(
                    node, shape, id, name, ToDuty),

                LegacyCurveShape.Mix => new MixCurveDefinition
                {
                    Id = id,
                    Name = name,
                    Function = MixFunctionForCurve(Integer(node, "SelectedMixFunction") ?? 0),
                    Sources = [.. ResolveCurveList(node, "SelectedFanCurves", name)],
                },

                LegacyCurveShape.MixLegacy => new MixCurveDefinition
                {
                    Id = id,
                    Name = name,
                    Function = MixFunctionForCurve(Integer(node, "SelectedMixFunction") ?? 0),
                    Sources =
                    [
                        .. Single(ResolveCurveReference(node["SelectedFanCurveA"], name)),
                        .. Single(ResolveCurveReference(node["SelectedFanCurveB"], name)),
                    ],
                },

                // Sync mirrors a control, not a curve. That distinction is why the engine grew
                // control-sourced sync before this importer was written: mirroring the curve behind a
                // control would miss that control's own limits and ramp rate, and the synced fan
                // would drift away from the one it is meant to match.
                LegacyCurveShape.Sync => new SyncCurveDefinition
                {
                    Id = id,
                    Name = name,
                    SourceKind = SyncSourceKind.Control,
                    SourceControl = ResolveSensorReference(node["SelectedControl"], name, "synced control"),
                    Offset = (float)(Number(node, "SelectedOffset") ?? 0d),
                    Proportional = Flag(node, "Proportional") ?? false,
                },

                LegacyCurveShape.Auto => new AutoCurveDefinition
                {
                    Id = id,
                    Name = name,
                    Source = ResolveTempSource(node, name),
                    IdleTemperature = (float)(Number(node, "IdleTemperature") ?? 35d),
                    LoadTemperature = (float)(Number(node, "LoadTemperature") ?? 70d),
                    MinimumDuty = ToDuty(Number(node, "MinFanSpeed") ?? 0d),
                    MaximumDuty = ToDuty(Number(node, "MaxFanSpeed") ?? 100d),
                    Step = (float)(Number(node, "Step") ?? 2d),
                    Deadband = (float)(Number(node, "Deadband") ?? 3d),
                    ResponseTime = TimeSpan.FromSeconds(Integer(node, "SelectedResponseTime") ?? 2),
                },

                // The older Auto shape names the same two limits after idle and load rather than
                // minimum and maximum.
                LegacyCurveShape.AutoLegacy => new AutoCurveDefinition
                {
                    Id = id,
                    Name = name,
                    Source = ResolveTempSource(node, name),
                    IdleTemperature = (float)(Number(node, "IdleTemperature") ?? 35d),
                    LoadTemperature = (float)(Number(node, "LoadTemperature") ?? 70d),
                    MinimumDuty = ToDuty(Number(node, "IdleFanSpeed") ?? 0d),
                    MaximumDuty = ToDuty(Number(node, "LoadFanSpeed") ?? 100d),
                    Step = (float)(Number(node, "Step") ?? 2d),
                    Deadband = (float)(Number(node, "Deadband") ?? 3d),
                    ResponseTime = TimeSpan.FromSeconds(Integer(node, "SelectedResponseTime") ?? 2),
                },

                _ => null,
            };
        }

        private TriggerCurveDefinition BuildTrigger(
            JsonObject node,
            LegacyCurveShape shape,
            CurveId id,
            string name,
            Func<double, Duty> toDuty)
        {
            TimeSpan up, down;

            if (shape == LegacyCurveShape.TriggerLegacy)
            {
                up = down = TimeSpan.FromSeconds(Integer(node, "SelectedResponseTime") ?? 0);
            }
            else
            {
                var response = node["ResponseTimeConfig"] as JsonObject;
                up = TimeSpan.FromSeconds(Integer(response, "ResponseTimeUp") ?? 0);
                down = TimeSpan.FromSeconds(Integer(response, "ResponseTimeDown") ?? 0);
            }

            return new TriggerCurveDefinition
            {
                Id = id,
                Name = name,
                Source = ResolveTempSource(node, name),
                IdleInput = (float)(Number(node, "IdleTemperature") ?? 0d),
                LoadInput = (float)(Number(node, "LoadTemperature") ?? 100d),
                IdleDuty = toDuty(Number(node, "IdleFanSpeed") ?? 0d),
                LoadDuty = toDuty(Number(node, "LoadFanSpeed") ?? 100d),
                ResponseUp = up,
                ResponseDown = down,
            };
        }

        // ---------------------------------------------------------------- controls

        /// <summary>Reads the controls and how each is driven.</summary>
        public List<ControlBindingDefinition> ReadControls(JsonArray controls)
        {
            var result = new List<ControlBindingDefinition>();

            foreach (var node in controls.OfType<JsonObject>())
            {
                var identifier = Text(node, "Identifier");
                var nickname = Text(node, "NickName") ?? identifier ?? "Control";

                var controlId = ResolveHardware(identifier, nickname, "control");
                RecordName(controlId, Text(node, "NickName"));
                if (controlId.IsNone)
                {
                    // Already reported by ResolveHardware. Keeping a binding that names no control
                    // would put a row in the config that can never do anything.
                    continue;
                }

                var curveName = Text(node["SelectedFanCurve"] as JsonObject, "Name");
                var curveId = CurveId.None;

                if (!string.IsNullOrEmpty(curveName) && !_curveIds.TryGetValue(curveName, out curveId))
                {
                    Note(
                        ImportOutcome.Unresolved,
                        nickname,
                        $"Was driven by '{curveName}', which did not come across. The fan is left unbound.");
                    curveId = CurveId.None;
                }

                var pinned = Flag(node, "ManualControl") == true
                    ? new Duty(Integer(node, "ManualControlValue") ?? 0)
                    : (Duty?)null;

                // A pinned fan counts as something to drive. Left disabled it would be ignored
                // entirely, which for a fan pinned at zero looks identical right up until the
                // firmware decides otherwise.
                var enabled = (Flag(node, "Enable") ?? false) && (!curveId.IsNone || pinned is not null);

                ReportUnmodelledControlSettings(node, nickname);

                result.Add(new ControlBindingDefinition
                {
                    ControlId = controlId,
                    CurveId = curveId,
                    Enabled = enabled,
                    MinimumDuty = new Duty(Integer(node, "MinimumPercent") ?? 0),
                    StartDuty = new Duty(Integer(node, "SelectedStart") ?? 0),
                    StopDuty = new Duty(Integer(node, "SelectedStop") ?? 0),

                    // The source stores these per update, not per second, and its own update period
                    // is one second by default — so on a default setup the numbers carry over as
                    // they stand. A user who had shortened that period gets a gentler ramp here.
                    MaximumStepUpPerSecond = (float)(Number(node, "SelectedCommandStepUp") ?? 100d),
                    MaximumStepDownPerSecond = (float)(Number(node, "SelectedCommandStepDown") ?? 100d),

                    PairedFanSensorId = ResolveSensorReference(
                        node["PairedFanSensor"], nickname, "paired fan sensor"),

                    // A fan pinned by hand stays pinned across the import, the same way it stays
                    // pinned across a restart.
                    ManualDuty = pinned,
                    Calibration = [.. ReadCalibration(node)],
                });

                Note(ImportOutcome.Imported, nickname, (pinned, enabled) switch
                {
                    ({ } duty, _) => $"Pinned by hand at {duty}.",
                    (_, true) => $"Driven by '{curveName}'.",
                    _ => "Imported, but left switched off.",
                });
            }

            return result;
        }

        /// <summary>
        /// Says out loud which per-control settings have no equivalent here.
        /// </summary>
        /// <remarks>
        /// Only when they were actually in use. A dropped setting that was at its default changes
        /// nothing and does not need saying; a dropped setting that was doing something does, because
        /// the fan will behave differently and the user deserves to know which knob went missing.
        /// </remarks>
        private void ReportUnmodelledControlSettings(JsonObject node, string nickname)
        {
            if (Number(node, "SelectedOffset") is { } offset && Math.Abs(offset) > 0.01d)
            {
                Note(
                    ImportOutcome.Adjusted,
                    nickname,
                    $"Had a {offset:0.#} point offset applied to its curve. Impeller has no per-control "
                    + "offset; adjust the curve itself if you need it back.");
            }

            if (Flag(node, "ForceApply") == true)
            {
                Note(
                    ImportOutcome.Adjusted,
                    nickname,
                    "Had force-apply switched on, which Impeller does not have as a setting.");
            }
        }

        private static List<CalibrationPointDefinition> ReadCalibration(JsonObject node)
        {
            var points = new List<CalibrationPointDefinition>();

            foreach (var entry in (node["Calibration"] as JsonArray ?? []).OfType<JsonNode>())
            {
                // Stored as a bare triple, and read as an object too because older files wrote the
                // tuple's own property names.
                if (entry is JsonArray triple && triple.Count >= 2)
                {
                    points.Add(new CalibrationPointDefinition(
                        new Duty(ToSingle(triple[0]) ?? 0f),
                        (int)(ToSingle(triple[1]) ?? 0f),
                        triple.Count > 2 && ToBoolean(triple[2]) == true));
                }
                else if (entry is JsonObject record)
                {
                    var duty = Number(record, "Command") ?? Number(record, "Item1");
                    var speed = Number(record, "Rpm") ?? Number(record, "Item2");

                    if (duty is not null && speed is not null)
                    {
                        points.Add(new CalibrationPointDefinition(
                            new Duty((float)duty.Value),
                            (int)speed.Value,
                            (Flag(record, "Avoid") ?? Flag(record, "Item3")) == true));
                    }
                }
            }

            return points;
        }

        /// <summary>
        /// Notes which fan's calibration table can translate each RPM-targeting curve.
        /// </summary>
        /// <remarks>
        /// A curve does not carry a table; the fan it drives does. So the only way to read a speed
        /// target as a duty is through whichever control uses the curve. Where several do, the first
        /// with real measurements wins — they are all the same fan model often enough, and a
        /// conversion from one real table beats refusing.
        /// </remarks>
        private void IndexCalibrationByCurve(JsonArray controls)
        {
            foreach (var node in controls.OfType<JsonObject>())
            {
                var curveName = Text(node["SelectedFanCurve"] as JsonObject, "Name");
                if (string.IsNullOrEmpty(curveName) || _curveCalibration.ContainsKey(curveName))
                {
                    continue;
                }

                var points = ReadCalibration(node);
                if (points.Count > 0)
                {
                    _curveCalibration[curveName] = [.. points];
                }
            }
        }

        private EquatableArray<CalibrationPointDefinition> CalibrationFor(string curveName) =>
            _curveCalibration.TryGetValue(curveName, out var table) ? table : [];

        // ---------------------------------------------------------------- resolution

        private SensorId ResolveTempSource(JsonObject node, string subject) =>
            ResolveSensorReference(node["SelectedTempSource"], subject, "sensor");

        private List<SensorId> ResolveSensorList(JsonObject node, string property, string subject)
        {
            var ids = new List<SensorId>();

            foreach (var entry in (node[property] as JsonArray ?? []).OfType<JsonNode>())
            {
                var id = ResolveSensorReference(entry, subject, "sensor");
                if (!id.IsNone)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        private List<CurveId> ResolveCurveList(JsonObject node, string property, string subject)
        {
            var ids = new List<CurveId>();

            foreach (var entry in (node[property] as JsonArray ?? []).OfType<JsonNode>())
            {
                var id = ResolveCurveReference(entry, subject);
                if (!id.IsNone)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        private CurveId ResolveCurveReference(JsonNode? reference, string subject)
        {
            var name = Text(reference as JsonObject, "Name");

            if (string.IsNullOrEmpty(name))
            {
                return CurveId.None;
            }

            if (_curveIds.TryGetValue(name, out var id))
            {
                return id;
            }

            Note(ImportOutcome.Unresolved, subject, $"Refers to a curve called '{name}', which is not in the file.");
            return CurveId.None;
        }

        private SensorId ResolveSensorReference(JsonNode? reference, string subject, string role)
        {
            var identifier = reference switch
            {
                JsonObject holder => Text(holder, "Identifier"),
                JsonValue value => value.GetValue<object>()?.ToString(),
                _ => null,
            };

            return string.IsNullOrEmpty(identifier)
                ? SensorId.None
                : ResolveHardware(identifier, subject, role);
        }

        /// <summary>
        /// Turns a stored identifier into an id, by whichever of the three routes applies.
        /// </summary>
        /// <remarks>
        /// A custom sensor is looked up in this import's own table, because its id was minted a
        /// moment ago and exists nowhere else. Hardware goes through the identity map, which only
        /// answers for hardware it has actually seen — deliberately, since minting an id here would
        /// produce a configuration pointing at a fan that does not exist and never will.
        /// </remarks>
        private SensorId ResolveHardware(string? identifier, string subject, string role)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                return SensorId.None;
            }

            if (_customSensorIds.TryGetValue(identifier, out var custom))
            {
                return custom;
            }

            foreach (var fingerprint in LegacyIdentifier.Candidates(identifier))
            {
                if (identityMap.TryGet(fingerprint, out var id))
                {
                    return id;
                }
            }

            if (ResolveVendor(identifier, subject, role) is { } aliased)
            {
                return aliased;
            }

            var pending = LegacyIdentifier.PendingBackend(identifier);

            Note(
                ImportOutcome.Unresolved,
                subject,
                pending is null
                    ? $"Its {role} '{identifier}' is not present on this machine. Pick a replacement."
                    : $"Its {role} was read through the {pending} backend, and nothing matching it is "
                      + "present here. Pick a replacement.");

            return SensorId.None;
        }

        /// <summary>The names the user gave things, where they are actually names.</summary>
        public Dictionary<SensorId, string> Names { get; } = [];

        /// <summary>
        /// Keeps a nickname, but only when it says something the hardware does not.
        /// </summary>
        /// <remarks>
        /// FanControl pre-fills a nickname with the provider's own name, so most of them carry no
        /// decision at all. Storing those would turn every fan into a "renamed" one and would mask
        /// a later firmware change behind a name nobody chose.
        /// </remarks>
        private void RecordName(SensorId id, string? nickname)
        {
            var wanted = nickname?.Trim();

            if (id.IsNone || string.IsNullOrEmpty(wanted) || registry is null)
            {
                return;
            }

            var sensor = registry.Controls.FirstOrDefault(control => control.Id == id) as ISensor
                ?? registry.Sensors.FirstOrDefault(entry => entry.Id == id);

            if (sensor is null || string.Equals(sensor.Name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Names[id] = wanted;
        }

        /// <summary>
        /// Repairs a graphics-card reference by matching the card's name against what is here.
        /// </summary>
        /// <remarks>
        /// The vendor backends this replaces name a card and number it their own way, so nothing in
        /// the stored identifier can be turned into a fingerprint arithmetically. Matching on the
        /// card's name is weaker than a fingerprint and is used exactly once, during an import, to
        /// turn a dead reference into a live one — after which the configuration holds a real id
        /// like everything else.
        /// </remarks>
        private SensorId? ResolveVendor(string identifier, string subject, string role)
        {
            if (registry is null || !VendorIdentifier.TryParse(identifier, out var reference))
            {
                return null;
            }

            if (VendorIdentifier.Resolve(reference, registry, out var ambiguous) is not { } sensor)
            {
                return null;
            }

            Note(
                ImportOutcome.Adjusted,
                subject,
                ambiguous
                    ? $"Its {role} was read through the {reference.Vendor} backend; matched to "
                      + $"'{sensor.Name}'. More than one card fitted that name — check it is the right one."
                    : $"Its {role} was read through the {reference.Vendor} backend; now read directly "
                      + $"from '{sensor.Name}'.");

            return sensor.Id;
        }

        private string UniqueName(string name)
        {
            for (var suffix = 2; ; suffix++)
            {
                var candidate = $"{name} ({suffix.ToString(CultureInfo.InvariantCulture)})";
                if (!_curveIds.ContainsKey(candidate))
                {
                    return candidate;
                }
            }
        }

        private void Note(ImportOutcome outcome, string subject, string message) =>
            Notes.Add(new ImportNote(outcome, subject, message));

        // ---------------------------------------------------------------- value readers

        /// <summary>
        /// Rebuilds the current hysteresis shape from either of the two ways it has been stored.
        /// </summary>
        /// <remarks>
        /// The older shape is a single value with one response time, plus a flag meaning "damp the
        /// fall but not the rise". The conversion below is the same one the source performs when it
        /// upgrades a file: the down side keeps the stored values, and the up side collapses to one
        /// when the flag is set.
        /// </remarks>
        private HysteresisDefinition ReadHysteresis(JsonObject node, bool legacy, string subject)
        {
            if (legacy)
            {
                var value = Number(node, "SelectedHysteresis") ?? 0d;
                var response = Integer(node, "SelectedResponseTime") ?? 0;
                var oneWay = Flag(node, "OneWayHysteresis") ?? false;

                return new HysteresisDefinition(
                    DeadbandUp: oneWay ? 1f : (float)value,
                    DeadbandDown: (float)value,
                    ResponseUp: TimeSpan.FromSeconds(oneWay ? 1 : response),
                    ResponseDown: TimeSpan.FromSeconds(response));
            }

            var config = node["HysteresisConfig"] as JsonObject;

            if (Flag(config, "IgnoreHysteresisAtLimits") == false)
            {
                Note(
                    ImportOutcome.Adjusted,
                    subject,
                    "Applied hysteresis at the ends of its range too. Impeller always releases it there.");
            }

            return new HysteresisDefinition(
                DeadbandUp: (float)(Number(config, "HysteresisValueUp") ?? 0d),
                DeadbandDown: (float)(Number(config, "HysteresisValueDown") ?? 0d),
                ResponseUp: TimeSpan.FromSeconds(Integer(config, "ResponseTimeUp") ?? 0),
                ResponseDown: TimeSpan.FromSeconds(Integer(config, "ResponseTimeDown") ?? 0));
        }

        /// <summary>
        /// Reads graph vertices, in each of the three ways a point can arrive.
        /// </summary>
        /// <remarks>
        /// The source stores a WPF point, which its serializer renders through a type converter as
        /// <c>"40,20"</c> — the same route that turns a colour into <c>"#FF2E5263"</c> elsewhere in
        /// the same file. The object and array spellings are accepted as well rather than betting the
        /// import on that one behaviour holding for every version that ever wrote a graph curve.
        /// </remarks>
        private List<CurvePointDefinition> ReadPoints(JsonObject node, string subject, Func<double, Duty> toDuty)
        {
            var points = new List<CurvePointDefinition>();

            foreach (var entry in (node["Points"] as JsonArray ?? []).OfType<JsonNode>())
            {
                double? x = null, y = null;

                switch (entry)
                {
                    case JsonValue value when value.TryGetValue<string>(out var text):
                        var parts = text.Split(',');
                        if (parts.Length == 2
                            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var px)
                            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py))
                        {
                            (x, y) = (px, py);
                        }

                        break;

                    case JsonObject pair:
                        (x, y) = (Number(pair, "X"), Number(pair, "Y"));
                        break;

                    case JsonArray tuple when tuple.Count >= 2:
                        (x, y) = (ToSingle(tuple[0]), ToSingle(tuple[1]));
                        break;
                }

                if (x is null || y is null)
                {
                    Note(ImportOutcome.Adjusted, subject, "A point on this curve could not be read and was dropped.");
                    continue;
                }

                points.Add(new CurvePointDefinition((float)x.Value, toDuty(y.Value)));
            }

            return points.OrderBy(point => point.Input).ToList();
        }

        private static bool IsRpmMode(JsonObject node) => node["CommandMode"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) =>
                string.Equals(text, "RPM", StringComparison.OrdinalIgnoreCase),
            { } node2 => ToNumber(node2) is 1d,
            _ => false,
        };

        // The two enums below share a property name and disagree about what its numbers mean. On a
        // mix curve, one is Sum; on a mix sensor, one is Max. Both arrive as bare integers under
        // "SelectedMixFunction", so nothing in the file distinguishes them and only the context does.
        // Separate tables, separate tests, and no shared helper that could be called from the wrong
        // side by accident.
        private static MixFunction MixFunctionForCurve(int stored) => stored switch
        {
            0 => MixFunction.Maximum,
            1 => MixFunction.Sum,
            2 => MixFunction.Average,
            3 => MixFunction.Minimum,
            4 => MixFunction.Difference,
            _ => MixFunction.Maximum,
        };

        private static MixFunction MixFunctionForSensor(int stored) => stored switch
        {
            0 => MixFunction.Average,
            1 => MixFunction.Maximum,
            2 => MixFunction.Minimum,
            3 => MixFunction.Sum,
            4 => MixFunction.Difference,
            _ => MixFunction.Average,
        };

        private static IEnumerable<SensorId> Single(SensorId id) => id.IsNone ? [] : [id];

        private static IEnumerable<CurveId> Single(CurveId id) => id.IsNone ? [] : [id];

        private static string? Text(JsonObject? node, string property) =>
            node?[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static double? Number(JsonObject? node, string property) => ToNumber(node?[property]);

        private static int? Integer(JsonObject? node, string property) =>
            Number(node, property) is { } number ? (int)Math.Round(number) : null;

        private static bool? Flag(JsonObject? node, string property) => ToBoolean(node?[property]);

        private static float? ToSingle(JsonNode? node) => (float?)ToNumber(node);

        /// <summary>
        /// Reads a number however it happens to be held.
        /// </summary>
        /// <remarks>
        /// A value parsed from a file is backed by a <see cref="System.Text.Json.JsonElement"/> and
        /// converts to whatever numeric type is asked for. A value built in memory — by a migration,
        /// or by a caller assembling a document — holds a boxed primitive and answers only to its own
        /// exact type, so asking for a double gets nothing from something stored as an int. Each is
        /// tried in turn rather than assuming the document came off disk.
        /// </remarks>
        private static double? ToNumber(JsonNode? node)
        {
            if (node is not JsonValue value)
            {
                return null;
            }

            if (value.TryGetValue<double>(out var real))
            {
                return real;
            }

            if (value.TryGetValue<int>(out var whole))
            {
                return whole;
            }

            if (value.TryGetValue<long>(out var wide))
            {
                return wide;
            }

            if (value.TryGetValue<float>(out var single))
            {
                return single;
            }

            if (value.TryGetValue<decimal>(out var exact))
            {
                return (double)exact;
            }

            return value.TryGetValue<string>(out var text)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static bool? ToBoolean(JsonNode? node)
        {
            if (node is not JsonValue value)
            {
                return null;
            }

            if (value.TryGetValue<bool>(out var flag))
            {
                return flag;
            }

            return value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed)
                ? parsed
                : null;
        }
    }
}
