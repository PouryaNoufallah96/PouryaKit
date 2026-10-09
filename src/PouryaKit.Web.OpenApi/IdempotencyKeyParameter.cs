using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace PouryaKit.Web.OpenApi;

// An operation whose endpoint requires an Idempotency-Key (RequireIdempotencyKey) states it as a required header
// parameter, so a client generated from the document sends it; the endpoint answers 400 without it.
internal sealed class IdempotencyKeyParameter : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IdempotencyKeyRequirement>().Any())
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(
                new OpenApiParameter
                {
                    Name = IdempotencyKeyRequirement.Header,
                    In = ParameterLocation.Header,
                    Required = true,
                    Description =
                        $"A key the client chooses for this request: {IdempotencyKeyRequirement.Rule}. A repeat with the same key answers what the first request answered, without running it again.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                }
            );
        }

        return Task.CompletedTask;
    }
}
