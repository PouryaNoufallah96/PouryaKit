using System.Text.Json;
using System.Text.Json.Serialization;

namespace PouryaKit.Domain;

/// <summary>
/// Writes every typed ID as its bare value and reads it back. <c>UsePouryaKitJson</c> adds it once to
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
        // Through JsonSerializer, not the value converter, so the options' number handling applies as to a raw value.
        public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Create(JsonSerializer.Deserialize<TValue>(ref reader, options)!);

        public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Value, options);

        public override TId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Create(ValueConverter(options).ReadAsPropertyName(ref reader, typeof(TValue), options));

        public override void WriteAsPropertyName(Utf8JsonWriter writer, TId value, JsonSerializerOptions options) =>
            ValueConverter(options).WriteAsPropertyName(writer, value.Value, options);

        private static JsonConverter<TValue> ValueConverter(JsonSerializerOptions options) =>
            (JsonConverter<TValue>)options.GetConverter(typeof(TValue));

        // An empty value is invalid input here, so it surfaces as JsonException, which ASP.NET Core turns into a 400.
        private static TId Create(TValue value)
        {
            try
            {
                return TypedId.From<TId, TValue>(value);
            }
            catch (ArgumentException exception)
            {
                throw new JsonException($"{typeof(TId).Name} cannot be empty.", exception);
            }
        }
    }
}
