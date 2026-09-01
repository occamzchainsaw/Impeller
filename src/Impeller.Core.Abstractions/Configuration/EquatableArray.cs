using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Impeller.Core.Abstractions.Configuration;

/// <summary>
/// An immutable sequence that compares by its contents.
/// </summary>
/// <remarks>
/// <para>
/// The configuration records exist to be compared: "is this edit actually different from what is
/// running", "did the configuration the shell just sent change anything". A record holding an
/// <see cref="IReadOnlyList{T}"/> answers that with reference equality, so two configurations with
/// identical contents compare as different and every save looks like a change. This fixes that at
/// the one place it can be fixed once.
/// </para>
/// <para>
/// Serializes as an ordinary JSON array, so the stored form is unaffected by any of this.
/// </para>
/// </remarks>
/// <typeparam name="T">The element type.</typeparam>
[CollectionBuilder(typeof(EquatableArray), nameof(EquatableArray.Create))]
[JsonConverter(typeof(EquatableArrayJsonConverterFactory))]
public readonly struct EquatableArray<T> : IReadOnlyList<T>, IEquatable<EquatableArray<T>>
{
    private readonly T[]? _items;

    /// <summary>Wraps an existing sequence. The contents are copied unless already an array.</summary>
    public EquatableArray(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items as T[] ?? [.. items];
    }

    private T[] Items => _items ?? [];

    /// <inheritdoc />
    public int Count => Items.Length;

    /// <inheritdoc />
    public T this[int index] => Items[index];

    /// <inheritdoc />
    public bool Equals(EquatableArray<T> other) => Items.SequenceEqual(other.Items);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = default(HashCode);

        foreach (var item in Items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => Items.GetEnumerator();

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

    /// <summary>Implicit so an array or the result of a LINQ query drops straight in.</summary>
    public static implicit operator EquatableArray<T>(T[] items) => new(items);
}

/// <summary>Construction helpers for <see cref="EquatableArray{T}"/>.</summary>
public static class EquatableArray
{
    /// <summary>
    /// Builds a sequence from a collection expression. Present so <c>[a, b]</c> and <c>[.. items]</c>
    /// work on these properties the way they do on any other collection.
    /// </summary>
    public static EquatableArray<T> Create<T>(ReadOnlySpan<T> items) => new(items.ToArray());
}

/// <summary>
/// Makes <see cref="EquatableArray{T}"/> serialize as a plain JSON array, whatever the element type.
/// </summary>
public sealed class EquatableArrayJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        return typeToConvert.IsGenericType
            && typeToConvert.GetGenericTypeDefinition() == typeof(EquatableArray<>);
    }

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        var elementType = typeToConvert.GetGenericArguments()[0];

        return (JsonConverter)Activator.CreateInstance(
            typeof(EquatableArrayJsonConverter<>).MakeGenericType(elementType))!;
    }
}

/// <summary>Reads and writes one element type's <see cref="EquatableArray{T}"/> as a JSON array.</summary>
/// <typeparam name="T">The element type.</typeparam>
internal sealed class EquatableArrayJsonConverter<T> : JsonConverter<EquatableArray<T>>
{
    public override EquatableArray<T> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        new(JsonSerializer.Deserialize<T[]>(ref reader, options) ?? []);

    public override void Write(Utf8JsonWriter writer, EquatableArray<T> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToArray(), options);
}
