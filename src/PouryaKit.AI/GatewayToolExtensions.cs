using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Modularity;

namespace PouryaKit.AI;

/// <summary>Makes a Module's command or query a Gateway tool: a tool a model calls, which runs as an Operation.</summary>
public static class GatewayToolExtensions
{
    private static readonly JsonSerializerOptions Json = KitJson();

    extension<TModule>(IGatewayProcessor<TModule> gateway)
        where TModule : IModuleDefinition
    {
        /// <summary>
        /// The Gateway tool <paramref name="name"/>, described to the model by <paramref name="description"/>, that reads
        /// the model's arguments into a <typeparamref name="TArguments"/> with the Kit's JSON, whose schema the model is
        /// given, and runs the query <paramref name="query"/> makes of them through this Gateway, as an Operation under
        /// <paramref name="run"/>'s <see cref="OperationContext.Continue"/> for each call: its own Operation id, the run's
        /// as its causation, the run's Actor, tenant and correlation, and no request id. The continuation is trusted only
        /// when the run executes inside the authorized Operation <paramref name="run"/> is the context of, as a handler
        /// does; otherwise the Kit's authorizers check the Actor, as for any new Operation. The answer returns to the
        /// model in the Kit's JSON, and a failed Result as its Errors, never as an exception.
        /// </summary>
        public AIFunction Tool<TArguments, TResponse>(
            string name,
            string description,
            OperationContext run,
            Func<TArguments, Query<TResponse>> query
        )
            where TArguments : notnull
            where TResponse : notnull
        {
            ArgumentNullException.ThrowIfNull(gateway);
            ArgumentNullException.ThrowIfNull(query);
            return Create<TArguments>(
                name,
                description,
                run,
                async (arguments, context, cancellationToken) =>
                    Answer(await gateway.QueryAsync(query(arguments), context, cancellationToken))
            );
        }

        /// <summary>
        /// The Gateway tool <paramref name="name"/> that runs the command <paramref name="command"/> makes of the model's
        /// arguments through this Gateway, as the query overload of <c>Tool</c> runs a query: each call is an Operation of
        /// its own, committed by its own Unit of Work, which no other call of the run replays.
        /// </summary>
        public AIFunction Tool<TArguments>(
            string name,
            string description,
            OperationContext run,
            Func<TArguments, Command> command
        )
            where TArguments : notnull
        {
            ArgumentNullException.ThrowIfNull(gateway);
            ArgumentNullException.ThrowIfNull(command);
            return Create<TArguments>(
                name,
                description,
                run,
                async (arguments, context, cancellationToken) =>
                    Answer(await gateway.SendAsync(command(arguments), context, cancellationToken))
            );
        }

        /// <summary>
        /// The Gateway tool <paramref name="name"/> that runs the command <paramref name="command"/> makes of the model's
        /// arguments through this Gateway, as the query overload of <c>Tool</c> runs a query: each call is an Operation of
        /// its own, committed by its own Unit of Work, which no other call of the run replays.
        /// </summary>
        public AIFunction Tool<TArguments, TResponse>(
            string name,
            string description,
            OperationContext run,
            Func<TArguments, Command<TResponse>> command
        )
            where TArguments : notnull
            where TResponse : notnull
        {
            ArgumentNullException.ThrowIfNull(gateway);
            ArgumentNullException.ThrowIfNull(command);
            return Create<TArguments>(
                name,
                description,
                run,
                async (arguments, context, cancellationToken) =>
                    Answer(await gateway.SendAsync(command(arguments), context, cancellationToken))
            );
        }
    }

    // Continue() runs as the model's call is invoked, inside the run, so it is trusted while the run's Operation runs.
    private static GatewayTool Create<TArguments>(
        string name,
        string description,
        OperationContext run,
        Func<TArguments, OperationContext, CancellationToken, Task<JsonElement?>> send
    )
        where TArguments : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(run);
        return new GatewayTool(
            name,
            description,
            AIJsonUtilities.CreateJsonSchema(typeof(TArguments), serializerOptions: Json),
            Json,
            (arguments, cancellationToken) =>
                send(Read<TArguments>(arguments), run.Continue(), cancellationToken)
        );
    }

    private static TArguments Read<TArguments>(AIFunctionArguments arguments) =>
        JsonSerializer
            .SerializeToElement<IDictionary<string, object?>>(arguments, Json)
            .Deserialize<TArguments>(Json)
        ?? throw new JsonException($"The model's arguments are not a {typeof(TArguments).Name}.");

    private static JsonElement? Answer<TResponse>(Result<TResponse> result)
        where TResponse : notnull =>
        result.IsSuccess
            ? JsonSerializer.SerializeToElement(result.Value, Json)
            : Failed(result.Errors);

    // A command with no value answers nothing, which function invocation tells the model as a completed call.
    private static JsonElement? Answer(Result result) =>
        result.IsSuccess ? null : Failed(result.Errors);

    private static JsonElement Failed(ImmutableArray<Error> errors) =>
        JsonSerializer.SerializeToElement(
            new FailedCall([
                .. errors.Select(error => new FailedCallError(
                    error.Code,
                    error.Message,
                    error.Target
                )),
            ]),
            Json
        );

    private static JsonSerializerOptions KitJson()
    {
        var json = new JsonSerializerOptions().UsePouryaKitJson();
        json.MakeReadOnly(populateMissingResolver: true);
        return json;
    }

    private sealed record FailedCall(IReadOnlyList<FailedCallError> Errors);

    private sealed record FailedCallError(string Code, string Message, string? Target);
}
