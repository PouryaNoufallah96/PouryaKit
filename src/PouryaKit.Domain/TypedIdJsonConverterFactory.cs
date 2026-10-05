using System.Text.Json;
using System.Text.Json.Serialization;

namespace PouryaKit.Domain;

/// <summary>
/// Writes every typed ID as its bare value and reads it back. Add it once to
/// <see cref="JsonSerializerOptions.Converters"/>; System.Text.Json does not apply a converter
/// attribute on a base type to derived types, so a per-type attribute is never the way.
/// </summary>
public sealed class TypedIdJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => TypedId.GetValueType(typeToConvert) is not null;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType =
            TypedId.GetValueType(typeToConvert)
            ?? throw new ArgumentException($"{typeToConvert.Name} is not a typed ID.", nameof(typeToConvert));
        return (JsonConverter)Activator.CreateInstance(typeof(Converter<,>).MakeGenericType(typeToConvert, valueType))!;
    }

    private sealed class Converter<TId, TValue> : JsonConverter<TId>
        where TId : TypedId<TValue>
        where TValue : notnull
    {
        public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            TypedId.From<TId, TValue>(JsonSerializer.Deserialize<TValue>(ref reader, options)!);

        public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Value, options);
    }
}
