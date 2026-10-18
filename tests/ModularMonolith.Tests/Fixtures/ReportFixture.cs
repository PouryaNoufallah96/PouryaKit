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

    /// <summary>The started application.</summary>
    public WebApplication Host => _host ?? throw new InvalidOperationException("The Host starts in InitializeAsync.");

    /// <summary>The Host's report kinds.</summary>
    public TestReports Reports { get; } = new();

    /// <summary>The Host's one file storage, which records the keys it signed a link for.</summary>
    public RecordingFileStorage Storage => (RecordingFileStorage)Host.Services.GetRequiredService<IFileStorage>();

    public async ValueTask InitializeAsync()
    {
        await _store.StartAsync(TestContext.Current.CancellationToken);
        var serviceUrl = $"http://{_store.Hostname}:{_store.GetMappedPublicPort(S3Port)}";
        using (var client = new AmazonS3Client(
                   new BasicAWSCredentials(AccessKey, SecretKey),
                   new AmazonS3Config { ServiceURL = serviceUrl, ForcePathStyle = true, AuthenticationRegion = "us-east-1" }
               ))
        {
            await client.PutBucketAsync(Bucket, TestContext.Current.CancellationToken);
        }

        var database = $"reports_{Guid.CreateVersion7():N}";
        await app.RowsAsync($"create database {database}");
        _connectionString = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = database }.ConnectionString;
        _host = AppFixture.ComposeHost(
            "Development",
            _connectionString,
            new RecordedLogs(),
            builder =>
            {
                builder.Services.Replace(ServiceDescriptor.Singleton<IReportRequestAuthorizer>(Reports));
                builder.Services.Replace(ServiceDescriptor.Singleton<IReportLayouts>(Reports));
                RecordingFileStorage.Install(builder.Services);
            },
            $"--PouryaKit:Storage:S3:Bucket={Bucket}",
            $"--PouryaKit:Storage:S3:ServiceUrl={serviceUrl}",
            $"--PouryaKit:Storage:S3:AccessKey={AccessKey}",
            $"--PouryaKit:Storage:S3:SecretKey={SecretKey}",
            $"--PouryaKit:Reports:ClaimTimeout={ClaimTimeout}",
            "--PouryaKit:Reports:CleanupInterval=00:00:01"
        );
        await HostFixture.StartHostAsync(_host, TestContext.Current.CancellationToken);
    }

    /// <summary>A client of the Host's HTTP pipeline, as <see cref="AppFixture.Client"/> makes one.</summary>
    public HttpClient Client(string caller, string tenant, params string[] permissions) =>
        AppFixture.ClientOf(Host, caller, tenant, permissions);

    /// <summary>Every row <paramref name="sql"/> returns on the application's database.</summary>
    public Task<List<object[]>> RowsAsync(string sql) =>
        Sql.RowsAsync(_connectionString ?? throw new InvalidOperationException("The database is created in InitializeAsync."), sql);

    /// <summary>Waits until the SQL <paramref name="condition"/> holds on the application's database; fails after 30 s.</summary>
    public Task WaitUntilAsync(string condition, string because) =>
        Sql.WaitUntilAsync(_connectionString ?? throw new InvalidOperationException("The database is created in InitializeAsync."), condition, because, 30);

    public async ValueTask DisposeAsync()
    {
        Reports.ReleaseHeld();
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
/// The test Host's report kinds: the sample's <see cref="SalesReports"/>, and <see cref="Held"/>, which any person of a
/// tenant may request and whose first layout waits until <see cref="ReleaseHeld"/>, its table one cell naming the call
/// that laid it out. It records the context of every request it was asked to authorize and every layout it gave.
/// </summary>
public sealed class TestReports : IReportRequestAuthorizer, IReportLayouts
{
    public const string Held = "held";

    private readonly SalesReports _sales = new();
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _heldCalls;

    public ConcurrentQueue<OperationContext> Authorized { get; } = [];

    public ConcurrentQueue<OperationContext> LaidOut { get; } = [];

    /// <summary>The number of layouts of <see cref="Held"/> asked for so far.</summary>
    public int HeldCalls => Volatile.Read(ref _heldCalls);

    public void ReleaseHeld() => _release.TrySetResult();

    public Task<bool> MayRequestAsync(OperationContext context, ReportRequest request, CancellationToken cancellationToken)
    {
        Authorized.Enqueue(context);
        return request.Kind == Held
            ? Task.FromResult(context.Actor.Kind == ActorKind.Person && context.Tenant is not null)
            : _sales.MayRequestAsync(context, request, cancellationToken);
    }

    public async Task<Result<ReportLayout>> LayOutAsync(OperationContext context, ReportRequest request, CancellationToken cancellationToken)
    {
        LaidOut.Enqueue(context);
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

/// <summary>The Host's one <see cref="IFileStorage"/>, its provider's, recording every key it signs a link for.</summary>
public sealed class RecordingFileStorage(IFileStorage provider) : IFileStorage
{
    public ConcurrentQueue<string> Signed { get; } = [];

    /// <summary>The provider itself, to read what is stored under a key as no Feature does.</summary>
    public IFileStorage Provider => provider;

    /// <summary>Puts the recording in front of the provider <paramref name="services"/> registered, as its one registration.</summary>
    public static void Install(IServiceCollection services)
    {
        var registered = services.Single(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IFileStorage));
        var factory = registered.ImplementationFactory!;
        services[services.IndexOf(registered)] = ServiceDescriptor.Singleton<IFileStorage>(provider => new RecordingFileStorage((IFileStorage)factory(provider)));
    }

    public Task<Result> UploadAsync(string key, Stream content, string contentType, CancellationToken cancellationToken) =>
        provider.UploadAsync(key, content, contentType, cancellationToken);

    public Task<Result<StoredFile>> DownloadAsync(string key, CancellationToken cancellationToken) => provider.DownloadAsync(key, cancellationToken);

    public Task<Result> DeleteAsync(string key, CancellationToken cancellationToken) => provider.DeleteAsync(key, cancellationToken);

    public Task<Result<Uri>> GetPresignedUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        Signed.Enqueue(key);
        return provider.GetPresignedUrlAsync(key, lifetime, cancellationToken);
    }
}
