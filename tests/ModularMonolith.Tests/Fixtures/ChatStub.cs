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
/// last one once they run out. Deterministic, so a test chooses which tools the model asks for and sees what each call
/// sent back.
/// </summary>
public sealed class ChatStub : IAsyncDisposable
{
    private readonly WebApplication _server;
    private readonly Lock _lock = new();
    private readonly List<JsonElement> _requests = [];
    private Queue<ChatReply> _replies = new([ChatReply.Text("No reply was scripted.")]);
    private ChatReply _last = ChatReply.Text("No reply was scripted.");
    private int _calls;

    private ChatStub(WebApplication server) => _server = server;

    /// <summary>The address of the server's API, which a Host's chat client is pointed at.</summary>
    public Uri Endpoint => new($"{_server.Urls.Single()}/v1");

    /// <summary>The body of every chat completions request received since the last <see cref="Script"/>, in order.</summary>
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
        }
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
        ChatReply reply;
        int call;
        lock (_lock)
        {
            _requests.Add(body);
            reply = _replies.TryDequeue(out var next) ? next : _last;
            call = ++_calls;
        }

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
                        ["id"] = $"call_{call}_{index}",
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = tool.Name, ["arguments"] = JsonSerializer.Serialize(tool.Arguments) },
                    }),
                ]
            );
        }

        await http.Response.WriteAsJsonAsync(
            new JsonObject
            {
                ["id"] = $"chatcmpl-{call}",
                ["object"] = "chat.completion",
                ["created"] = 0,
                ["model"] = "stub",
                ["choices"] = new JsonArray(
                    new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = reply.ToolCalls is { Length: > 0 } ? "tool_calls" : "stop" }
                ),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["completion_tokens"] = 1, ["total_tokens"] = 2 },
            },
            http.RequestAborted
        );
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
