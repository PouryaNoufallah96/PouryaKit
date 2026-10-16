using System.Reflection;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Testing;
using Wolverine;

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

        // Catalog is tenant-scoped, so the question is asked in the lamp's tenant.
        var noted = await Fixture
            .Gateway<StubModule>()
            .SendAsync(new NoteProductCommand(lamp), new OperationContext(GatewayTestExtensions.TestActor, AppFixture.TestTenant), TestContext.Current.CancellationToken);
        var unknown = await Fixture
            .Gateway<StubModule>()
            .SendAsync(new NoteProductCommand(Guid.CreateVersion7()), new OperationContext(GatewayTestExtensions.TestActor, AppFixture.TestTenant), TestContext.Current.CancellationToken);

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
        var asking = new OperationContext(Actor.Person("ada"), tenant: AppFixture.TestTenant);

        await Fixture
            .Gateway<StubModule>()
            .SendAsync(new NoteProductCommand(lamp), asking, TestContext.Current.CancellationToken);

        var answering = Questions.Seen[^1];
        Assert.Equal(asking.Actor, answering.Actor);
        Assert.Equal(asking.Tenant, answering.Tenant);
        Assert.Equal(asking.CorrelationId, answering.CorrelationId);
        Assert.Equal(asking.OperationId, answering.CausationId);
        Assert.NotEqual(asking.OperationId, answering.OperationId);
        // Continued inside the asking Operation, which the Kit ran and authorized.
        Assert.True(answering.IsTrustedContinuation);
    }

    [Fact]
    public void An_undefined_numeric_actor_kind_cannot_continue_as_a_system_actor()
    {
        var context = new OperationContext(Actor.System("review"));
        var envelope = EnvelopeOf(context);
        Assert.Equal(context.Actor, Continued(envelope).Actor);
        envelope.Headers[OperationContextHeaders.ActorKind] = "999";

        var thrown = Assert.Throws<TargetInvocationException>(() => Continued(envelope));

        Assert.Contains("undefined ActorKind", Assert.IsType<InvalidOperationException>(thrown.InnerException).Message);
    }

    // Only the exact names the Outbox writes, as Web reads its own header: never a number, a combination of names or a
    // name with spaces around it, which an enum parse accepts, and never a bare ArgumentException for an unknown name.
    [Theory]
    [InlineData("1")]
    [InlineData("Person, System")]
    [InlineData("Robot")]
    [InlineData(" Person")]
    [InlineData("System ")]
    public void An_actor_kind_header_that_is_not_exactly_person_or_system_is_refused(string kind)
    {
        var envelope = EnvelopeOf(new OperationContext(Actor.Person("ada")));
        envelope.Headers[OperationContextHeaders.ActorKind] = kind;

        var thrown = Assert.Throws<TargetInvocationException>(() => Continued(envelope));

        Assert.Equal(
            $"The message {envelope.Id} has an undefined ActorKind header.",
            Assert.IsType<InvalidOperationException>(thrown.InnerException).Message
        );
    }

    [Fact]
    public void A_person_and_a_system_actor_continue_as_themselves()
    {
        OperationContext person = new(Actor.Person("ada")), system = new(Actor.System("review"));

        Assert.Equal([person.Actor, system.Actor], [Continued(EnvelopeOf(person)).Actor, Continued(EnvelopeOf(system)).Actor]);
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

    private static Envelope EnvelopeOf(OperationContext context)
    {
        var envelope = new Envelope();
        foreach (var (name, value) in new DeliveryOptions().WithOperationContext(context).Headers)
        {
            envelope.Headers[name] = value;
        }

        return envelope;
    }

    // Exercise the transport's context reader without exposing this internal seam to Feature code.
    private static OperationContext Continued(Envelope envelope) =>
        Assert.IsType<(OperationContext Context, System.Diagnostics.ActivityContext TraceParent)>(
            typeof(OperationContextHeaderExtensions)
                .GetMethod("ContinuedOperationContext", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [envelope])
        ).Context;

    private CatalogQuestionContexts Questions => Fixture.Host.Services.GetRequiredService<CatalogQuestionContexts>();
}
