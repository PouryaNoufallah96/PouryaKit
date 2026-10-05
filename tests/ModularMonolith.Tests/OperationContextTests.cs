using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

public sealed class OperationContextTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task The_context_a_caller_passes_to_a_gateway_is_the_context_the_handler_reads()
    {
        var context = new OperationContext(
            Actor.Person("ada"),
            tenant: "acme",
            correlationId: Guid.CreateVersion7(),
            causationId: Guid.CreateVersion7()
        );

        var read = await Fixture
            .Gateway<StubModule>()
            .QueryAsync(new ReadOperationContextQuery(), context, TestContext.Current.CancellationToken);

        Assert.Equal(context, read.Value);
    }

    [Fact]
    public async Task An_operation_with_no_context_fails_instead_of_running_as_a_default_actor()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Fixture
                .Gateway<StubModule>()
                .QueryAsync(new ReadOperationContextQuery(), null!, TestContext.Current.CancellationToken)
        );

        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.QueryAsync(new ReadOperationContextQuery(), TestContext.Current.CancellationToken)
        );
        Assert.Contains("No Operation Context", thrown.Message);
    }

    [Fact]
    public async Task A_module_answers_another_modules_question_through_its_contracts_and_its_gateway()
    {
        var lamp = await CreateLampAsync();
        var questionsBefore = Questions.Seen.Count;

        var noted = await Fixture
            .Gateway<StubModule>()
            .SendAsync(new NoteProductCommand(lamp), TestContext.Current.CancellationToken);
        var unknown = await Fixture
            .Gateway<StubModule>()
            .SendAsync(new NoteProductCommand(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        Assert.True(noted.IsSuccess);
        Assert.Equal(NoteProductCommandHandler.UnknownProduct, Assert.Single(unknown.Errors));
        Assert.Equal("Lamp", await Fixture.ScalarAsync<string>("select text from stub.notes"));
        // Each answer was a Catalog Operation, run through Catalog's Gateway and its pipeline.
        Assert.Equal(questionsBefore + 2, Questions.Seen.Count);
    }

    [Fact]
    public async Task A_question_to_another_module_continues_the_asking_operations_context()
    {
        var lamp = await CreateLampAsync();
        var asking = new OperationContext(Actor.Person("ada"), tenant: "acme");

        await Fixture
            .Gateway<StubModule>()
            .SendAsync(new NoteProductCommand(lamp), asking, TestContext.Current.CancellationToken);

        var answering = Questions.Seen[^1];
        Assert.Equal(asking.Actor, answering.Actor);
        Assert.Equal(asking.Tenant, answering.Tenant);
        Assert.Equal(asking.CorrelationId, answering.CorrelationId);
        Assert.Equal(asking.OperationId, answering.CausationId);
        Assert.NotEqual(asking.OperationId, answering.OperationId);
    }

    private async Task<Guid> CreateLampAsync()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var lamps = Guid.CreateVersion7();
        await catalog.SendAsync(new CreateCategoryCommand(lamps, "Lamps"), TestContext.Current.CancellationToken);
        var lamp = await catalog.SendAsync(
            new CreateProductCommand(Guid.CreateVersion7(), lamps, "Lamp"),
            TestContext.Current.CancellationToken
        );
        return lamp.Value;
    }

    private CatalogQuestionContexts Questions => Fixture.Host.Services.GetRequiredService<CatalogQuestionContexts>();
}
