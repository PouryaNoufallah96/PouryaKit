using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>
/// An OpenAI-compatible chat completions server in the test process, on a loopback port, standing in for a model: it
/// records the body of every request it receives and answers each with the next reply a test scripted, repeating the
/// last one once they run out, as server-sent chunks when the request asks for a stream. Deterministic, so a test chooses
/// which tools the model asks for and sees what each call sent back. It embeds text too, as <see cref="Embed"/> does.
/// </summary>
public sealed class ChatStub : IAsyncDisposable
{
    private readonly WebApplication _server;
    private readonly Lock _lock = new();
    private readonly List<JsonElement> _requests = [];
    private Queue<ChatReply> _replies = new([ChatReply.Text("No reply was scripted.")]);
    private ChatReply _last = ChatReply.Text("No reply was scripted.");
    private Func<JsonElement, ChatReply>? _answer;
    private int _calls;

    private ChatStub(WebApplication server) => _server = server;

    /// <summary>The dimensions of every embedding the server returns.</summary>
    public const int Dimensions = 8;

    /// <summary>The address of the server's API, which a Host's chat client is pointed at.</summary>
    public Uri Endpoint => new($"{_server.Urls.Single()}/v1");

    /// <summary>The body of every chat completions request received since the last <c>Script</c>, in order.</summary>
    public IReadOnlyList<JsonElement> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Starts the server on a free loopback port.</summary>
    public static async Task<ChatStub> StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var server = builder.Build();
        var stub = new ChatStub(server);
        server.MapPost("/v1/chat/completions", stub.AnswerAsync);
        server.MapPost("/v1/embeddings", EmbedAsync);
        await server.StartAsync(cancellationToken);
        return stub;
    }

    /// <summary>Forgets every request received and answers the next ones with <paramref name="replies"/>, in order.</summary>
    public void Script(params ChatReply[] replies)
    {
        lock (_lock)
        {
            _requests.Clear();
            _replies = new(replies);
            _last = replies[^1];
            _answer = null;
        }
    }

    /// <summary>
    /// Forgets every request received and answers each next one with what <paramref name="answer"/> makes of its body, as
    /// runs that call the model at once need: their requests arrive in no set order.
    /// </summary>
    public void Script(Func<JsonElement, ChatReply> answer)
    {
        lock (_lock)
        {
            _requests.Clear();
            _answer = answer;
        }
    }

    /// <summary>
    /// The embedding of <paramref name="text"/>: how many of its words begin with each group of letters, a, i, q and y
    /// counting in the first dimension, b, j, r and z in the second, and so on to h, p and x in the eighth. Texts whose
    /// words begin alike point the same way.
    /// </summary>
    public static float[] Embed(string text)
    {
        var embedding = new float[Dimensions];
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            embedding[(char.ToLowerInvariant(word[0]) - 'a') % Dimensions]++;
        }

        return embedding;
    }

    /// <summary>The content of every tool message of the request at <paramref name="index"/>: what each tool call returned.</summary>
    public IReadOnlyList<string> ToolResultsOf(int index) =>
        [
            .. Requests[index]
                .GetProperty("messages")
                .EnumerateArray()
                .Where(message => message.GetProperty("role").GetString() == "tool")
                .Select(message => message.GetProperty("content").GetString()!),
        ];

    /// <summary>The names of the tools the request at <paramref name="index"/> offered the model.</summary>
    public IReadOnlyList<string> ToolsOf(int index) =>
        Requests[index].TryGetProperty("tools", out var tools)
            ? [.. tools.EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()!)]
            : [];

    private async Task AnswerAsync(HttpContext http)
    {
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, cancellationToken: http.RequestAborted);
        Func<JsonElement, ChatReply>? answer;
        ChatReply? scripted;
        int call;
        lock (_lock)
        {
            _requests.Add(body);
            answer = _answer;
            scripted = answer is null ? (_replies.TryDequeue(out var next) ? next : _last) : null;
            call = ++_calls;
        }

        // Outside the lock, so an answer that holds one request, as a test of two runs at once does, holds no other.
        var reply = scripted ?? answer!(body);

        http.Response.StatusCode = reply.Status;
        if (reply.Status != StatusCodes.Status200OK)
        {
            http.Response.Headers.RetryAfter = "0";
            return;
        }

        var message = new JsonObject { ["role"] = "assistant", ["content"] = reply.Content };
        if (reply.ToolCalls is { Length: > 0 } calls)
        {
            message["tool_calls"] = new JsonArray(
                [
                    .. calls.Select((tool, index) => new JsonObject
                    {
                        ["index"] = index,
                        ["id"] = $"call_{call}_{index}",
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = tool.Name, ["arguments"] = JsonSerializer.Serialize(tool.Arguments) },
                    }),
                ]
            );
        }

        var finish = reply.ToolCalls is { Length: > 0 } ? "tool_calls" : "stop";
        if (body.TryGetProperty("stream", out var stream) && stream.GetBoolean())
        {
            await StreamAsync(http, call, message, finish);
            return;
        }

        await http.Response.WriteAsJsonAsync(
            new JsonObject
            {
                ["id"] = $"chatcmpl-{call}",
                ["object"] = "chat.completion",
                ["created"] = 0,
                ["model"] = "stub",
                ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = finish }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["completion_tokens"] = 1, ["total_tokens"] = 2 },
            },
            http.RequestAborted
        );
    }

    // The embeddings API: one embedding per input, as floats or, as the OpenAI client asks by default, as base64 of their
    // little-endian bytes.
    private static async Task EmbedAsync(HttpContext http)
    {
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, cancellationToken: http.RequestAborted);
        var input = body.GetProperty("input");
        string[] texts = input.ValueKind == JsonValueKind.Array ? [.. input.EnumerateArray().Select(text => text.GetString()!)] : [input.GetString()!];
        var base64 = body.TryGetProperty("encoding_format", out var format) && format.GetString() == "base64";
        await http.Response.WriteAsJsonAsync(
            new JsonObject
            {
                ["object"] = "list",
                ["data"] = new JsonArray(
                    [
                        .. texts.Select((text, index) =>
                        {
                            var embedding = Embed(text);
                            return new JsonObject
                            {
                                ["object"] = "embedding",
                                ["index"] = index,
                                ["embedding"] = base64
                                    ? JsonValue.Create(Convert.ToBase64String(MemoryMarshal.AsBytes(embedding.AsSpan())))
                                    : new JsonArray([.. embedding.Select(value => JsonValue.Create(value))]),
                            };
                        }),
                    ]
                ),
                ["model"] = "stub",
                ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["total_tokens"] = 1 },
            },
            http.RequestAborted
        );
    }

    // A streamed answer as server-sent chunks: the text in two halves, so a run sees it arrive in parts, then the tool calls
    // and the finish reason, then the end of the stream.
    private static async Task StreamAsync(HttpContext http, int call, JsonObject message, string finish)
    {
        http.Response.ContentType = "text/event-stream";
        var content = message["content"]?.GetValue<string>();
        List<JsonObject> deltas = content is { Length: > 1 }
            ? [new() { ["role"] = "assistant", ["content"] = content[..(content.Length / 2)] }, new() { ["content"] = content[(content.Length / 2)..] }]
            : [new() { ["role"] = "assistant", ["content"] = content }];
        if (message["tool_calls"] is JsonArray calls)
        {
            deltas.Add(new() { ["tool_calls"] = calls.DeepClone() });
        }

        foreach (var (delta, last) in deltas.Select((delta, index) => (delta, index == deltas.Count - 1)))
        {
            await WriteChunkAsync(http, call, delta, last ? finish : null);
        }

        await http.Response.WriteAsync("data: [DONE]\n\n", http.RequestAborted);
    }

    private static async Task WriteChunkAsync(HttpContext http, int call, JsonObject delta, string? finish)
    {
        var chunk = new JsonObject
        {
            ["id"] = $"chatcmpl-{call}",
            ["object"] = "chat.completion.chunk",
            ["created"] = 0,
            ["model"] = "stub",
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }),
        };
        await http.Response.WriteAsync($"data: {chunk.ToJsonString()}\n\n", http.RequestAborted);
        await http.Response.Body.FlushAsync(http.RequestAborted);
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();
}

/// <summary>One answer of the <see cref="ChatStub"/>: text, tool calls, or an HTTP failure.</summary>
public sealed record ChatReply(int Status, string? Content, ToolCall[]? ToolCalls)
{
    /// <summary>The model answers with <paramref name="text"/>, which ends the run.</summary>
    public static ChatReply Text(string text) => new(StatusCodes.Status200OK, text, null);

    /// <summary>The model asks for <paramref name="calls"/>, in one turn.</summary>
    public static ChatReply Calls(params ToolCall[] calls) => new(StatusCodes.Status200OK, null, calls);

    /// <summary>The server fails with <paramref name="status"/>, telling the client it may retry at once.</summary>
    public static ChatReply Failure(int status) => new(status, null, null);
}

/// <summary>A call of the tool <paramref name="Name"/> with <paramref name="Arguments"/>, written as JSON.</summary>
public sealed record ToolCall(string Name, object Arguments);
