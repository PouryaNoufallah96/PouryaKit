using System.Net;
using System.Net.Http.Headers;
using Inventory.Host;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using PouryaKit.Cqrs;
using PouryaKit.Testing;
using PouryaKit.Web.OpenApi;
using static ModularMonolith.Tests.Fixtures.ProblemResponses;

namespace ModularMonolith.Tests;

// Inventory's GetStockItem over HTTP in Inventory.Host, the Proving Sample's service composition on the broker fixture:
// Inventory is not tenant-scoped, so it keeps the tenant of the delivery that opened each stock item, as Billing reads
// the delivered tenant, and answers a stock item only to an Operation of that tenant. Callers present bearer tokens of
// TestIssuer, verified by the Host's own bearer scheme.
[Collection<BrokerCollection>]
public sealed class GetStockItemTests(BrokerFixture broker)
{
    [Fact]
    public async Task A_stock_item_a_delivery_opened_under_one_tenant_is_answered_to_that_tenants_actor_and_to_no_other_operation()
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync();
        await using var catalog = await environment.StartCatalogHostAsync();
        var product = Guid.CreateVersion7();
        await BrokerEnvironment.CreateProductAsync(catalog, product, "Lamp", new OperationContext(GatewayTestExtensions.TestActor, "north"));
        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            $"exists (select from inventory.stock_items where product_id = '{product}')",
            "Inventory.Host never stocked the product."
        );
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);
        using var north = ClientOf(inventory, "north");
        using var south = ClientOf(inventory, "south");
        using var tenantless = ClientOf(inventory, tenant: null);

        using var answered = await north.GetAsync($"/inventory/v2/stock-items/{product}", TestContext.Current.CancellationToken);
        using var otherTenant = await south.GetAsync($"/inventory/v2/stock-items/{product}", TestContext.Current.CancellationToken);
        using var noTenant = await tenantless.GetAsync($"/inventory/v2/stock-items/{product}", TestContext.Current.CancellationToken);

        Assert.Equal([["north"]], await Sql.RowsAsync(environment.Inventory, $"select tenant from inventory.stock_items where product_id = '{product}'"));
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.Equal(
            $$"""{"productId":"{{product}}","quantityOnHand":0}""",
            await answered.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal("StockItem.NotFound", ErrorCodeOf(await ProblemAsync(otherTenant, HttpStatusCode.NotFound)));
        Assert.Equal("StockItem.NotFound", ErrorCodeOf(await ProblemAsync(noTenant, HttpStatusCode.NotFound)));
    }

    // Authenticated by default and versioned as the Catalog endpoints are: an anonymous caller is refused before the
    // endpoint, and a caller naming no version on the unversioned path is refused by API versioning.
    [Fact]
    public async Task The_endpoint_refuses_an_anonymous_caller_and_one_naming_no_api_version_and_answers_under_version_2()
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync();
        var product = Guid.CreateVersion7();
        using var anonymous = inventory.GetTestClient();
        using var north = ClientOf(inventory, "north");
        using var byHeader = new HttpRequestMessage(HttpMethod.Get, $"/inventory/stock-items/{product}")
        {
            Headers = { { WebOpenApiServiceCollectionExtensions.ApiVersionHeader, "2.0" } },
        };

        using var refusedAnonymous = await anonymous.GetAsync($"/inventory/v2/stock-items/{product}", TestContext.Current.CancellationToken);
        using var unversioned = await north.GetAsync($"/inventory/stock-items/{product}", TestContext.Current.CancellationToken);
        using var versioned = await north.SendAsync(byHeader, TestContext.Current.CancellationToken);

        await ProblemAsync(refusedAnonymous, HttpStatusCode.Unauthorized);
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, Assert.Single(refusedAnonymous.Headers.WwwAuthenticate).Scheme);
        await ProblemAsync(unversioned, HttpStatusCode.BadRequest);
        // The endpoint's own answer, which no stock item of the product makes NotFound, under the version the header named.
        Assert.Equal("StockItem.NotFound", ErrorCodeOf(await ProblemAsync(versioned, HttpStatusCode.NotFound)));
        Assert.Equal(["2.0"], versioned.Headers.GetValues("api-supported-versions"));
    }

    // A client of Inventory.Host as the person ada, with a token that names tenant, or none.
    private static HttpClient ClientOf(IHost inventory, string? tenant)
    {
        Dictionary<string, object> claims = new() { ["sub"] = "ada" };
        if (tenant is not null)
        {
            claims[InventoryHost.TenantClaim] = tenant;
        }

        var client = inventory.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(JwtBearerDefaults.AuthenticationScheme, TestIssuer.Token(claims));
        return client;
    }

    private static string? ErrorCodeOf(System.Text.Json.JsonElement problem) =>
        Assert.Single(problem.GetProperty("errors").EnumerateArray()).GetProperty("code").GetString();
}
