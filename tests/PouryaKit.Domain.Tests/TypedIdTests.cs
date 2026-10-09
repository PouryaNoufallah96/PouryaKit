using System.Text.Encodings.Web;
using System.Text.Json;

namespace PouryaKit.Domain.Tests;

public class TypedIdTests
{
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions().UsePouryaKitJson();

    public static TheoryData<object, string> IdsAndTheirJson =>
        new()
        {
            { new ProductId(Guid.Parse("0199b2a4-6f1e-7c3a-9d2b-5e8f4a1c7b30")), "\"0199b2a4-6f1e-7c3a-9d2b-5e8f4a1c7b30\"" },
            { new InvoiceNumber(9_000_000_001), "9000000001" },
            { new LineNumber(7), "7" },
            { new Sku("LAMP-01"), "\"LAMP-01\"" },
        };

    [Fact]
    public void Ids_are_equal_only_when_type_and_value_are_equal()
    {
        var value = Guid.CreateVersion7();

        Assert.Equal(new ProductId(value), new ProductId(value));
        Assert.NotEqual(new ProductId(value), new ProductId(Guid.CreateVersion7()));
        Assert.NotEqual<object>(new ProductId(value), new OrderId(value));
    }

    [Fact]
    public void New_guid_ids_are_time_ordered()
    {
        var id = TypedId.New<ProductId>();

        Assert.Equal(7, id.Value.Version);
    }

    [Theory]
    [MemberData(nameof(IdsAndTheirJson))]
    public void Ids_round_trip_through_json_as_their_bare_value(object id, string json)
    {
        Assert.Equal(json, JsonSerializer.Serialize(id, id.GetType(), Json));
        Assert.Equal(id, JsonSerializer.Deserialize(json, id.GetType(), Json));
    }

    [Fact]
    public void A_numeric_id_follows_the_options_number_handling_like_its_raw_value()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web).UsePouryaKitJson();

        Assert.Equal(new InvoiceNumber(42), JsonSerializer.Deserialize<InvoiceNumber>("\"42\"", web));
    }

    [Fact]
    public void Ids_inside_an_object_are_written_as_their_bare_values_under_camel_case_names()
    {
        var order = new IdsOfEveryKind(
            new ProductId(Guid.Parse("0199b2a4-6f1e-7c3a-9d2b-5e8f4a1c7b30")),
            new InvoiceNumber(9_000_000_001),
            new LineNumber(7),
            new Sku("LAMP-01")
        );
        const string json =
            """{"product":"0199b2a4-6f1e-7c3a-9d2b-5e8f4a1c7b30","invoice":9000000001,"line":7,"sku":"LAMP-01"}""";

        Assert.Equal(json, JsonSerializer.Serialize(order, Json));
        Assert.Equal(order, JsonSerializer.Deserialize<IdsOfEveryKind>(json, Json));
    }

    [Fact]
    public void The_kits_json_options_write_the_same_text_from_default_web_or_snake_case_strict_options_and_when_applied_twice()
    {
        var order = new IdsOfEveryKind(TypedId.New<ProductId>(), new InvoiceNumber(42), new LineNumber(7), new Sku("Lampe <Café> & \"Ø\""));
        // Wolverine and ASP.NET Core each hand over an instance with framework defaults of their own; a third starts
        // from another naming policy and the strict default encoder.
        var fromDefaults = new JsonSerializerOptions().UsePouryaKitJson();
        var fromWebDefaults = new JsonSerializerOptions(JsonSerializerDefaults.Web).UsePouryaKitJson().UsePouryaKitJson();
        var fromSnakeCaseAndStrictEncoder = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Encoder = JavaScriptEncoder.Default,
        }.UsePouryaKitJson();

        Assert.Equal(JsonSerializer.Serialize(order, fromDefaults), JsonSerializer.Serialize(order, fromWebDefaults));
        Assert.Equal(JsonSerializer.Serialize(order, fromDefaults), JsonSerializer.Serialize(order, fromSnakeCaseAndStrictEncoder));
        Assert.Contains("\"sku\":\"Lampe <Café> & \\\"Ø\\\"\"", JsonSerializer.Serialize(order, fromDefaults), StringComparison.Ordinal);
        Assert.Single(fromWebDefaults.Converters, converter => converter.GetType().FullName == "PouryaKit.Domain.TypedIdJsonConverterFactory");
    }

    [Fact]
    public void Ids_round_trip_through_json_as_dictionary_keys()
    {
        var stock = new Dictionary<ProductId, int> { [TypedId.New<ProductId>()] = 3, [TypedId.New<ProductId>()] = 5 };

        var read = JsonSerializer.Deserialize<Dictionary<ProductId, int>>(JsonSerializer.Serialize(stock, Json), Json);

        Assert.Equal(stock, read);
    }

    [Fact]
    public void An_empty_id_in_json_is_a_json_error()
    {
        var error = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ProductId>($"\"{Guid.Empty}\"", Json)
        );

        Assert.IsType<ArgumentException>(error.InnerException);
    }

    [Fact]
    public void Empty_values_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ProductId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new Sku(" "));
        Assert.Throws<ArgumentException>(() => TypedId.From<ProductId, Guid>(Guid.Empty));
    }

    [Fact]
    public void Value_types_other_than_guid_long_int_and_string_are_rejected()
    {
        Assert.Throws<NotSupportedException>(() => new PriceId(1m));
    }

    [Theory]
    [InlineData(typeof(GenericId<>))]
    [InlineData(typeof(FixedValueId<>))]
    public void Open_generic_ids_are_not_concrete_ids(Type type)
    {
        Assert.Null(TypedId.GetValueType(type));
    }

    [Theory]
    [InlineData(typeof(GenericId<Guid>), typeof(Guid))]
    [InlineData(typeof(GenericId<long>), typeof(long))]
    [InlineData(typeof(FixedValueId<string>), typeof(Guid))]
    public void Closed_generic_ids_expose_their_value_type(Type type, Type valueType)
    {
        Assert.Equal(valueType, TypedId.GetValueType(type));
    }

    private sealed record IdsOfEveryKind(ProductId Product, InvoiceNumber Invoice, LineNumber Line, Sku Sku);

    private sealed record GenericId<TValue>(TValue Value) : TypedId<TValue>(Value)
        where TValue : notnull;

    private sealed record FixedValueId<TTag>(Guid Value) : TypedId<Guid>(Value);
}
