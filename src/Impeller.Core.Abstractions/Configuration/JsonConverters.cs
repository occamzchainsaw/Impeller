using System.Text.Json;
using System.Text.Json.Serialization;

namespace Impeller.Core.Abstractions;

/// <summary>
/// Writes a <see cref="SensorId"/> as a plain GUID string.
/// </summary>
/// <remarks>
/// Without this the id serialises as <c>{ "Value": "..." }</c>, because it is a record struct
/// wrapping a single field. A configuration is read and hand-edited by people often enough that
/// the extra nesting is worth removing, and the same converter serves the engine-to-shell channel
/// so the two representations cannot drift apart.
/// </remarks>
public sealed class SensorIdJsonConverter : JsonConverter<SensorId>
{
    /// <inheritdoc />
    public override SensorId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        SensorId.TryParse(reader.GetString(), out var id) ? id : SensorId.None;

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SensorId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }

    /// <inheritdoc />
    public override SensorId ReadAsPropertyName(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        SensorId.TryParse(reader.GetString(), out var id) ? id : SensorId.None;

    /// <inheritdoc />
    public override void WriteAsPropertyName(
        Utf8JsonWriter writer,
        SensorId value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(value.ToString());
    }
}

/// <summary>Writes a <see cref="CurveId"/> as a plain GUID string.</summary>
public sealed class CurveIdJsonConverter : JsonConverter<CurveId>
{
    /// <inheritdoc />
    public override CurveId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        CurveId.TryParse(reader.GetString(), out var id) ? id : CurveId.None;

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, CurveId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }

    /// <inheritdoc />
    public override CurveId ReadAsPropertyName(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        CurveId.TryParse(reader.GetString(), out var id) ? id : CurveId.None;

    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, CurveId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(value.ToString());
    }
}

/// <summary>
/// Writes a <see cref="Duty"/> as a bare number of percent.
/// </summary>
/// <remarks>
/// Reading tolerates a quoted number as well, because hand-edited files and configurations
/// converted from other tools both produce them, and refusing to start over a pair of quotes
/// would be a poor trade.
/// </remarks>
public sealed class DutyJsonConverter : JsonConverter<Duty>
{
    /// <inheritdoc />
    public override Duty Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && float.TryParse(
                reader.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            return new Duty(parsed);
        }

        return new Duty(reader.GetSingle());
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Duty value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(value.Percent);
    }
}
