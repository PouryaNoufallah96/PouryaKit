using System.Collections.Concurrent;
using Amazon.Runtime;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModularMonolith.Host;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Reporting;
using PouryaKit.Reporting.Module;
using PouryaKit.Storage;
using PouryaKit.Testing;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>
/// A RustFS store, S3-compatible, and the application started on a database of its own with its files stored in a
/// bucket there, its report kinds those of <see cref="TestReports"/>, a claim that goes stale after
/// <see cref="ClaimTimeout"/> and a retention every second; every file storage call goes through
/// <see cref="RecordingFileStorage"/>. The fixture's Host, on its own database, runs none of these jobs.
/// </summary>
public sealed class ReportFixture(AppFixture app) : IAsyncLifetime
{
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(2);

    private const string Bucket = "reports";
    private const string AccessKey = "kit-access";
    private const string SecretKey = "kit-secret-key";
    private const ushort S3Port = 9000;

    private readonly IContainer _store = new ContainerBuilder("rustfs/rustfs:1.0.2")
        .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
        .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
        .WithPortBinding(S3Port, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(S3Port).ForPath("/health/ready")))
        .Build();

    private WebApplication? _host;
    private string? _connectionString;
    private string? _serviceUrl;

    /// <summary>The started application.</summary>
    public WebApplication Host => _host ?? throw new InvalidOperationException("The Host starts in InitializeAsync.");

    /// <summary>The Host's report kinds.</summary>
    public TestReports Reports { get; } = new();

    /// <summary>The Host's one file storage, which records the keys it signed a link for and each link's lifetime.</summary>
    public RecordingFileStorage Storage => (RecordingFileStorage)Host.Services.GetRequiredService<IFileStorage>();

    public async ValueTask InitializeAsync()
    {
        await _store.StartAsync(TestContext.Current.CancellationToken);
        _serviceUrl = $"http://{_store.Hostname}:{_store.GetMappedPublicPort(S3Port)}";
        using (var client = new AmazonS3Client(
                   new BasicAWSCredentials(AccessKey, SecretKey),
                   new AmazonS3Config { ServiceURL = _serviceUrl, ForcePathStyle = true, AuthenticationRegion = "us-east-1" }
               ))
        {
            await client.PutBucketAsync(Bucket, TestContext.Current.CancellationToken);
        }

        var database = $"reports_{Guid.CreateVersion7():N}";
        await app.RowsAsync($"create database {database}");
        _connectionString = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = database }.ConnectionString;
        _host = Compose();
        await HostFixture.StartHostAsync(_host, TestContext.Current.CancellationToken);
    }

    /// <summary>A Host composed as the fixture's, with <paramref name="arguments"/> after its own, and not started.</summary>
    public WebApplication Compose(params string[] arguments) =>
        AppFixture.ComposeHost(
            "Development",
            _connectionString ?? throw new InvalidOperationException("The database is created in InitializeAsync."),
            new RecordedLogs(),
            builder =>
            {
                builder.Services.Replace(ServiceDescriptor.Singleton<IReportRequestAuthorizer>(Reports));
                builder.Services.Replace(ServiceDescriptor.Singleton<IReportLayouts>(Reports));
                RecordingFileStorage.Install(builder.Services);
            },
            [
                $"--PouryaKit:Storage:S3:Bucket={Bucket}",
                $"--PouryaKit:Storage:S3:ServiceUrl={_serviceUrl}",
                $"--PouryaKit:Storage:S3:AccessKey={AccessKey}",
                $"--PouryaKit:Storage:S3:SecretKey={SecretKey}",
                $"--PouryaKit:Reports:ClaimTimeout={ClaimTimeout}",
                "--PouryaKit:Reports:CleanupInterval=00:00:01",
                .. arguments,
            ]
        );

    /// <summary>A client of the Host's HTTP pipeline, as <see cref="AppFixture.Client"/> makes one.</summary>
    public HttpClient Client(string caller, string tenant, params string[] permissions) =>
        AppFixture.ClientOf(Host, caller, tenant, permissions);

    /// <summary>Every row <paramref name="sql"/> returns on the application's database.</summary>
    public Task<List<object[]>> RowsAsync(string sql) =>
        Sql.RowsAsync(_connectionString ?? throw new InvalidOperationException("The database is created in InitializeAsync."), sql);

    /// <summary>Waits until the SQL <paramref name="condition"/> holds on the application's database; fails after 30 s.</summary>
    public Task WaitUntilAsync(string condition, string because) =>
        Sql.WaitUntilAsync(_connectionString ?? throw new InvalidOperationException("The database is created in InitializeAsync."), condition, because, 30);

    /// <summary>
    /// Holds every delivery of the Module, the retention's included, until the returned hold is disposed: its transaction
    /// locks the Module's Receipt table against writes, and a delivery records its Receipt before its command runs. A
    /// query records none, so the Host still answers reads. Closing the connection rolls the transaction back.
    /// </summary>
    public async Task<IAsyncDisposable> HoldDeliveriesAsync()
    {
        var connection = new NpgsqlConnection(_connectionString ?? throw new InvalidOperationException("The database is created in InitializeAsync."));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var hold = new NpgsqlCommand("lock table reports.pouryakit_receipts in share mode", connection, transaction);
        await hold.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        Reports.ReleaseHeld();
        Reports.ReleaseStalled();
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
            await _store.DisposeAsync();
        }
    }
}

/// <summary>
/// The test Host's report kinds: the sample's <see cref="SalesReports"/>; <see cref="Held"/>, whose first layout waits
/// until <see cref="ReleaseHeld"/>, its table one cell naming the call that laid it out; and <see cref="Stalled"/>, every
/// layout of which waits until <see cref="ReleaseStalled"/>. Any person of a tenant may request the two. It records the
/// context of every request it was asked to authorize and every layout it gave.
/// </summary>
public sealed class TestReports : IReportRequestAuthorizer, IReportLayouts
{
    public const string Held = "held";

    public const string Stalled = "stalled";

    private readonly SalesReports _sales = new();
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseStalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _heldCalls;
    private int _stalledCalls;

    public ConcurrentQueue<OperationContext> Authorized { get; } = [];

    public ConcurrentQueue<OperationContext> LaidOut { get; } = [];

    /// <summary>The number of layouts of <see cref="Held"/> asked for so far.</summary>
    public int HeldCalls => Volatile.Read(ref _heldCalls);

    /// <summary>The number of layouts of <see cref="Stalled"/> asked for so far.</summary>
    public int StalledCalls => Volatile.Read(ref _stalledCalls);

    public void ReleaseHeld() => _release.TrySetResult();

    public void ReleaseStalled() => _releaseStalled.TrySetResult();

    public Task<bool> MayRequestAsync(OperationContext context, ReportRequest request, CancellationToken cancellationToken)
    {
        Authorized.Enqueue(context);
        return request.Kind is Held or Stalled
            ? Task.FromResult(context.Actor.Kind == ActorKind.Person && context.Tenant is not null)
            : _sales.MayRequestAsync(context, request, cancellationToken);
    }

    public async Task<Result<ReportLayout>> LayOutAsync(OperationContext context, ReportRequest request, CancellationToken cancellationToken)
    {
        LaidOut.Enqueue(context);
        if (request.Kind == Stalled)
        {
            Interlocked.Increment(ref _stalledCalls);
            await _releaseStalled.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return HeldLayout(0);
        }

        if (request.Kind != Held)
        {
            return await _sales.LayOutAsync(context, request, cancellationToken);
        }

        var call = Interlocked.Increment(ref _heldCalls);
        if (call == 1)
        {
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }

        return HeldLayout(call);
    }

    /// <summary>The layout the <paramref name="call"/>th layout of <see cref="Held"/> gives.</summary>
    public static ReportLayout HeldLayout(int call) => new("Held", new ReportTable([new("Call")], [[$"call {call}"]]));
}

/// <summary>
/// The Host's one <see cref="IFileStorage"/>, its provider's, recording every key it stores a file under, and every key
/// it signs a link for with the link's lifetime.
/// </summary>
public sealed class RecordingFileStorage(IFileStorage provider) : IFileStorage, IDisposable
{
    public ConcurrentQueue<string> Uploaded { get; } = [];

    public ConcurrentQueue<(string Key, TimeSpan Lifetime)> Signed { get; } = [];

    /// <summary>The provider itself, to read what is stored under a key as no Feature does.</summary>
    public IFileStorage Provider => provider;

    /// <summary>Puts the recording in front of the provider <paramref name="services"/> registered, as its one registration.</summary>
    public static void Install(IServiceCollection services)
    {
        var registered = services.Single(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IFileStorage));
        var factory = registered.ImplementationFactory!;
        services[services.IndexOf(registered)] = ServiceDescriptor.Singleton<IFileStorage>(provider => new RecordingFileStorage((IFileStorage)factory(provider)));
    }

    public async Task<Result> UploadAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var stored = await provider.UploadAsync(key, content, contentType, cancellationToken);
        Uploaded.Enqueue(key);
        return stored;
    }

    public Task<Result<StoredFile>> DownloadAsync(string key, CancellationToken cancellationToken) => provider.DownloadAsync(key, cancellationToken);

    public Task<Result> DeleteAsync(string key, CancellationToken cancellationToken) => provider.DeleteAsync(key, cancellationToken);

    public Task<Result<Uri>> GetPresignedUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        Signed.Enqueue((key, lifetime));
        return provider.GetPresignedUrlAsync(key, lifetime, cancellationToken);
    }

    /// <summary>Disposes the provider, which the container sees only through this registration.</summary>
    public void Dispose() => (provider as IDisposable)?.Dispose();
}
