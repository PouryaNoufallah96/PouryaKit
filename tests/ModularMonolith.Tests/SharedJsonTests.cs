using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Catalog.Categories.ValueObjects;
using Catalog.Products.Events.DomainEvents;
using Catalog.Products.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Testing;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Util;

namespace ModularMonolith.Tests;

// One JSON representation for HTTP bodies and stored messages: the same typed IDs, read from a real response and from
// the envelope PostgreSQL holds for a delivered Notification.
public sealed class SharedJsonTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task A_typed_id_is_written_the_same_in_an_http_response_and_in_its_delivered_envelope()
    {
        using var client = Fixture.Client("ada");
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/catalog/categories", new { categoryId = category, name = "Lamps" }, TestContext.Current.CancellationToken)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Created,
            (
                await client.PostAsJsonAsync(
                    "/catalog/products",
                    new { productId = product, categoryId = category, name = "Desk lamp" },
                    TestContext.Current.CancellationToken
                )
            ).StatusCode
        );
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);

        // The list's ids are the typed IDs ProductId and CategoryId.
        using var listed = JsonDocument.Parse(
            await client.GetStringAsync("/catalog/products", TestContext.Current.CancellationToken)
        );
        var http = Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray());
        // Every subscription to ProductCreatedDomainEvent has an envelope of its own, written by the Host's serializer.
        var bodies = await Fixture.RowsAsync(
            $"select body from wolverine.wolverine_incoming_envelopes where message_type = '{typeof(ProductCreatedDomainEvent).ToMessageTypeName()}' and status = 'Handled'"
        );
        Assert.Equal(2, bodies.Count);
        var serializer = Fixture.Host.Services.GetRequiredService<IWolverineRuntime>().Options.DefaultSerializer;

        Assert.Equal($"\"{product}\"", http.GetProperty("id").GetRawText());
        Assert.Equal($"\"{category}\"", http.GetProperty("categoryId").GetRawText());
        foreach (var body in bodies)
        {
            var envelope = EnvelopeSerializer.Deserialize((byte[])body[0]);
            using var stored = JsonDocument.Parse(envelope.Data!);
            Assert.Equal(http.GetProperty("id").GetRawText(), stored.RootElement.GetProperty("productId").GetRawText());
            Assert.Equal(http.GetProperty("categoryId").GetRawText(), stored.RootElement.GetProperty("categoryId").GetRawText());
            // And it reads back as the typed IDs it was written from.
            var delivered = Assert.IsType<ProductCreatedDomainEvent>(serializer.ReadFromData(typeof(ProductCreatedDomainEvent), envelope));
            Assert.Equal(new ProductId(product), delivered.ProductId);
            Assert.Equal(new CategoryId(category), delivered.CategoryId);
        }
    }
}
