using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.RemoveProducts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModularMonolith.Host;
using PouryaKit.Cqrs;
using PouryaKit.Security;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// The sample Host's own bearer scheme, AddPouryaKitJwtBearer, in place of TestAuthentication: real tokens from
// TestIssuer, verified by the JWT bearer middleware over the Host's HTTP pipeline.
public sealed class JwtBearerTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task A_verified_token_reaches_the_operation_as_its_subject_in_its_tenant()
    {
        await using var host = await StartHostAsync();
        using var client = ClientOf(host, TestIssuer.Token(new() { ["sub"] = "ada", [ModularMonolithHost.TenantClaim] = "acme" }));

        var seen = await ContextAsync(client);

        Assert.Equal((new SeenActor("ada", ActorKind.Person), "acme"), (seen.Actor, seen.Tenant));
    }

    [Fact]
    public async Task A_verified_token_without_the_tenant_claim_runs_with_no_tenant()
    {
        await using var host = await StartHostAsync();
        using var client = ClientOf(host, TestIssuer.Token(new() { ["sub"] = "ada" }));

        var seen = await ContextAsync(client);

        Assert.Equal((new SeenActor("ada", ActorKind.Person), (string?)null), (seen.Actor, seen.Tenant));
    }

    [Theory]
    [InlineData("a wrong signature")]
    [InlineData("a wrong issuer")]
    [InlineData("a wrong audience")]
    [InlineData("an expired lifetime")]
    [InlineData("no subject")]
    public async Task A_token_with_a_flaw_is_refused_with_a_401_problem_response(string flaw)
    {
        Dictionary<string, object> claims = flaw == "no subject" ? new() { ["name"] = "ada" } : new() { ["sub"] = "ada" };
        var token = TestIssuer.Token(
            claims,
            descriptor =>
            {
                switch (flaw)
                {
                    case "a wrong signature":
                        descriptor.SigningCredentials = TestIssuer.Untrusted();
                        break;
                    case "a wrong issuer":
                        descriptor.Issuer = "https://another-issuer.test";
                        break;
                    case "a wrong audience":
                        descriptor.Audience = "another-api";
                        break;
                    case "an expired lifetime":
                        // Beyond the five minutes of clock skew the handler allows.
                        descriptor.IssuedAt = descriptor.NotBefore = DateTime.UtcNow.AddMinutes(-30);
                        descriptor.Expires = DateTime.UtcNow.AddMinutes(-10);
                        break;
                }
            }
        );
        await using var host = await StartHostAsync();
        using var client = ClientOf(host, token);

        var response = await client.GetAsync("/stub/context", TestContext.Current.CancellationToken);

        var problem = await ProblemAsync(response, HttpStatusCode.Unauthorized);
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task The_permission_claims_of_a_verified_token_decide_a_declared_permission_and_without_it_nothing_commits()
    {
        var lamp = await CreateLampAsync();
        // A second started Host shares the message store's queues: it must not take, and die holding, a delivery the
        // fixture's Host is waiting for.
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        await using var host = await StartHostAsync();
        using var other = ClientOf(host, TestIssuer.Token(new() { ["sub"] = "ada", [CallerPermissions.ClaimType] = "catalog.products.other" }));
        using var holder = ClientOf(host, TestIssuer.Token(new() { ["sub"] = "bob", [CallerPermissions.ClaimType] = RemoveProductsCommand.Permission }));

        var refused = await RemoveLampAsync(other);
        var unchanged = await Fixture.RowsAsync($"select is_deleted, version from catalog.products where id = '{lamp}'");
        var removed = await RemoveLampAsync(holder);
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);

        var problem = await ProblemAsync(refused, HttpStatusCode.Forbidden);
        Assert.Equal(
            [PermissionErrors.Denied.Code],
            problem.GetProperty("errors").EnumerateArray().Select(error => error.GetProperty("code").GetString())
        );
        Assert.Equal([[false, 1L]], unchanged);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal([[true, "bob"]], await Fixture.RowsAsync($"select is_deleted, deleted_by from catalog.products where id = '{lamp}'"));
    }

    [Theory]
    [InlineData("audience validation off", "validate the audience")]
    [InlineData("no audience", "validate the audience")]
    [InlineData("issuer validation off", "validate the issuer")]
    [InlineData("no issuer", "validate the issuer")]
    [InlineData("no signing key", "validate the signature")]
    [InlineData("lifetime validation off", "validate the token's lifetime")]
    [InlineData("inbound claims mapped", "maps inbound claims")]
    [InlineData("events resolved by type", "bypasses the Kit's claim normalization")]
    public async Task A_host_whose_bearer_scheme_would_weaken_verification_fails_when_it_starts(string weakness, string failure)
    {
        await using var host = AppFixture.ComposeHost(
            "Production",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder =>
                builder.Services.Configure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    bearer =>
                    {
                        var token = bearer.TokenValidationParameters;
                        switch (weakness)
                        {
                            case "audience validation off":
                                token.ValidateAudience = false;
                                break;
                            case "no audience":
                                (token.ValidAudience, token.ValidAudiences) = (null, []);
                                break;
                            case "issuer validation off":
                                token.ValidateIssuer = false;
                                break;
                            case "no issuer":
                                (token.ValidIssuer, token.ValidIssuers) = (null, []);
                                break;
                            case "no signing key":
                                token.IssuerSigningKeys = [];
                                break;
                            case "lifetime validation off":
                                token.ValidateLifetime = false;
                                break;
                            case "inbound claims mapped":
                                bearer.MapInboundClaims = true;
                                break;
                            case "events resolved by type":
                                bearer.EventsType = typeof(JwtBearerEvents);
                                break;
                        }
                    }
                )
        );

        var thrown = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(failure, Assert.Single(thrown.Failures));
    }

    private async Task<WebApplication> StartHostAsync()
    {
        var host = AppFixture.ComposeHost(
            "Production",
            Fixture.ConnectionString,
            new RecordedLogs(),
            // After AppFixture's TestAuthentication, so the bearer scheme is the default again.
            builder => builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        );
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static HttpClient ClientOf(WebApplication host, string token)
    {
        var client = AppFixture.ClientOf(host);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(JwtBearerDefaults.AuthenticationScheme, token);
        return client;
    }

    private static async Task<SeenContext> ContextAsync(HttpClient client)
    {
        var response = await client.GetAsync("/stub/context", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SeenContext>(TestContext.Current.CancellationToken))!;
    }

    private async Task<Guid> CreateLampAsync()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var lamps = Guid.CreateVersion7();
        var lamp = Guid.CreateVersion7();
        await catalog.SendAsync(new CreateCategoryCommand(lamps, "Lamps"), TestContext.Current.CancellationToken);
        await catalog.SendAsync(new CreateProductCommand(lamp, lamps, "Lamp"), TestContext.Current.CancellationToken);
        return lamp;
    }

    private static Task<HttpResponseMessage> RemoveLampAsync(HttpClient client) =>
        client.PostAsJsonAsync("/stub/catalog-products/removal", new { names = new[] { "Lamp" } }, TestContext.Current.CancellationToken);

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return problem.RootElement.Clone();
    }
}
