using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ModularMonolith.Tests.McpServer;

/// <summary>The tools of the server over stdio.</summary>
[McpServerToolType]
public static class StdioTools
{
    /// <summary>The server process's id, so a test sees which process to watch.</summary>
    [McpServerTool(Name = "process_id"), Description("Gives the id of the server's process.")]
    public static int ProcessId() => Environment.ProcessId;

    /// <summary>The text reversed.</summary>
    [McpServerTool(Name = "reverse"), Description("Writes a text backwards.")]
    public static string Reverse([Description("The text to reverse.")] string text) =>
        string.Concat(text.Reverse());

    /// <summary>A variable of the server's environment, or an empty text when it has none.</summary>
    [
        McpServerTool(Name = "read_variable"),
        Description("Reads a variable of the server's environment.")
    ]
    public static string ReadVariable([Description("The variable's name.")] string name) =>
        Environment.GetEnvironmentVariable(name) ?? "";
}

/// <summary>The tools of the server over HTTP, each recording its call on the server.</summary>
[McpServerToolType]
public static class HttpTools
{
    /// <summary>The text in capitals.</summary>
    [McpServerTool(Name = "upper"), Description("Writes a text in capitals.")]
    public static string Upper(
        McpCalls calls,
        [Description("The text to write in capitals.")] string text
    )
    {
        calls.Add("upper");
        return text.ToUpperInvariant();
    }

    /// <summary>A tool no allow-list of the tests names.</summary>
    [McpServerTool(Name = "erase"), Description("Erases every record.")]
    public static string Erase(McpCalls calls)
    {
        calls.Add("erase");
        return "Erased.";
    }
}

/// <summary>The names of the tools the server over HTTP ran, in order.</summary>
public sealed class McpCalls
{
    private readonly Lock _lock = new();
    private readonly List<string> _calls = [];

    /// <summary>Every call so far.</summary>
    public IReadOnlyList<string> All
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Records a call of <paramref name="tool"/>.</summary>
    public void Add(string tool)
    {
        lock (_lock)
        {
            _calls.Add(tool);
        }
    }
}
