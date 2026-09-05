using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;

namespace Impeller.Core.Engine.Sensors;

/// <summary>
/// A sensor the engine computes from other sensors.
/// </summary>
/// <remarks>
/// <para>
/// Worth being precise about what this is not: a mix <em>curve</em> combines duties, this combines
/// readings. "The hotter of my CPU and GPU, fed to one curve" is this. "The higher of what these
/// two curves are asking for" is a mix curve. Both exist, both are useful, and confusing them
/// produces a configuration that looks right and behaves oddly.
/// </para>
/// <para>
/// Identity comes from the definition, which carries its own id. The app this replaces derives a
/// custom sensor's identifier from its name, so renaming one silently breaks every curve that
/// referenced it; here a name is only ever a label.
/// </para>
/// </remarks>
public sealed class CustomSensor : ISensor
{
    private readonly Queue<(DateTimeOffset At, float Value)> _window = [];

    private CustomSensorDefinition _definition;
    private string? _lastFilePath;

    /// <param name="definition">What this sensor derives, and from what.</param>
    public CustomSensor(CustomSensorDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        _definition = definition;
        Id = definition.Id;
        Fingerprint = new HardwareFingerprint(
            CustomSensorProvider.Id,
            definition.Id.ToString(),
            0,
            definition.Measures);
    }

    /// <inheritdoc />
    public SensorId Id { get; }

    /// <inheritdoc />
    public string Name => _definition.Name;

    /// <summary>
    /// What these belong to, where a measured sensor names its hardware.
    /// </summary>
    /// <remarks>
    /// Answered rather than left empty, because the shell heads a group with it. Left empty it fell
    /// back to the path, and the Sensors page showed a heading reading
    /// <c>custom/b9077721-e3a9-4ba8-acf3-b83209164176</c>.
    /// </remarks>
    public string HardwareName => CustomSensorProvider.Hardware;

    /// <inheritdoc />
    public SensorKind Kind => _definition.Measures;

    /// <inheritdoc />
    public float? Value { get; private set; }

    /// <inheritdoc />
    public HardwareFingerprint Fingerprint { get; }

    /// <summary>What this sensor derives, and from what.</summary>
    public CustomSensorDefinition Definition => _definition;

    /// <summary>Every sensor this one reads, so the provider can order its evaluation.</summary>
    public IReadOnlyList<SensorId> Sources => _definition.Sources;

    /// <summary>
    /// Recomputes from the current readings.
    /// </summary>
    /// <param name="read">
    /// Reads a source value. A delegate rather than the registry so the provider can resolve its
    /// own sensors first, which is what makes a chain of derived sensors correct within one pass
    /// regardless of when the registry last rebuilt its index.
    /// </param>
    /// <param name="now">The current time, for the averaging window.</param>
    public void Refresh(Func<SensorId, float?> read, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(read);

        Value = _definition.Kind switch
        {
            CustomSensorKind.Mix => Combine(read),
            CustomSensorKind.Offset => Shift(read),
            CustomSensorKind.TimeAverage => Average(read, now),
            CustomSensorKind.File => ReadFile(),
            _ => null,
        };
    }

    /// <summary>
    /// Replaces the definition, keeping the identity. Discards accumulated history, because an
    /// average over a window that has just changed length is an average of nothing in particular.
    /// </summary>
    public void Update(CustomSensorDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        _definition = definition;
        _window.Clear();
        Value = null;
    }

    private float? Combine(Func<SensorId, float?> read)
    {
        var values = new List<float>(_definition.Sources.Count);
        var missing = false;

        foreach (var source in _definition.Sources)
        {
            if (read(source) is { } value)
            {
                values.Add(value);
            }
            else
            {
                missing = true;
            }
        }

        // A "hottest of CPU and GPU" sensor that keeps reporting after the GPU stops answering is
        // reporting something other than what it claims to. Opt in to that if you want it.
        if (missing && !_definition.AllowMissingSource)
        {
            return null;
        }

        if (values.Count == 0)
        {
            return null;
        }

        return _definition.Function switch
        {
            MixFunction.Maximum => values.Max(),
            MixFunction.Minimum => values.Min(),
            MixFunction.Average => values.Average(),
            MixFunction.Sum => values.Sum(),
            MixFunction.Difference => values[0] - values.Skip(1).Sum(),
            _ => null,
        };
    }

    private float? Shift(Func<SensorId, float?> read)
    {
        if (FirstSource(read) is not { } value)
        {
            return null;
        }

        return _definition.Proportional
            ? value * (1f + (_definition.Offset / 100f))
            : value + _definition.Offset;
    }

    private float? Average(Func<SensorId, float?> read, DateTimeOffset now)
    {
        if (FirstSource(read) is { } value)
        {
            _window.Enqueue((now, value));
        }

        var cutoff = now - _definition.Window;

        while (_window.Count > 0 && _window.Peek().At < cutoff)
        {
            _window.Dequeue();
        }

        // Reports from the first sample rather than waiting for a full window. An average of two
        // seconds of data is a worse answer than an average of ten, but it is a far better one
        // than no reading at all, which would leave a fan holding for the whole window.
        return _window.Count == 0 ? null : _window.Average(sample => sample.Value);
    }

    private float? ReadFile()
    {
        var path = _definition.Path;

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(path).Trim();
            _lastFilePath = path;

            return float.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A file that is being rewritten, locked, or has gone away reads as no value, which the
            // engine already knows how to handle. Throwing here would fault the whole refresh pass
            // and take every other custom sensor down with it.
            return _lastFilePath == path ? Value : null;
        }
    }

    private float? FirstSource(Func<SensorId, float?> read) =>
        _definition.Sources.Count == 0 ? null : read(_definition.Sources[0]);
}
