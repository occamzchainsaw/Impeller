using System.Text.Json;
using System.Text.Json.Serialization;

namespace Impeller.Plugins.Abstractions;

/// <summary>
/// How a plugin refers to one sensor or control.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately opaque, and deliberately not the engine's own <c>SensorId</c>. Sharing that type
/// would mean sharing the assembly it lives in, and with it the whole configuration model — curve
/// definitions, calibration tables, provider interfaces — as the permanent public dependency
/// surface of every third-party plugin. A plugin that wants to drive a fan should not have to take
/// a version dependency on what a curve is.
/// </para>
/// <para>
/// It is wire-identical to the engine's id, because both serialise as a plain GUID string. The
/// translation on the host side is a cast over a <see cref="Guid"/>, not a lookup.
/// </para>
/// <para>
/// A plugin should treat the value as meaningless and store it verbatim. It is stable across
/// restarts, across re-enumeration, and across the hardware being renamed by its vendor — which is
/// exactly what a saved "which fan am I driving" setting needs, and what matching on a hardware
/// path never gave anyone.
/// </para>
/// </remarks>
[JsonConverter(typeof(SensorRefJsonConverter))]
public readonly record struct SensorRef
{
    /// <summary>A reference to nothing.</summary>
    public static SensorRef None => default;

    /// <summary>Wraps a value, typically one the plugin saved earlier.</summary>
    public SensorRef(Guid value) => Value = value;

    /// <summary>The underlying value.</summary>
    public Guid Value { get; }

    /// <summary>True when this refers to nothing.</summary>
    public bool IsNone => Value == Guid.Empty;

    /// <summary>Reads a reference back from its string form.</summary>
    public static bool TryParse(string? text, out SensorRef reference)
    {
        if (Guid.TryParse(text, out var value))
        {
            reference = new SensorRef(value);
            return true;
        }

        reference = None;
        return false;
    }

    /// <summary>The round-trip string form, which is what a plugin should persist.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// Writes a <see cref="SensorRef"/> as a plain GUID string.
/// </summary>
/// <remarks>
/// Without this it serialises as <c>{ "Value": "..." }</c>, and would stop being wire-identical to
/// the engine's own id — which is the one property that keeps the host's translation layer down to
/// a cast.
/// </remarks>
public sealed class SensorRefJsonConverter : JsonConverter<SensorRef>
{
    /// <inheritdoc />
    public override SensorRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetGuid());

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SensorRef value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value);
    }

    /// <inheritdoc />
    public override SensorRef ReadAsPropertyName(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        new(Guid.Parse(reader.GetString() ?? string.Empty));

    /// <inheritdoc />
    public override void WriteAsPropertyName(
        Utf8JsonWriter writer,
        SensorRef value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(value.Value.ToString("D"));
    }
}
