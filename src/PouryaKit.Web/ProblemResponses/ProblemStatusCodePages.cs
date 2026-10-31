using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace PouryaKit.Web;

// Routing answers some refusals itself with an empty body, before any endpoint or exception handler runs: 415 for a
// body of a content type the endpoint does not accept, 405 for a method the route does not serve. Status code pages,
// outermost in the pipeline after response compression when that is enabled, write every failure that left the body empty as the same problem response a failed Result
// and an exception get, through ProblemHttpResult, whatever the caller accepts. A response with a body is left alone.
internal sealed class ProblemStatusCodePages : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.UseStatusCodePages(context =>
                TypedResults
                    .Problem(statusCode: context.HttpContext.Response.StatusCode)
                    .ExecuteAsync(context.HttpContext)
            );
            next(app);
        };
}
