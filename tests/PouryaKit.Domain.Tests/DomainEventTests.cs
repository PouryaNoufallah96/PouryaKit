using System.Text.Json;

namespace PouryaKit.Domain.Tests;

public class DomainEventTests
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new TypedIdJsonConverterFactory() } };

    [Fact]
    public void A_domain_event_gets_a_time_ordered_identity_and_a_utc_time_once_when_raised()
    {
        var before = DateTimeOffset.UtcNow;
        var raised = new ProductRenamedDomainEvent(TypedId.New<ProductId>(), "Lamp");
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(7, raised.Id.Version);
        Assert.Equal(TimeSpan.Zero, raised.OccurredOn.Offset);
        Assert.InRange(raised.OccurredOn, before, after);
        Assert.NotEqual(raised.Id, new ProductRenamedDomainEvent(raised.ProductId, "Lamp").Id);
    }

    [Fact]
    public void A_domain_event_keeps_its_identity_and_time_through_json()
    {
        var raised = new ProductRenamedDomainEvent(TypedId.New<ProductId>(), "Lamp");

        var read = JsonSerializer.Deserialize<ProductRenamedDomainEvent>(JsonSerializer.Serialize(raised, Json), Json)!;

        Assert.Equal(raised.Id, read.Id);
        Assert.Equal(raised.OccurredOn, read.OccurredOn);
        Assert.Equal(raised, read);
    }

    [Fact]
    public void A_time_given_in_another_offset_is_kept_in_utc()
    {
        var local = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.FromHours(3.5));

        var read = JsonSerializer.Deserialize<ProductRenamedDomainEvent>(
            $$"""{"ProductId":"{{Guid.CreateVersion7()}}","NewName":"Lamp","Id":"{{Guid.CreateVersion7()}}","OccurredOn":"{{local:O}}"}""",
            Json
        )!;

        Assert.Equal(TimeSpan.Zero, read.OccurredOn.Offset);
        Assert.Equal(local, read.OccurredOn);
    }
}
