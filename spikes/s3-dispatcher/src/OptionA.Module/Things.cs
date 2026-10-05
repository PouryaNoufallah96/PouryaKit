using OptionA.Dispatcher;
using Spike.Common;

namespace OptionA.Module;

public enum NestedOutcome { Succeeds, HandlerFails, ValidationFails, AuthorizationFails }

/// <summary>The Module's only public surface; every command, query and handler behind it is internal.</summary>
public static class ThingsGateway
{
    public static Task<Result<Guid>> CreateThing(IDispatcher dispatcher, string name) =>
        dispatcher.Send(new CreateThingCommand(name));

    public static Task<Result<string>> GetThing(IDispatcher dispatcher, Guid id) =>
        dispatcher.Query(new GetThingQuery(id));

    public static Task<Result<string>> RunOuter(IDispatcher dispatcher, NestedOutcome nested) =>
        dispatcher.Send(new RunOuterCommand(nested));
}

internal sealed record CreateThingCommand(string Name) : ICommand<Guid>;

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

internal sealed class CreateThingHandler(OperationTrace trace) : ICommandHandler<CreateThingCommand, Guid>
{
    public Task<Result<Guid>> Handle(CreateThingCommand command, CancellationToken ct)
    {
        trace.Add("handle CreateThingCommand");
        return Task.FromResult<Result<Guid>>(Guid.CreateVersion7());
    }
}

internal sealed record GetThingQuery(Guid Id) : IQuery<string>;

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

internal sealed class GetThingHandler(OperationTrace trace) : IQueryHandler<GetThingQuery, string>
{
    public Task<Result<string>> Handle(GetThingQuery query, CancellationToken ct)
    {
        trace.Add("handle GetThingQuery");
        return Task.FromResult<Result<string>>("thing");
    }
}

internal sealed record RunOuterCommand(NestedOutcome Nested) : ICommand<string>;

internal sealed class RunOuterHandler(IDispatcher dispatcher, OperationTrace trace) : ICommandHandler<RunOuterCommand, string>
{
    public async Task<Result<string>> Handle(RunOuterCommand command, CancellationToken ct)
    {
        trace.Add("handle RunOuterCommand");
        _ = await dispatcher.Send(new RunNestedCommand(command.Nested), ct); // deliberately ignored
        return "outer done";
    }
}

internal sealed record RunNestedCommand(NestedOutcome Outcome) : ICommand<string>;

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

internal sealed class RunNestedHandler(OperationTrace trace) : ICommandHandler<RunNestedCommand, string>
{
    public Task<Result<string>> Handle(RunNestedCommand command, CancellationToken ct)
    {
        trace.Add("handle RunNestedCommand");
        return Task.FromResult<Result<string>>(command.Outcome == NestedOutcome.HandlerFails
            ? new Error(ErrorKind.Conflict, "nested.failed")
            : "nested done");
    }
}
