using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModularMonolith.Tests.Vectors;
using Npgsql;
using PouryaKit.AI;
using PouryaKit.AI.Postgres;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Testing;
using Testcontainers.Ollama;

namespace ModularMonolith.Tests;

// Ticket #138: the Host's embedding generator, from the AI options validated at start, embeds through the OpenAI client,
// and a Module's vector store keeps each embedding in a collection of the package's own schema, ai, created by the Kit's
// migration helper, writes it on the Operation's Unit of Work and finds the nearest by cosine distance within the
// Operation's tenant. Against pgvector in a container and the ChatStub's deterministic embeddings; the one real probe,
// Ollama embedding text, runs only when POURYAKIT_OLLAMA_PROBE=1.
public sealed class VectorSearchTests(AppFixture app, VectorFixture vectors)
    : IClassFixture<VectorFixture>
{
    private const string OllamaProbe = "POURYAKIT_OLLAMA_PROBE";
    private const string OllamaImage = "ollama/ollama:0.40.6";

    // AiTests' chat model, qwen2.5:0.5b, serves no embeddings on this Ollama: its runner answers 501, "This server does not
    // support embeddings", so the probe pulls an embedding model.
    private const string OllamaModel = "all-minilm:22m";

    private static readonly Actor Ada = Actor.Person("ada");

    private IGatewayProcessor<VectorsModule> Vectors =>
        vectors.Host.Services.GetRequiredService<IGatewayProcessor<VectorsModule>>();

    [Fact]
    public async Task Stored_passages_are_found_nearest_first_by_cosine_distance()
    {
        var run = new OperationContext(Ada, Tenant());
        await StoreAsync(run, "a-same", "avocado bread");
        await StoreAsync(run, "b-far", "cherry date");
        await StoreAsync(run, "c-near", "almond");

        var found = await SearchAsync(run, "apple banana", 2);

        Assert.Equal(["a-same", "c-near"], found.Select(match => match.Key));
        Assert.Equal("avocado bread", found[0].Content);
        Assert.Equal(0, found[0].Distance, 6);
        Assert.Equal(1 - 1 / Math.Sqrt(2), found[1].Distance, 6);
    }

    [Fact]
    public async Task The_collection_is_a_table_of_the_packages_own_schema_with_the_vector_extension_its_migration_created()
    {
        var columns = await Sql.RowsAsync(
            vectors.ConnectionString,
            "select c.table_schema, format_type(a.atttypid, a.atttypmod) from information_schema.columns c join pg_attribute a on a.attrelid = format('%I.%I', c.table_schema, c.table_name)::regclass and a.attname = c.column_name where c.table_name = 'passages' and c.column_name = 'embedding'"
        );
        var extensions = await Sql.RowsAsync(
            vectors.ConnectionString,
            "select extname from pg_extension where extname = 'vector'"
        );

        Assert.Equal(["ai", $"vector({VectorsModule.Dimensions})"], Assert.Single(columns));
        Assert.Single(extensions);
    }

    [Fact]
    public async Task A_database_without_the_vector_extension_fails_its_migration_naming_the_fix()
    {
        var database = $"vectors_{Guid.CreateVersion7():N}";
        await app.RowsAsync($"create database {database}");
        using var host = VectorFixture.Compose(
            new NpgsqlConnectionStringBuilder(app.ConnectionString)
            {
                Database = database,
            }.ConnectionString,
            vectors.Stub
        );

        var thrown = await Assert.ThrowsAsync<PostgresException>(() =>
            HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            "PouryaKit.AI.Postgres stores embeddings with the vector extension, which this database could not create. Install pgvector on the PostgreSQL server, or have a role that may create extensions run create extension vector in this database, then migrate again.",
            thrown.MessageText
        );
    }

    [Fact]
    public async Task A_search_finds_only_the_passages_its_operations_tenant_stored()
    {
        var (north, south) = (
            new OperationContext(Ada, Tenant()),
            new OperationContext(Ada, Tenant())
        );
        await StoreAsync(north, "shared", "apple banana north");
        await StoreAsync(south, "shared", "apple banana south");
        await StoreAsync(south, "only-south", "apple banana");

        var found = await SearchAsync(north, "apple banana", 5);

        var match = Assert.Single(found);
        Assert.Equal(("shared", "apple banana north"), (match.Key, match.Content));
    }

    [Fact]
    public async Task A_passage_a_failed_command_stored_rolls_back_with_it()
    {
        var run = new OperationContext(Ada, Tenant());
        await StoreAsync(run, "kept", "apple banana");

        var stored = await Vectors.SendAsync(
            new StorePassageCommand("refused", "apple banana", Fail: true),
            run,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(StorePassageCommandHandler.Refused, Assert.Single(stored.Errors));
        Assert.Equal(
            ["kept"],
            (await SearchAsync(run, "apple banana", 5)).Select(match => match.Key)
        );
    }

    [Fact]
    public async Task A_search_in_the_command_that_stored_a_passage_finds_it_and_the_command_still_commits()
    {
        var run = new OperationContext(Ada, Tenant());

        var found = await Vectors.SendAsync(
            new StoreThenSearchPassageCommand("own", "apple banana"),
            run,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("own", Assert.Single(found.Value).Key);
        Assert.Equal(
            ["own"],
            (await SearchAsync(run, "apple banana", 5)).Select(match => match.Key)
        );
    }

    [Fact]
    public async Task A_removed_passage_is_found_no_more()
    {
        var run = new OperationContext(Ada, Tenant());
        await StoreAsync(run, "kept", "apple banana");
        await StoreAsync(run, "removed", "apple banana");

        Assert.True(
            (
                await Vectors.SendAsync(
                    new RemovePassageCommand("removed"),
                    run,
                    TestContext.Current.CancellationToken
                )
            ).IsSuccess
        );

        Assert.Equal(
            ["kept"],
            (await SearchAsync(run, "apple banana", 5)).Select(match => match.Key)
        );
    }

    [Fact]
    public async Task A_host_whose_embedding_model_is_blank_fails_to_start_naming_the_option()
    {
        using var host = VectorFixture.Compose(
            vectors.ConnectionString,
            vectors.Stub,
            null,
            "--PouryaKit:AI:EmbeddingModel= "
        );

        var thrown = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            "PouryaKitAIOptions.EmbeddingModel names no model; set the model the Host embeds text with, or for Azure OpenAI its deployment.",
            Assert.Single(thrown.Failures)
        );
    }

    [Fact]
    public async Task A_host_with_the_chat_client_and_the_embedding_generator_checks_their_one_provider_once()
    {
        using var host = VectorFixture.Compose(
            vectors.ConnectionString,
            vectors.Stub,
            services => services.AddPouryaKitAI(),
            "--PouryaKit:AI:Model=stub",
            "--PouryaKit:AI:Provider=99"
        );

        var thrown = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            "PouryaKitAIOptions.Provider is not an AIProvider value; set OpenAI, AzureOpenAI or Ollama.",
            Assert.Single(thrown.Failures)
        );
    }

    [Fact]
    public async Task An_ollama_model_in_a_container_embeds_text_with_no_hosted_key()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(OllamaProbe) == "1",
            $"The Ollama probe runs only with {OllamaProbe}=1: it pulls {OllamaImage} and the model {OllamaModel}."
        );
        await using var ollama = new OllamaBuilder(OllamaImage).Build();
        await ollama.StartAsync(TestContext.Current.CancellationToken);
        var pulled = await ollama.ExecAsync(
            ["ollama", "pull", OllamaModel],
            TestContext.Current.CancellationToken
        );
        Assert.True(pulled.ExitCode == 0, pulled.Stderr);
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder([
            "--PouryaKit:AI:Provider=Ollama",
            $"--PouryaKit:AI:Endpoint={ollama.GetBaseAddress()}v1",
            $"--PouryaKit:AI:EmbeddingModel={OllamaModel}",
        ]);
        builder.Services.AddPouryaKitEmbeddings();
        builder.Services.Configure<PouryaKitAIOptions>(
            builder.Configuration.GetSection("PouryaKit:AI")
        );
        using var host = builder.Build();

        var embedding = await host
            .Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()
            .GenerateVectorAsync(
                "A lamp for a reading desk.",
                cancellationToken: TestContext.Current.CancellationToken
            );

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{OllamaModel} embedded the text in {embedding.Length} dimensions."
        );
        Assert.Contains(embedding.ToArray(), value => value != 0);
    }

    private async Task StoreAsync(OperationContext run, string key, string text) =>
        Assert.True(
            (
                await Vectors.SendAsync(
                    new StorePassageCommand(key, text),
                    run,
                    TestContext.Current.CancellationToken
                )
            ).IsSuccess
        );

    private async Task<IReadOnlyList<VectorMatch>> SearchAsync(
        OperationContext run,
        string text,
        int top
    )
    {
        var found = await Vectors.QueryAsync(
            new SearchPassagesQuery(text, top),
            run,
            TestContext.Current.CancellationToken
        );
        Assert.True(found.IsSuccess);
        return found.Value;
    }

    // Each test's own tenant, so no test reads another's passages.
    private static string Tenant() => $"tenant-{Guid.CreateVersion7():N}";
}
