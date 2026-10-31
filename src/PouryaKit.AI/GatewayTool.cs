using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PouryaKit.AI;

// A Module's command or query offered to a model, as GatewayToolExtensions builds it: the model's arguments, read with
// the Kit's JSON against the schema of the tool's arguments type, go to the Gateway on each call.
internal sealed class GatewayTool(
    string name,
    string description,
    JsonElement schema,
    JsonSerializerOptions json,
    Func<AIFunctionArguments, CancellationToken, Task<JsonElement?>> call
) : AIFunction
{
    public override string Name => name;

    public override string Description => description;

    public override JsonElement JsonSchema => schema;

    public override JsonSerializerOptions JsonSerializerOptions => json;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken
    ) => await call(arguments, cancellationToken);
}
