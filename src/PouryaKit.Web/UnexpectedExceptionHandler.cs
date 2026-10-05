using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace PouryaKit.Web;

// An exception is a bug or an infrastructure failure, never a business outcome (G.1): the caller gets a 500 problem
// response that names nothing of it, and the log gets all of it. Since .NET 10 the exception handler middleware stops
// logging an exception a handler handled, so this handler logs it. A request the client aborted is not an error: the
// middleware's own aborted-request path answers it with 499 before any handler runs.
internal sealed partial class UnexpectedExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<UnexpectedExceptionHandler> logger
) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        LogUnexpected(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return problemDetails.TryWriteAsync(
            new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = new ProblemDetails { Status = StatusCodes.Status500InternalServerError },
            }
        );
    }

    [LoggerMessage(
        EventName = "UnexpectedException",
        Level = LogLevel.Error,
        Message = "{Method} {Path} failed with an unexpected exception"
    )]
    private static partial void LogUnexpected(ILogger logger, Exception exception, string method, PathString path);
}
