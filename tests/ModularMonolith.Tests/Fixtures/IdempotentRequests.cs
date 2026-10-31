using System.Net.Http.Json;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>Requests to an endpoint that requires an <c>Idempotency-Key</c>, as its clients send them.</summary>
internal static class IdempotentRequests
{
    public const string Header = "Idempotency-Key";

    extension(HttpClient client)
    {
        /// <summary>POSTs <paramref name="value"/> as JSON with <paramref name="key"/>, a new one unless repeating a request.</summary>
        public async Task<HttpResponseMessage> PostAsJsonAsync<T>(
            string uri,
            T value,
            string key,
            CancellationToken cancellationToken
        )
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(value),
            };
            request.Headers.Add(Header, key);
            return await client.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>A key no other request has used.</summary>
    public static string NewKey() => Guid.CreateVersion7().ToString();
}
