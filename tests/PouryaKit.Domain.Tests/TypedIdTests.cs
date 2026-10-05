using System.Text.Json;

namespace PouryaKit.Domain.Tests;

public class TypedIdTests
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new TypedIdJsonConverterFactory() } };

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
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new TypedIdJsonConverterFactory() } };

        Assert.Equal(new InvoiceNumber(42), JsonSerializer.Deserialize<InvoiceNumber>("\"42\"", web));
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
}
