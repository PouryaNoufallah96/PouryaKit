using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.RemoveProducts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModularMonolith.Host;
using PouryaKit.Cqrs;
using PouryaKit.Security;
using PouryaKit.Testing;
using static ModularMonolith.Tests.Fixtures.ProblemResponses;

namespace ModularMonolith.Tests;

// The sample Host's own bearer scheme, AddPouryaKitJwtBearer, in place of TestAuthentication: real tokens from
// TestIssuer, verified by the JWT bearer middleware over the Host's HTTP pipeline.
public sealed class JwtBearerTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task A_verified_token_reaches_the_operation_as_its_subject_in_its_tenant()
    {
        await using var host = await StartHostAsync();
        using var client = ClientOf(
            host,
            TestIssuer.Token(new() { ["sub"] = "ada", [ModularMonolithHost.TenantClaim] = "acme" })
        );

        var seen = await ContextAsync(client);

        Assert.Equal((new SeenActor("ada", ActorKind.Person), "acme"), (seen.Actor, seen.Tenant));
    }

    [Fact]
    public async Task A_verified_token_without_the_tenant_claim_runs_with_no_tenant()
    {
        await using var host = await StartHostAsync();
        using var client = ClientOf(host, TestIssuer.Token(new() { ["sub"] = "ada" }));

        var seen = await ContextAsync(client);

        Assert.Equal(
            (new SeenActor("ada", ActorKind.Person), (string?)null),
            (seen.Actor, seen.Tenant)
        );
    }

    [Theory]
    [InlineData("acme")]
    [InlineData(null)]
    public async Task The_token_s_own_actor_and_tenant_claims_never_stand_in_for_the_configured_ones(
        string? tenant
    )
    {
        Dictionary<string, object> claims = new()
        {
            ["sub"] = "ada",
            [ClaimTypes.NameIdentifier] = "mallory",
            [PouryaKitClaimTypes.Tenant] = "globex",
        };
        if (tenant is not null)
        {
            claims[ModularMonolithHost.TenantClaim] = tenant;
        }

        await using var host = await StartHostAsync();
        using var client = ClientOf(host, TestIssuer.Token(claims));

        var seen = await ContextAsync(client);

        Assert.Equal((new SeenActor("ada", ActorKind.Person), tenant), (seen.Actor, seen.Tenant));
    }

    [Theory]
    [InlineData("a wrong signature")]
    [InlineData("a wrong issuer")]
    [InlineData("a wrong audience")]
    [InlineData("an expired lifetime")]
    [InlineData("no subject")]
    [InlineData("no audience")]
    public async Task A_token_with_a_flaw_is_refused_with_a_401_problem_response(string flaw)
    {
        Dictionary<string, object> claims =
            flaw == "no subject" ? new() { ["name"] = "ada" } : new() { ["sub"] = "ada" };
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
                    case "no audience":
                        descriptor.Audience = null;
                        break;
                    case "an expired lifetime":
                        // Beyond the five minutes of clock skew the handler allows.
                        descriptor.IssuedAt = descriptor.NotBefore = DateTime.UtcNow.AddMinutes(
                            -30
                        );
                        descriptor.Expires = DateTime.UtcNow.AddMinutes(-10);
                        break;
                }
            }
        );
        await using var host = await StartHostAsync();
        using var client = ClientOf(host, token);

        var response = await client.GetAsync(
            "/stub/context",
            TestContext.Current.CancellationToken
        );

        var problem = await ProblemAsync(response, HttpStatusCode.Unauthorized);
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
        Assert.Equal(
            JwtBearerDefaults.AuthenticationScheme,
            Assert.Single(response.Headers.WwwAuthenticate).Scheme
        );
    }

    [Fact]
    public async Task The_permission_claims_of_a_verified_token_decide_a_declared_permission_and_without_it_nothing_commits()
    {
        var lamp = await CreateLampAsync();
        // A second started Host shares the message store's queues: it must not take, and die holding, a delivery the
        // fixture's Host is waiting for.
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        await using var host = await StartHostAsync();
        using var other = ClientOf(
            host,
            TestIssuer.Token(
                new()
                {
                    ["sub"] = "ada",
                    [ModularMonolithHost.TenantClaim] = AppFixture.TestTenant,
                    [CallerPermissions.ClaimType] = "catalog.products.other",
                }
            )
        );
        using var holder = ClientOf(
            host,
            TestIssuer.Token(
                new()
                {
                    ["sub"] = "bob",
                    [ModularMonolithHost.TenantClaim] = AppFixture.TestTenant,
                    [CallerPermissions.ClaimType] = RemoveProductsCommand.Permission,
                }
            )
        );

        var refused = await RemoveLampAsync(other);
        var unchanged = await Fixture.RowsAsync(
            $"select is_deleted, version from catalog.products where id = '{lamp}'"
        );
        var removed = await RemoveLampAsync(holder);
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);

        var problem = await ProblemAsync(refused, HttpStatusCode.Forbidden);
        Assert.Equal(
            [PermissionErrors.Denied.Code],
            problem
                .GetProperty("errors")
                .EnumerateArray()
                .Select(error => error.GetProperty("code").GetString())
        );
        Assert.Equal(
            [
                [false, 1L],
            ],
            unchanged
        );
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(
            [
                [true, "bob"],
            ],
            await Fixture.RowsAsync(
                $"select is_deleted, deleted_by from catalog.products where id = '{lamp}'"
            )
        );
    }

    [Theory]
    [InlineData("audience validation off", "validate it against a configured audience")]
    [InlineData("audience not required", "validate it against a configured audience")]
    [InlineData("no audience", "validate it against a configured audience")]
    [InlineData("issuer validation off", "validate the issuer")]
    [InlineData("no issuer", "validate the issuer")]
    [InlineData("no signing key", "validate the signature")]
    [InlineData("lifetime validation off", "validate the token's lifetime")]
    [InlineData("inbound claims mapped", "maps inbound claims")]
    [InlineData(
        nameof(TokenValidationParameters.AudienceValidator),
        "TokenValidationParameters.AudienceValidator"
    )]
    [InlineData(
        nameof(TokenValidationParameters.IssuerValidator),
        "TokenValidationParameters.IssuerValidator,"
    )]
    [InlineData(
        nameof(TokenValidationParameters.IssuerValidatorUsingConfiguration),
        "TokenValidationParameters.IssuerValidatorUsingConfiguration"
    )]
    [InlineData(
        nameof(TokenValidationParameters.LifetimeValidator),
        "TokenValidationParameters.LifetimeValidator"
    )]
    [InlineData(
        nameof(TokenValidationParameters.SignatureValidator),
        "TokenValidationParameters.SignatureValidator,"
    )]
    [InlineData(
        nameof(TokenValidationParameters.SignatureValidatorUsingConfiguration),
        "TokenValidationParameters.SignatureValidatorUsingConfiguration"
    )]
    [InlineData("events resolved by type", "bypass the Kit's claim normalization")]
    [InlineData(
        "events replaced after the kit's registration",
        "bypass the Kit's claim normalization"
    )]
    [InlineData("events subclassed", "bypass the Kit's claim normalization")]
    public async Task A_host_whose_bearer_scheme_would_weaken_verification_fails_when_it_starts(
        string weakness,
        string failure
    )
    {
        await using var host = AppFixture.ComposeHost(
            "Production",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder =>
            {
                if (weakness == "events replaced after the kit's registration")
                {
                    builder.Services.PostConfigure<JwtBearerOptions>(
                        JwtBearerDefaults.AuthenticationScheme,
                        bearer => bearer.Events = new JwtBearerEvents()
                    );
                    return;
                }

                builder.Services.Configure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    bearer => Weaken(bearer, weakness)
                );
            }
        );

        var thrown = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains(failure, Assert.Single(thrown.Failures));
    }

    [Fact]
    public async Task A_host_that_resolves_its_signing_keys_from_the_configuration_resolver_alone_verifies_signatures()
    {
        await using var host = await StartHostAsync(builder =>
            builder.Services.Configure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                bearer =>
                {
                    var token = bearer.TokenValidationParameters;
                    (token.IssuerSigningKey, token.IssuerSigningKeys) = (null, []);
                    token.IssuerSigningKeyResolverUsingConfiguration = (_, _, _, _, _) =>
                        [TestIssuer.SigningKey];
                }
            )
        );
        using var trusted = ClientOf(host, TestIssuer.Token(new() { ["sub"] = "ada" }));
        using var untrusted = ClientOf(
            host,
            TestIssuer.Token(
                new() { ["sub"] = "ada" },
                descriptor => descriptor.SigningCredentials = TestIssuer.Untrusted()
            )
        );

        var seen = await ContextAsync(trusted);
        var refused = await untrusted.GetAsync(
            "/stub/context",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(new SeenActor("ada", ActorKind.Person), seen.Actor);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task An_application_token_validated_handler_still_runs_after_the_normalization()
    {
        List<string?> seenActors = [];
        await using var host = await StartHostAsync(builder =>
            builder.Services.Configure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                bearer =>
                    bearer.Events.OnTokenValidated = context =>
                    {
                        seenActors.Add(
                            context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier)
                        );
                        return Task.CompletedTask;
                    }
            )
        );
        using var client = ClientOf(host, TestIssuer.Token(new() { ["sub"] = "ada" }));

        var seen = await ContextAsync(client);

        Assert.Equal(["ada"], seenActors);
        Assert.Equal(new SeenActor("ada", ActorKind.Person), seen.Actor);
    }

    private static void Weaken(JwtBearerOptions bearer, string weakness)
    {
        var token = bearer.TokenValidationParameters;
        switch (weakness)
        {
            case "audience validation off":
                token.ValidateAudience = false;
                break;
            case "audience not required":
                token.RequireAudience = false;
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
            case nameof(TokenValidationParameters.AudienceValidator):
                token.AudienceValidator = (_, _, _) => true;
                break;
            case nameof(TokenValidationParameters.IssuerValidator):
                token.IssuerValidator = (issuer, _, _) => issuer;
                break;
            case nameof(TokenValidationParameters.IssuerValidatorUsingConfiguration):
                token.IssuerValidatorUsingConfiguration = (issuer, _, _, _) => issuer;
                break;
            case nameof(TokenValidationParameters.LifetimeValidator):
                token.LifetimeValidator = (_, _, _, _) => true;
                break;
            case nameof(TokenValidationParameters.SignatureValidator):
                token.SignatureValidator = (jwt, _) => new JsonWebToken(jwt);
                break;
            case nameof(TokenValidationParameters.SignatureValidatorUsingConfiguration):
                token.SignatureValidatorUsingConfiguration = (jwt, _, _) => new JsonWebToken(jwt);
                break;
            case "events resolved by type":
                bearer.EventsType = typeof(JwtBearerEvents);
                break;
            case "events subclassed":
                bearer.Events = new SkippingEvents();
                break;
        }
    }

    // Overrides TokenValidated without calling OnTokenValidated, so the Kit's normalization would never run.
    private sealed class SkippingEvents : JwtBearerEvents
    {
        public override Task TokenValidated(TokenValidatedContext context) => Task.CompletedTask;
    }

    private async Task<WebApplication> StartHostAsync(
        Action<WebApplicationBuilder>? configure = null
    )
    {
        var host = AppFixture.ComposeHost(
            "Production",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder =>
            {
                // After AppFixture's TestAuthentication, so the bearer scheme is the default again.
                builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme);
                configure?.Invoke(builder);
            }
        );
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static HttpClient ClientOf(WebApplication host, string token)
    {
        var client = AppFixture.ClientOf(host);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            JwtBearerDefaults.AuthenticationScheme,
            token
        );
        return client;
    }

    private static async Task<SeenContext> ContextAsync(HttpClient client)
    {
        var response = await client.GetAsync(
            "/stub/context",
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (
            await response.Content.ReadFromJsonAsync<SeenContext>(
                TestContext.Current.CancellationToken
            )
        )!;
    }

    private async Task<Guid> CreateLampAsync()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var lamps = Guid.CreateVersion7();
        var lamp = Guid.CreateVersion7();
        await catalog.SendAsync(
            new CreateCategoryCommand(lamps, "Lamps"),
            TestContext.Current.CancellationToken
        );
        await catalog.SendAsync(
            new CreateProductCommand(lamp, lamps, "Lamp"),
            TestContext.Current.CancellationToken
        );
        return lamp;
    }

    private static Task<HttpResponseMessage> RemoveLampAsync(HttpClient client) =>
        client.PostAsJsonAsync(
            "/stub/catalog-products/removal",
            new { names = new[] { "Lamp" } },
            TestContext.Current.CancellationToken
        );
}
