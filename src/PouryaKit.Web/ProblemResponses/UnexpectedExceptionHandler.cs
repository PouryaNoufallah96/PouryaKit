using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PouryaKit.Web;

// An exception is a bug or an infrastructure failure, never a business outcome (G.1): the caller gets a 500 problem
// response that names nothing of it, and the log gets all of it. The one exception that is the caller's mistake is a
// request that could not be read: it answers its own status as a problem response, with nothing logged as an error. When
// the Kit refused it (KitBadRequestException), the problem's detail is the rule the request broke; when ASP.NET Core did
// (malformed JSON, a value that does not parse), it has no detail, because ASP.NET Core's message quotes what the caller
// sent. Both are written as a failed Result is, through ProblemHttpResult, which falls back to application/problem+json
// when the caller's Accept rules out every problem details writer.
//
// The Error entry here is the HTTP-level record of the request that failed. The dispatcher also logs an exception a
// handler throws, but an exception raised outside an Operation (authentication, binding, an endpoint) has only this
// one, and since .NET 10 the exception handler middleware no longer logs an exception a handler handled. A request the
// client aborted is not an error: the middleware's own aborted-request path answers it with 499 before any handler runs.
internal sealed partial class UnexpectedExceptionHandler(ILogger<UnexpectedExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        int status;
        string? detail = null;
        if (exception is BadHttpRequestException badRequest)
        {
            LogBadRequest(
                logger,
                badRequest,
                httpContext.Request.Method,
                httpContext.Request.Path,
                badRequest.StatusCode
            );
            status = badRequest.StatusCode;
            detail = (badRequest as KitBadRequestException)?.Message;
        }
        else
        {
            LogUnexpected(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
            status = StatusCodes.Status500InternalServerError;
        }

        await TypedResults.Problem(detail, statusCode: status).ExecuteAsync(httpContext);
        return true;
    }

    [LoggerMessage(
        EventName = "UnexpectedException",
        Level = LogLevel.Error,
        Message = "{Method} {Path} failed with an unexpected exception"
    )]
    private static partial void LogUnexpected(
        ILogger logger,
        Exception exception,
        string method,
        PathString path
    );

    [LoggerMessage(
        EventName = "BadRequest",
        Level = LogLevel.Debug,
        Message = "{Method} {Path} was refused with {StatusCode}: the request could not be read"
    )]
    private static partial void LogBadRequest(
        ILogger logger,
        Exception exception,
        string method,
        PathString path,
        int statusCode
    );
}
