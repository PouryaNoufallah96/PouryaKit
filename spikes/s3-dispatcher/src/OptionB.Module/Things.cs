using Mediator;
using Spike.Common;

namespace OptionB.Module;

public enum NestedOutcome { Succeeds, HandlerFails, ValidationFails, AuthorizationFails }

/// <summary>The Module's only public surface; every command, query and handler behind it is internal.</summary>
public static class ThingsGateway
{
    public static ValueTask<Result<Guid>> CreateThing(ISender sender, string name) =>
        sender.Send(new CreateThingCommand(name));

    public static ValueTask<Result<string>> GetThing(ISender sender, Guid id) =>
        sender.Send(new GetThingQuery(id));

    public static ValueTask<Result<string>> RunOuter(ISender sender, NestedOutcome nested) =>
        sender.Send(new RunOuterCommand(nested));
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

internal sealed class RunOuterHandler(ISender sender, OperationTrace trace) : ICommandHandler<RunOuterCommand, Result<string>>
{
    public async ValueTask<Result<string>> Handle(RunOuterCommand command, CancellationToken ct)
    {
        trace.Add("handle RunOuterCommand");
        _ = await sender.Send(new RunNestedCommand(command.Nested), ct); // deliberately ignored
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
