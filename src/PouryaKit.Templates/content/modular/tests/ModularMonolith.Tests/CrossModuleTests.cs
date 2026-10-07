using System.Net;
using System.Net.Http.Json;
using Npgsql;

namespace ModularMonolith.Tests;

// What one Module's Operation causes in another, inside the one Host: Catalog publishes, Inventory receives.
public sealed class CrossModuleTests(App app)
{
    [Fact]
    public async Task A_product_catalog_creates_over_http_opens_its_stock_item_in_inventory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = app.ClientAs("ada");
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        await client.PostAsJsonAsync("/catalog/categories", new { categoryId = category, name = "Chairs" }, cancellationToken);

        var created = await App.PostAsync(
            client,
            "/catalog/products",
            new { productId = product, categoryId = category, name = "Armchair" },
            Guid.CreateVersion7().ToString(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        // Inventory receives ProductCreatedV1 after Catalog's commit, in an Operation and a schema of its own.
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var stockItems = new NpgsqlCommand("select count(*) from inventory.stock_items where product_id = $1", connection)
        {
            Parameters = { new() { Value = product } },
        };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        long opened;
        while ((opened = (long)(await stockItems.ExecuteScalarAsync(cancellationToken))!) == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken);
        }

        Assert.Equal(1L, opened);
    }
}
