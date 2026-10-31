using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>What a test asserts of a failure the Host answers over HTTP: one problem response for each (ADR 0008).</summary>
public static class ProblemResponses
{
    /// <summary>
    /// Asserts that <paramref name="response"/> is a problem response of <paramref name="status"/>, in its status line and
    /// in its body, and returns the body.
    /// </summary>
    public static async Task<JsonElement> ProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status
    )
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken
        );
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        return problem;
    }
}
