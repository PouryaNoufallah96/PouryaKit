using Microsoft.Extensions.AI;
using PouryaKit.AI;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Modularity;

namespace ModularMonolith.Tests.Stub;

// Runs a test's agents inside an authorized Operation, as a handler runs them, with this Operation's context: each tool
// call of the run continues it.
internal sealed record RunAgentQuery(Func<OperationContext, CancellationToken, Task<string>> Run) : Query<string>;

internal sealed class RunAgentQueryHandler(OperationContext context) : QueryHandler<RunAgentQuery, string>
{
    public override async Task<Result<string>> Handle(RunAgentQuery query, CancellationToken cancellationToken) =>
        await query.Run(context, cancellationToken);
}

// Scheduled agent work: each occurrence of a recurring command runs an agent whose one tool, Stub's own query, answers
// with the Operation Context the call ran with.
internal sealed record RunScheduledAgentCommand(DateTimeOffset Occurrence) : Command;

internal sealed class RunScheduledAgentCommandHandler(IChatClient chat, IGatewayProcessor<StubModule> stub, OperationContext context)
    : CommandHandler<RunScheduledAgentCommand>
{
    public override async Task<Result> Handle(RunScheduledAgentCommand command, CancellationToken cancellationToken)
    {
        var readContext = stub.Tool("ReadOperationContext", "Reads the context this call runs with.", context, (NoArguments _) => new ReadOperationContextQuery());
        await chat.AsAIAgent("You write the morning digest.", tools: [readContext]).RunAsync($"Write the digest of {command.Occurrence:O}.", cancellationToken: cancellationToken);
        return Result.Success();
    }
}

internal sealed record NoArguments;
