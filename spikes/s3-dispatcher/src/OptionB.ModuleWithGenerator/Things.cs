using Mediator;
using Microsoft.Extensions.DependencyInjection;
using OptionB.Pipeline;
using Spike.Common;
using ModuleMediator = global::Mediator.Mediator; // this Module's generated, internal concrete mediator

namespace OptionB.ModuleWithGenerator;

public enum NestedOutcome { Succeeds, HandlerFails, ValidationFails, AuthorizationFails }

// Same Feature set as OptionB.Module, but every dispatch goes through this Module's own generated
// concrete Mediator class. An injected ISender would bind to whichever Module registered first (TryAdd).
public static class ThingsGateway
{
    public static IServiceCollection AddThingsModule(this IServiceCollection services)
    {
        services.AddMediator(options =>
        {
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.GenerateTypesAsInternal = true;
        });
        return services.AddKitPipeline(typeof(ThingsGateway).Assembly);
    }

    public static ValueTask<Result<Guid>> CreateThing(IServiceProvider scope, string name) =>
        scope.GetRequiredService<ModuleMediator>().Send(new CreateThingCommand(name));

    public static ValueTask<Result<string>> GetThing(IServiceProvider scope, Guid id) =>
        scope.GetRequiredService<ModuleMediator>().Send(new GetThingQuery(id));

    public static ValueTask<Result<string>> RunOuter(IServiceProvider scope, NestedOutcome nested) =>
        scope.GetRequiredService<ModuleMediator>().Send(new RunOuterCommand(nested));
}

internal sealed record CreateThingCommand(string Name) : ICommand<Result<Guid>>;

internal sealed class CreateThingAuthorizer(OperationTrace trace) : IAuthorizer<CreateThingCommand>
{
    public Error? Authorize(CreateThingCommand operation)
    {
        trace.Add("authorize CreateThingCommand");
        return null;
    }
}

internal sealed class CreateThingValidator(OperationTrace trace) : IValidator<CreateThingCommand>
{
    public IEnumerable<Error> Validate(CreateThingCommand operation)
    {
        trace.Add("validate CreateThingCommand");
        return operation.Name.Length == 0 ? [new Error(ErrorKind.Validation, "name.required")] : [];
    }
}

internal sealed class CreateThingHandler(OperationTrace trace) : ICommandHandler<CreateThingCommand, Result<Guid>>
{
    public ValueTask<Result<Guid>> Handle(CreateThingCommand command, CancellationToken ct)
    {
        trace.Add("handle CreateThingCommand");
        return ValueTask.FromResult<Result<Guid>>(Guid.CreateVersion7());
    }
}

internal sealed record GetThingQuery(Guid Id) : IQuery<Result<string>>;

internal sealed class GetThingAuthorizer(OperationTrace trace) : IAuthorizer<GetThingQuery>
{
    public Error? Authorize(GetThingQuery operation)
    {
        trace.Add("authorize GetThingQuery");
        return null;
    }
}

internal sealed class GetThingValidator(OperationTrace trace) : IValidator<GetThingQuery>
{
    public IEnumerable<Error> Validate(GetThingQuery operation)
    {
        trace.Add("validate GetThingQuery");
        return [];
    }
}

internal sealed class GetThingHandler(OperationTrace trace) : IQueryHandler<GetThingQuery, Result<string>>
{
    public ValueTask<Result<string>> Handle(GetThingQuery query, CancellationToken ct)
    {
        trace.Add("handle GetThingQuery");
        return ValueTask.FromResult<Result<string>>("thing");
    }
}

internal sealed record RunOuterCommand(NestedOutcome Nested) : ICommand<Result<string>>;

internal sealed class RunOuterHandler(ModuleMediator mediator, OperationTrace trace) : ICommandHandler<RunOuterCommand, Result<string>>
{
    public async ValueTask<Result<string>> Handle(RunOuterCommand command, CancellationToken ct)
    {
        trace.Add("handle RunOuterCommand");
        _ = await mediator.Send(new RunNestedCommand(command.Nested), ct); // deliberately ignored
        return "outer done";
    }
}

internal sealed record RunNestedCommand(NestedOutcome Outcome) : ICommand<Result<string>>;

internal sealed class RunNestedAuthorizer : IAuthorizer<RunNestedCommand>
{
    public Error? Authorize(RunNestedCommand operation) =>
        operation.Outcome == NestedOutcome.AuthorizationFails ? new Error(ErrorKind.Forbidden, "nested.forbidden") : null;
}

internal sealed class RunNestedValidator : IValidator<RunNestedCommand>
{
    public IEnumerable<Error> Validate(RunNestedCommand operation) =>
        operation.Outcome == NestedOutcome.ValidationFails ? [new Error(ErrorKind.Validation, "nested.invalid")] : [];
}

internal sealed class RunNestedHandler(OperationTrace trace) : ICommandHandler<RunNestedCommand, Result<string>>
{
    public ValueTask<Result<string>> Handle(RunNestedCommand command, CancellationToken ct)
    {
        trace.Add("handle RunNestedCommand");
        return ValueTask.FromResult<Result<string>>(command.Outcome == NestedOutcome.HandlerFails
            ? new Error(ErrorKind.Conflict, "nested.failed")
            : "nested done");
    }
}
