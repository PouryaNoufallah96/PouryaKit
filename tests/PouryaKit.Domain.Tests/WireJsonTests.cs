using System.Text.Json;

namespace PouryaKit.Domain.Tests;

// The strict variant Wolverine stores and delivers messages with (#60) also writes strictly: a null its type forbids
// fails where the message is serialized, so it never reaches a receiver that would refuse to read it.
public class WireJsonTests
{
    [Fact]
    public void The_wire_options_refuse_to_write_null_into_a_non_nullable_member()
    {
        var unnamed = new ProductCreatedDomainEvent(TypedId.New<ProductId>(), null!);

        var thrown = Assert.Throws<JsonException>(() => JsonSerializer.Serialize(unnamed, new JsonSerializerOptions().UsePouryaKitWireJson()));

        Assert.Contains($"'name' on type '{typeof(ProductCreatedDomainEvent).FullName}' doesn't allow getting null values", thrown.Message, StringComparison.Ordinal);
    }
}
