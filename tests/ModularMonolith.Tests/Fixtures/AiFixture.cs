using Microsoft.AspNetCore.Builder;
using Npgsql;
using PouryaKit.Testing;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>
/// The <see cref="ChatStub"/> and the application started on a database of its own with its chat client pointed at it:
/// a run there may be given GetProduct, CreateProduct and RemoveProducts, and calls the model at most
/// <see cref="MaximumIterations"/> times with tools.
/// </summary>
public sealed class AiFixture(AppFixture app) : IAsyncLifetime
{
    /// <summary>The iteration cap the application's runs have.</summary>
    public const int MaximumIterations = 3;

    private ChatStub? _stub;
    private WebApplication? _host;

    /// <summary>The model the application's chat client calls.</summary>
    public ChatStub Stub => _stub ?? throw new InvalidOperationException("The stub starts in InitializeAsync.");

    /// <summary>The started application.</summary>
    public WebApplication Host => _host ?? throw new InvalidOperationException("The Host starts in InitializeAsync.");

    /// <summary>The configuration that points a Host's chat client at <paramref name="stub"/>, beside the sample's own.</summary>
    public static string[] StubArguments(ChatStub stub) =>
        [
            $"--PouryaKit:AI:Endpoint={stub.Endpoint}",
            "--PouryaKit:AI:Tools:1=CreateProduct",
            "--PouryaKit:AI:Tools:2=RemoveProducts",
            $"--PouryaKit:AI:MaximumIterationsPerRun={MaximumIterations}",
        ];

    public async ValueTask InitializeAsync()
    {
        _stub = await ChatStub.StartAsync(TestContext.Current.CancellationToken);
        var database = $"ai_{Guid.CreateVersion7():N}";
        await app.RowsAsync($"create database {database}");
        var connectionString = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = database }.ConnectionString;
        _host = AppFixture.ComposeHost("Development", connectionString, new RecordedLogs(), null, StubArguments(_stub));
        await HostFixture.StartHostAsync(_host, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_host is not null)
            {
                await _host.StopAsync(TestContext.Current.CancellationToken);
                await _host.DisposeAsync();
            }
        }
        finally
        {
            if (_stub is not null)
            {
                await _stub.DisposeAsync();
            }
        }
    }
}
