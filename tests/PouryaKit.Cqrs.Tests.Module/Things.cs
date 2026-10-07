using PouryaKit.Domain;

namespace PouryaKit.Cqrs.Tests.Module;

public enum NestedOutcome
{
    Succeeds,
    HandlerFails,
    ValidationFails,
    AuthorizationFails,
    Throws,
}

/// <summary>The Module's only public surface; every command, query, handler and authorizer behind it is internal.</summary>
public static class ThingsGateway
{
    public static Task<Result<Guid>> CreateThing(IDispatcher dispatcher) =>
        dispatcher.SendAsync(new CreateThingCommand("Lamp"));

    public static Task<Result<string>> GetThing(IDispatcher dispatcher) =>
        dispatcher.QueryAsync(new GetThingQuery(Guid.CreateVersion7()));

    public static Task<Result> Ping(IDispatcher dispatcher) => dispatcher.SendAsync(new PingCommand());

    public static Task<Result<string>> RunOuter(IDispatcher dispatcher, NestedOutcome nested) =>
        dispatcher.SendAsync(new RunOuterCommand(nested));

    public static Task<Result<string>> RunNested(IDispatcher dispatcher, NestedOutcome outcome) =>
        dispatcher.SendAsync(new RunNestedCommand(outcome));

    public static Task<Result<string>> RunOuterIgnoringAFailedQuery(IDispatcher dispatcher) =>
        dispatcher.SendAsync(new ReadMissingThingCommand());

    public static Task<Result<string>> QuerySendingACommand(IDispatcher dispatcher) =>
        dispatcher.QueryAsync(new SendCommandQuery());

    public static Task<Result> AuthorizeBySendingACommand(IDispatcher dispatcher) =>
        dispatcher.SendAsync(new GuardedByCommandCommand());

    public static Task<Result> NestACommandWhoseAuthorizerSendsACommand(IDispatcher dispatcher) =>
        dispatcher.SendAsync(new SendGuardedByCommandCommand());

    public static Task<Result> AuthorizeByRunningAQuery(IDispatcher dispatcher) =>
        dispatcher.SendAsync(new GuardedByQueryCommand());

    public static Task<Result<string>> RunOuterLettingANestedExceptionThrough(IDispatcher dispatcher) =>
        dispatcher.SendAsync(new RunOuterUncaughtCommand());

    public static Task<Result<string>> SealThing(IDispatcher dispatcher) => dispatcher.SendAsync(new SealThingCommand());
}

internal sealed record GuardedByCommandCommand : Command;

internal sealed class GuardedByCommandAuthorizer(IDispatcher dispatcher) : IAuthorizer<GuardedByCommandCommand>
{
    public async Task<Result> AuthorizeAsync(GuardedByCommandCommand command, CancellationToken cancellationToken) =>
        await dispatcher.SendAsync(new PingCommand(), cancellationToken);
}

internal sealed class GuardedByCommandHandler(OperationTrace trace) : CommandHandler<GuardedByCommandCommand>
{
    public override Task<Result> Handle(GuardedByCommandCommand command, CancellationToken cancellationToken)
    {
        trace.Add("handle GuardedByCommandCommand");
        return Task.FromResult(Result.Success());
    }
}

internal sealed record SendGuardedByCommandCommand : Command;

// Inside the active Unit of Work, the nested command's authorizer must still not send a command.
internal sealed class SendGuardedByCommandHandler(IDispatcher dispatcher) : CommandHandler<SendGuardedByCommandCommand>
{
    public override Task<Result> Handle(SendGuardedByCommandCommand command, CancellationToken cancellationToken) =>
        dispatcher.SendAsync(new GuardedByCommandCommand(), cancellationToken);
}

internal sealed record GuardedByQueryCommand : Command;

// A permission lookup: an authorizer may read through a query before the Unit of Work starts.
internal sealed class GuardedByQueryAuthorizer(IDispatcher dispatcher) : IAuthorizer<GuardedByQueryCommand>
{
    public async Task<Result> AuthorizeAsync(GuardedByQueryCommand command, CancellationToken cancellationToken)
    {
        var thing = await dispatcher.QueryAsync(new GetThingQuery(Guid.CreateVersion7()), cancellationToken);
        return thing.IsSuccess ? Result.Success() : thing.Errors;
    }
}

internal sealed class GuardedByQueryHandler(OperationTrace trace) : CommandHandler<GuardedByQueryCommand>
{
    public override Task<Result> Handle(GuardedByQueryCommand command, CancellationToken cancellationToken)
    {
        trace.Add("handle GuardedByQueryCommand");
        return Task.FromResult(Result.Success());
    }
}

internal sealed record RunOuterUncaughtCommand : Command<string>;

internal sealed class RunOuterUncaughtHandler(IDispatcher dispatcher) : CommandHandler<RunOuterUncaughtCommand, string>
{
    public override async Task<Result<string>> Handle(RunOuterUncaughtCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new RunNestedCommand(NestedOutcome.Throws), cancellationToken);
        return "unreachable";
    }
}

internal sealed record CreateThingCommand(string Name) : Command<Guid>;

internal sealed class CreateThingAuthorizer(OperationTrace trace) : IAuthorizer<CreateThingCommand>
{
    public Task<Result> AuthorizeAsync(CreateThingCommand operation, CancellationToken cancellationToken)
    {
        trace.Add("authorize CreateThingCommand");
        return Task.FromResult(Result.Success());
    }
}

internal sealed class CreateThingHandler(OperationTrace trace) : CommandHandler<CreateThingCommand, Guid>
{
    public override Task<Result<Guid>> Handle(CreateThingCommand command, CancellationToken cancellationToken)
    {
        trace.Add("handle CreateThingCommand");
        return Task.FromResult<Result<Guid>>(Guid.CreateVersion7());
    }
}

internal sealed record GetThingQuery(Guid Id) : Query<string>;

internal sealed class GetThingAuthorizer(OperationTrace trace) : IAuthorizer<GetThingQuery>
{
    public Task<Result> AuthorizeAsync(GetThingQuery operation, CancellationToken cancellationToken)
    {
        trace.Add("authorize GetThingQuery");
        return Task.FromResult(Result.Success());
    }
}

internal sealed class GetThingHandler(OperationTrace trace) : QueryHandler<GetThingQuery, string>
{
    public override Task<Result<string>> Handle(GetThingQuery query, CancellationToken cancellationToken)
    {
        trace.Add("handle GetThingQuery");
        return Task.FromResult<Result<string>>("thing");
    }
}

internal sealed record PingCommand : Command;

internal sealed class PingHandler(OperationTrace trace) : CommandHandler<PingCommand>
{
    public override Task<Result> Handle(PingCommand command, CancellationToken cancellationToken)
    {
        trace.Add("handle PingCommand");
        return Task.FromResult(Result.Success());
    }
}

internal sealed record RunOuterCommand(NestedOutcome Nested) : Command<string>;

internal sealed class RunOuterHandler(IDispatcher dispatcher, OperationTrace trace) : CommandHandler<RunOuterCommand, string>
{
    public override async Task<Result<string>> Handle(RunOuterCommand command, CancellationToken cancellationToken)
    {
        trace.Add("handle RunOuterCommand");
        try
        {
            _ = await dispatcher.SendAsync(new RunNestedCommand(command.Nested), cancellationToken); // ignored on purpose
        }
        catch (InvalidOperationException)
        {
            // swallowed on purpose, like the ignored Result
        }

        return "outer done";
    }
}

internal sealed record RunNestedCommand(NestedOutcome Outcome) : Command<string>, IValidatedOperation
{
    public bool IsValid => Outcome != NestedOutcome.ValidationFails;
}

internal sealed class RunNestedAuthorizer : IAuthorizer<RunNestedCommand>
{
    public Task<Result> AuthorizeAsync(RunNestedCommand operation, CancellationToken cancellationToken) =>
        Task.FromResult<Result>(
            operation.Outcome == NestedOutcome.AuthorizationFails
                ? Error.Forbidden("Nested.AuthorizationFails", "Not allowed.")
                : Result.Success()
        );
}

internal sealed class RunNestedHandler(OperationTrace trace) : CommandHandler<RunNestedCommand, string>
{
    public override Task<Result<string>> Handle(RunNestedCommand command, CancellationToken cancellationToken)
    {
        trace.Add("handle RunNestedCommand");
        return command.Outcome switch
        {
            NestedOutcome.HandlerFails => Task.FromResult<Result<string>>(
                Error.Conflict("Nested.HandlerFails", "The nested command failed.")
            ),
            NestedOutcome.Throws => throw new InvalidOperationException("The nested command threw."),
            _ => Task.FromResult<Result<string>>("nested done"),
        };
    }
}

internal sealed record ReadMissingThingCommand : Command<string>;

internal sealed class ReadMissingThingHandler(IDispatcher dispatcher, OperationTrace trace)
    : CommandHandler<ReadMissingThingCommand, string>
{
    public override async Task<Result<string>> Handle(ReadMissingThingCommand command, CancellationToken cancellationToken)
    {
        trace.Add("handle ReadMissingThingCommand");
        _ = await dispatcher.QueryAsync(new FindMissingThingQuery(), cancellationToken); // its NotFound is ignored on purpose
        return "outer done";
    }
}

internal sealed record FindMissingThingQuery : Query<string>;

internal sealed class FindMissingThingHandler : QueryHandler<FindMissingThingQuery, string>
{
    public override Task<Result<string>> Handle(FindMissingThingQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<string>>(Error.NotFound("Thing.NotFound", "No such thing."));
}

internal sealed record SendCommandQuery : Query<string>;

internal sealed class SendCommandHandler(IDispatcher dispatcher) : QueryHandler<SendCommandQuery, string>
{
    public override async Task<Result<string>> Handle(SendCommandQuery query, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new PingCommand(), cancellationToken);
        return "sent";
    }
}

[Idempotent]
internal sealed record SealThingCommand : Command<string>;

// Sends a nested command that opts in too: only the outermost one reserves a key.
internal sealed class SealThingHandler(IDispatcher dispatcher, OperationTrace trace) : CommandHandler<SealThingCommand, string>
{
    public override async Task<Result<string>> Handle(SealThingCommand command, CancellationToken cancellationToken)
    {
        trace.Add("handle SealThingCommand");
        var part = await dispatcher.SendAsync(new SealPartCommand(), cancellationToken);
        return part.IsSuccess ? "sealed" : part.Errors;
    }
}

[Idempotent]
internal sealed record SealPartCommand : Command;

internal sealed class SealPartHandler(OperationTrace trace) : CommandHandler<SealPartCommand>
{
    public override Task<Result> Handle(SealPartCommand command, CancellationToken cancellationToken)
    {
        trace.Add("handle SealPartCommand");
        return Task.FromResult(Result.Success());
    }
}
