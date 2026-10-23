using System.ClientModel;
using System.Text.Json;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.AskAboutProducts;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.GetProduct;
using Catalog.Products.Features.RemoveProducts;
using Catalog.Products.Features.RenameProduct;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.AI;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Security;
using PouryaKit.Testing;
using Testcontainers.Ollama;

namespace ModularMonolith.Tests;

// Ticket #135: the Host's one chat client, from options validated at start, reaches an OpenAI-compatible API through the
// OpenAI client, and a Gateway tool runs what the model asks for as an Operation through the Module's Gateway, continuing
// the run once per call. Against the in-process ChatStub, deterministic (spec #115's A2); the one real probe, Ollama in a
// container with a small model and no hosted key, runs only when POURYAKIT_OLLAMA_PROBE=1.
public sealed class AiTests(AppFixture app, AiFixture ai) : IClassFixture<AiFixture>
{
    private const string OllamaProbe = "POURYAKIT_OLLAMA_PROBE";
    private const string OllamaImage = "ollama/ollama:0.40.6";
    private const string OllamaModel = "qwen2.5:0.5b";

    private static readonly Actor Ada = Actor.Person("ada");

    private IGatewayProcessor<CatalogModule> Catalog => ai.Host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();

    private IChatClient Chat => ai.Host.Services.GetRequiredService<IChatClient>();

    [Fact]
    public async Task A_tool_call_runs_through_the_gateway_as_an_operation_that_continues_the_run_once_per_call()
    {
        var (product, name) = await CreateProductAsync(Catalog);
        ai.Stub.Script(ChatReply.Calls(GetProduct(product), GetProduct(product)), ChatReply.Text($"It is the {name}."));
        // A request id the run carries is never a call's: each call is an Operation of its own.
        var run = new OperationContext(Ada, AppFixture.TestTenant) { RequestId = "ask-1" };

        var answered = await Catalog.QueryAsync(new AskAboutProductsQuery($"What is {product} called?"), run, TestContext.Current.CancellationToken);

        Assert.Equal($"It is the {name}.", answered.Value);
        var calls = SeenBy(ai.Host, run);
        Assert.Equal(2, calls.Count);
        Assert.All(
            calls,
            call => Assert.Equal(
                (run.Actor, run.Tenant, run.CorrelationId, (Guid?)run.OperationId, (string?)null, true),
                (call.Actor, call.Tenant, call.CorrelationId, call.CausationId, call.RequestId, call.IsTrustedContinuation)
            )
        );
        Assert.Equal(3, calls.Select(call => call.OperationId).Append(run.OperationId).Distinct().Count());
        // What each call answered reached the model: the product, as Catalog's GetProduct returns it.
        Assert.All(ai.Stub.ToolResultsOf(1), result => Assert.Equal(name, JsonDocument.Parse(result).RootElement.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Outside_an_authorized_operation_each_call_is_authorized_for_the_actor_and_a_denied_permission_is_a_failed_tool_result()
    {
        var (product, name) = await CreateProductAsync(Catalog);
        ai.Stub.Script(ChatReply.Calls(new ToolCall("RemoveProducts", new { names = new[] { name } })), ChatReply.Text("I may not."));
        // A run the Host itself starts for Ada, outside any Operation, as an endpoint or a background service would.
        var run = new OperationContext(Ada, AppFixture.TestTenant);
        var removeProducts = Catalog.Tool("RemoveProducts", "Removes the products with these names.", run, (ProductsToRemove products) => new RemoveProductsCommand(products.Names));

        var response = await Chat.GetResponseAsync("Remove it.", new ChatOptions { Tools = [removeProducts] }, TestContext.Current.CancellationToken);

        Assert.Equal("I may not.", response.Text);
        var failed = JsonDocument.Parse(Assert.Single(ai.Stub.ToolResultsOf(1))).RootElement;
        Assert.Equal(PermissionErrors.Denied.Code, Assert.Single(failed.GetProperty("errors").EnumerateArray()).GetProperty("code").GetString());
        Assert.True((await Catalog.QueryAsync(new GetProductQuery(product), TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task Two_calls_of_one_command_type_with_different_arguments_both_run_and_each_commits_through_its_unit_of_work()
    {
        var category = Guid.CreateVersion7();
        await Catalog.SendAsync(new CreateCategoryCommand(category, Unique("Chairs")), TestContext.Current.CancellationToken);
        var (first, second) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        ai.Stub.Script(
            ChatReply.Calls(CreateProduct(first, category, Unique("Chair")), CreateProduct(second, category, Unique("Stool"))),
            ChatReply.Text("Both are in.")
        );
        // CreateProduct is idempotent: had a call carried the run's request id, the second would replay the first.
        var run = new OperationContext(Ada, AppFixture.TestTenant) { RequestId = "create-1" };
        var createProduct = Catalog.Tool(
            "CreateProduct",
            "Creates a product in a category.",
            run,
            (ProductToCreate product) => new CreateProductCommand(product.ProductId, product.CategoryId, product.Name)
        );

        await Chat.GetResponseAsync("Add a chair and a stool.", new ChatOptions { Tools = [createProduct] }, TestContext.Current.CancellationToken);

        Assert.Equal([JsonSerializer.Serialize(first), JsonSerializer.Serialize(second)], ai.Stub.ToolResultsOf(1));
        Assert.True((await Catalog.QueryAsync(new GetProductQuery(first), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await Catalog.QueryAsync(new GetProductQuery(second), TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task A_run_whose_model_keeps_calling_tools_stops_at_the_iteration_cap()
    {
        var (product, _) = await CreateProductAsync(Catalog);
        ai.Stub.Script(ChatReply.Calls(GetProduct(product)));
        var run = new OperationContext(Ada, AppFixture.TestTenant);

        await Chat.GetResponseAsync("Look it up.", new ChatOptions { Tools = [GetProductTool(run)] }, TestContext.Current.CancellationToken);

        Assert.Equal(AiFixture.MaximumIterations, SeenBy(ai.Host, run).Count);
        // The call after the last iteration offers no tool, so the run ends with whatever the model answers.
        Assert.Equal(
            [.. Enumerable.Repeat<IReadOnlyList<string>>(["GetProduct"], AiFixture.MaximumIterations), []],
            Enumerable.Range(0, ai.Stub.Requests.Count).Select(ai.Stub.ToolsOf)
        );
    }

    [Fact]
    public async Task A_tool_the_allow_list_does_not_name_is_withheld_from_the_model_and_never_runs()
    {
        var (product, name) = await CreateProductAsync(Catalog);
        ai.Stub.Script(ChatReply.Calls(new ToolCall("RenameProduct", new { productId = product, name = "Renamed" })), ChatReply.Text("Done."));
        var run = new OperationContext(Ada, AppFixture.TestTenant);
        var renameProduct = Catalog.Tool("RenameProduct", "Renames a product.", run, (ProductToRename product) => new RenameProductCommand(product.ProductId, product.Name));

        await Chat.GetResponseAsync("Rename it.", new ChatOptions { Tools = [GetProductTool(run), renameProduct] }, TestContext.Current.CancellationToken);

        Assert.Equal(["GetProduct"], ai.Stub.ToolsOf(0));
        Assert.Equal(["Error: Requested function \"RenameProduct\" not found."], ai.Stub.ToolResultsOf(1));
        Assert.Equal(name, (await Catalog.QueryAsync(new GetProductQuery(product), TestContext.Current.CancellationToken)).Value.Name);
    }

    [Fact]
    public async Task A_host_with_the_service_defaults_keeps_their_one_resilience_pipeline_on_the_provider_client()
    {
        await using var host = AppFixture.ComposeHost("Development", app.ConnectionString, new RecordedLogs(), builder => builder.AddServiceDefaults(), AiFixture.StubArguments(ai.Stub));
        ai.Stub.Script(ChatReply.Failure(503));

        // The provider's client carries the one pipeline the defaults give every client, and none of its own.
        var handlers = host.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(AIServiceCollectionExtensions.HttpClientName);
        Assert.Single(Chain(handlers).OfType<ResilienceHandler>());
        // That pipeline retries safe methods only, so a failed chat completion, a POST, is sent once: a retry policy of the
        // OpenAI client's own would send it again.
        var thrown = await Assert.ThrowsAsync<ClientResultException>(() =>
            host.Services.GetRequiredService<IChatClient>().GetResponseAsync("Hello.", cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Equal(503, thrown.Status);
        Assert.Single(ai.Stub.Requests);
    }

    [Theory]
    [InlineData("PouryaKitAIOptions.Provider is not an AIProvider value; set OpenAI, AzureOpenAI or Ollama.", "--PouryaKit:AI:Provider=99")]
    [InlineData("PouryaKitAIOptions.Model names no model; set the model the Host's runs use, or for Azure OpenAI its deployment.", "--PouryaKit:AI:Model= ")]
    [InlineData("PouryaKitAIOptions.Endpoint is not an absolute HTTP or HTTPS address.", "--PouryaKit:AI:Endpoint=ftp://localhost/v1")]
    [InlineData(
        "PouryaKitAIOptions.ApiKey is required for OpenAI and Azure OpenAI; set it from a secret store, never in source control.",
        "--PouryaKit:AI:Provider=AzureOpenAI"
    )]
    [InlineData(
        "PouryaKitAIOptions.Endpoint is required for Azure OpenAI, its resource's v1 endpoint, and for Ollama, its OpenAI-compatible API.",
        "--PouryaKit:AI:Endpoint="
    )]
    [InlineData("PouryaKitAIOptions.MaximumIterationsPerRun is below 1; a run calls the model at least once.", "--PouryaKit:AI:MaximumIterationsPerRun=0")]
    [InlineData("PouryaKitAIOptions.Tools names a blank tool; name each tool a run may be given.", "--PouryaKit:AI:Tools:1= ")]
    public async Task A_host_whose_ai_options_are_invalid_fails_to_start_naming_the_option(string failure, params string[] arguments)
    {
        await using var host = AppFixture.ComposeHost("Development", app.ConnectionString, new RecordedLogs(), null, arguments);

        var thrown = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(failure, Assert.Single(thrown.Failures));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public void A_host_that_registers_another_chat_client_fails_when_it_is_built_naming_both(string order)
    {
        const string Kit = "the factory PouryaKit.AI.AIServiceCollectionExtensions.Create";
        var other = $"the factory {typeof(AiTests).FullName}.{nameof(OtherChatClient)}";
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.AddPouryaKitCompositionChecks();
        if (order == "before")
        {
            builder.Services.AddSingleton<IChatClient>(OtherChatClient);
        }

        builder.Services.AddPouryaKitAI();
        if (order == "after")
        {
            builder.Services.AddSingleton<IChatClient>(OtherChatClient);
        }

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            $"The Host registers 2 IChatClient providers, {string.Join(", ", order == "before" ? [other, Kit] : new[] { Kit, other })}, and composes exactly one: remove all but one of its chat client registrations.",
            thrown.Message
        );
    }

    [Fact]
    public void A_host_that_removes_the_chat_client_fails_when_it_is_built_naming_the_contract()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.AddPouryaKitCompositionChecks();
        builder.Services.AddPouryaKitAI();
        builder.Services.RemoveAll<IChatClient>();

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal("The Host registers no IChatClient, and composes exactly one: AddPouryaKitAI registered one, then it was removed.", thrown.Message);
    }

    // The second registration adds no provider of its own, and its settings in code still apply. Read validated, as the
    // clients read them: OpenAI's key is required.
    [Fact]
    public void The_options_each_registration_sets_in_code_apply()
    {
        var services = new ServiceCollection();
        services
            .AddPouryaKitAI(options =>
            {
                options.Model = "chat-model";
                options.ApiKey = "key";
            })
            .AddPouryaKitEmbeddings(options => options.EmbeddingModel = "embedding-model");
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<PouryaKitAIOptions>>().Value;

        Assert.Equal(("chat-model", "embedding-model"), (options.Model, options.EmbeddingModel));
    }

    [Fact]
    public async Task An_ollama_model_in_a_container_completes_a_tool_call_with_no_hosted_key()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(OllamaProbe) == "1",
            $"The Ollama probe runs only with {OllamaProbe}=1: it pulls {OllamaImage} and the model {OllamaModel}."
        );
        await using var ollama = new OllamaBuilder(OllamaImage).Build();
        await ollama.StartAsync(TestContext.Current.CancellationToken);
        var pulled = await ollama.ExecAsync(["ollama", "pull", OllamaModel], TestContext.Current.CancellationToken);
        Assert.True(pulled.ExitCode == 0, pulled.Stderr);
        var database = $"ollama_{Guid.CreateVersion7():N}";
        await app.RowsAsync($"create database {database}");
        await using var host = AppFixture.ComposeHost(
            "Development",
            new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = database }.ConnectionString,
            new RecordedLogs(),
            null,
            "--PouryaKit:AI:Provider=Ollama",
            $"--PouryaKit:AI:Endpoint={ollama.GetBaseAddress()}v1",
            $"--PouryaKit:AI:Model={OllamaModel}"
        );
        await HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken);
        try
        {
            var catalog = host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();
            var (product, name) = await CreateProductAsync(catalog);
            var run = new OperationContext(Ada, AppFixture.TestTenant);

            var answered = await catalog.QueryAsync(
                new AskAboutProductsQuery($"Call the GetProduct tool with the productId {product}, then tell me the product's name."),
                run,
                TestContext.Current.CancellationToken
            );

            TestContext.Current.TestOutputHelper?.WriteLine($"{OllamaModel} answered: {answered.Value} (the product is the {name})");
            Assert.NotEmpty(SeenBy(host, run));
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static IEnumerable<HttpMessageHandler> Chain(HttpMessageHandler handler)
    {
        for (HttpMessageHandler? next = handler; next is not null; next = (next as DelegatingHandler)?.InnerHandler)
        {
            yield return next;
        }
    }

    // The context of every GetProduct Operation of the run's chain, as RecordCatalogQuestionContext saw it in Catalog's scope.
    private static List<OperationContext> SeenBy(IHost host, OperationContext run) =>
        [.. host.Services.GetRequiredService<CatalogQuestionContexts>().Seen.Where(seen => seen.CorrelationId == run.CorrelationId)];

    private AIFunction GetProductTool(OperationContext run) =>
        Catalog.Tool("GetProduct", "Finds one product by its id.", run, (ProductToFind product) => new GetProductQuery(product.ProductId));

    private static async Task<(Guid Product, string Name)> CreateProductAsync(IGatewayProcessor<CatalogModule> catalog)
    {
        var (category, product, name) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Unique("Lamp"));
        Assert.True((await catalog.SendAsync(new CreateCategoryCommand(category, Unique("Lamps")), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await catalog.SendAsync(new CreateProductCommand(product, category, name), TestContext.Current.CancellationToken)).IsSuccess);
        return (product, name);
    }

    private static ToolCall GetProduct(Guid product) => new("GetProduct", new { productId = product });

    private static ToolCall CreateProduct(Guid product, Guid category, string name) =>
        new("CreateProduct", new { productId = product, categoryId = category, name });

    private static string Unique(string name) => $"{name} {Guid.CreateVersion7():N}";

    // A Host's own chat client, never called.
    private static IChatClient OtherChatClient(IServiceProvider services) => throw new NotSupportedException();

    private sealed record ProductsToRemove(IReadOnlyList<string> Names);

    private sealed record ProductToCreate(Guid ProductId, Guid CategoryId, string Name);

    private sealed record ProductToRename(Guid ProductId, string Name);
}
