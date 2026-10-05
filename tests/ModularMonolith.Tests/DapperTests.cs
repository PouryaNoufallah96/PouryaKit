using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Errors;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.GetProduct;
using Catalog.Products.Features.ListProducts;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Domain;
using PouryaKit.Persistence.Dapper;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// One typed ID per value type, with no Dapper code of their own: StubModule's AddPouryaKitDapper finds them.
internal sealed record GuidKey(Guid Value) : TypedId<Guid>(Value);

internal sealed record LongKey(long Value) : TypedId<long>(Value);

internal sealed record IntKey(int Value) : TypedId<int>(Value);

internal sealed record StringKey(string Value) : TypedId<string>(Value);

internal sealed record TypedIdRow(GuidKey GuidKey, LongKey LongKey, IntKey IntKey, StringKey StringKey);

public sealed class DapperTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private IReadConnectionFactory Connections => Fixture.Host.Services.GetRequiredService<IReadConnectionFactory>();

    [Fact]
    public async Task Get_and_list_read_what_commands_committed_and_a_missing_product_is_not_found()
    {
        var gateway = Fixture.Gateway<CatalogModule>();
        var furniture = Guid.CreateVersion7();
        var lamp = Guid.CreateVersion7();
        var desk = Guid.CreateVersion7();
        await gateway.SendAsync(new CreateCategoryCommand(furniture, "Furniture"), TestContext.Current.CancellationToken);
        await gateway.SendAsync(new CreateProductCommand(lamp, furniture, "Lamp"), TestContext.Current.CancellationToken);
        await gateway.SendAsync(new CreateProductCommand(desk, furniture, "Desk"), TestContext.Current.CancellationToken);

        var found = await gateway.QueryAsync(new GetProductQuery(lamp), TestContext.Current.CancellationToken);
        var listed = await gateway.QueryAsync(new ListProductsQuery(), TestContext.Current.CancellationToken);
        var missing = await gateway.QueryAsync(
            new GetProductQuery(Guid.CreateVersion7()),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(new ProductDto(lamp, furniture, "Lamp"), found.Value);
        Assert.Equal(
            [(desk, furniture, "Desk"), (lamp, furniture, "Lamp")],
            listed.Value.Select(product => (product.Id.Value, product.CategoryId.Value, product.Name))
        );
        Assert.Equal(ProductErrors.NotFound, Assert.Single(missing.Errors));
    }

    [Fact]
    public async Task Each_read_connection_is_a_new_open_lease_from_the_pool_that_closes_when_its_caller_disposes_it()
    {
        var first = await Connections.OpenAsync(TestContext.Current.CancellationToken);
        var second = await Connections.OpenAsync(TestContext.Current.CancellationToken);
        int[] backends = [await BackendAsync(first), await BackendAsync(second)];
        await first.DisposeAsync();
        await second.DisposeAsync();

        await using var third = await Connections.OpenAsync(TestContext.Current.CancellationToken);

        Assert.NotSame(first, second);
        Assert.NotEqual(backends[0], backends[1]);
        Assert.Equal(System.Data.ConnectionState.Closed, first.State);
        Assert.Equal(System.Data.ConnectionState.Closed, second.State);
        Assert.NotSame(first, third);
        Assert.NotSame(second, third);
        // A disposed lease returns its server connection to the pool, which the next lease reuses.
        Assert.Contains(await BackendAsync(third), backends);
    }

    [Fact]
    public async Task Opening_a_read_connection_with_a_cancelled_token_is_cancelled()
    {
        await using (await Connections.OpenAsync(TestContext.Current.CancellationToken)) { }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Connections.OpenAsync(new CancellationToken(canceled: true))
        );
    }

    [Fact]
    public async Task Typed_ids_of_every_value_type_are_dapper_parameters_and_results_with_no_code_of_their_own()
    {
        // Registered again, as a second Module or a second Host in this process would.
        new ServiceCollection().AddPouryaKitDapper("app", typeof(DapperTests).Assembly);
        var sent = new TypedIdRow(new GuidKey(Guid.CreateVersion7()), new LongKey(5_000_000_000), new IntKey(7), new StringKey("sku-1"));
        await using var connection = await Connections.OpenAsync(TestContext.Current.CancellationToken);

        var types = await connection.QuerySingleAsync<(string, string, string, string)>(
            new CommandDefinition(
                "select pg_typeof(@GuidKey)::text, pg_typeof(@LongKey)::text, pg_typeof(@IntKey)::text, pg_typeof(@StringKey)::text",
                sent,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
        var read = await connection.QuerySingleAsync<TypedIdRow>(
            new CommandDefinition(
                "select @GuidKey as guidkey, @LongKey as longkey, @IntKey as intkey, @StringKey as stringkey",
                sent,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(("uuid", "bigint", "integer", "text"), types);
        Assert.Equal(sent, read);
    }

    [Fact]
    public async Task A_dapper_write_on_the_unit_of_work_connection_commits_with_the_operation_and_rolls_back_with_it()
    {
        var gateway = Fixture.Gateway<StubModule>();

        var kept = await gateway.SendAsync(new WriteTalliedNoteCommand("kept", Reject: false), TestContext.Current.CancellationToken);
        var rejected = await gateway.SendAsync(
            new WriteTalliedNoteCommand("rejected", Reject: true),
            TestContext.Current.CancellationToken
        );
        var keptAgainThenRejected = await gateway.SendAsync(
            new WriteTalliedNoteCommand("kept", Reject: true),
            TestContext.Current.CancellationToken
        );

        Assert.True(kept.IsSuccess);
        Assert.Equal(WriteTalliedNoteCommandHandler.Rejected, Assert.Single(rejected.Errors));
        Assert.Equal(WriteTalliedNoteCommandHandler.Rejected, Assert.Single(keptAgainThenRejected.Errors));
        Assert.Equal([["kept", 1]], await Fixture.RowsAsync("select text, count from stub.note_tallies"));
        Assert.Equal([["kept"]], await Fixture.RowsAsync("select text from stub.notes"));
    }

    [Fact]
    public async Task A_query_cannot_join_the_unit_of_work_connection()
    {
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture
                .Gateway<StubModule>()
                .QueryAsync(new TallyNoteInQueryQuery("read"), TestContext.Current.CancellationToken)
        );

        Assert.StartsWith("A query, an authorizer or a validator asked to write", thrown.Message);
    }

    [Fact]
    public async Task A_query_sent_from_a_command_cannot_join_the_commands_unit_of_work_connection()
    {
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture
                .Gateway<StubModule>()
                .SendAsync(new TallyNoteThroughQueryCommand("nested"), TestContext.Current.CancellationToken)
        );

        Assert.StartsWith("A query, an authorizer or a validator asked to write", thrown.Message);
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.note_tallies"));
    }

    [Fact]
    public void Dapper_reads_in_one_host_cannot_name_a_second_connection()
    {
        var services = new ServiceCollection().AddPouryaKitDapper("app");

        var thrown = Assert.Throws<InvalidOperationException>(() => services.AddPouryaKitDapper("reports"));

        Assert.Contains("'app'", thrown.Message);
        Assert.Contains("'reports'", thrown.Message);
    }

    private static Task<int> BackendAsync(System.Data.Common.DbConnection connection) =>
        connection.ExecuteScalarAsync<int>(
            new CommandDefinition("select pg_backend_pid()", cancellationToken: TestContext.Current.CancellationToken)
        );
}
