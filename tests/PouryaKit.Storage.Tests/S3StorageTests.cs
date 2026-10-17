using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PouryaKit.Domain;
using PouryaKit.Storage.S3;

namespace PouryaKit.Storage.Tests;

// The S3 provider against a real S3-compatible store, RustFS in Testcontainers, through the contract a Feature uses.
public sealed class S3StorageTests(S3StoreFixture fixture) : IClassFixture<S3StoreFixture>
{
    private static readonly byte[] Report = Encoding.UTF8.GetBytes("%PDF-1.7 quarterly report");

    [Fact]
    public async Task An_uploaded_file_downloads_with_its_content_and_content_type()
    {
        var key = Key();
        Assert.True((await fixture.Storage.UploadAsync(key, new MemoryStream(Report), "application/pdf", Cancel)).IsSuccess);

        var download = await fixture.Storage.DownloadAsync(key, Cancel);

        await using var file = download.Value;
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal(Report, await ReadAll(file.Content));
    }

    [Fact]
    public async Task An_upload_stores_the_stream_from_its_current_position_and_leaves_it_open()
    {
        var key = Key();
        var content = new MemoryStream(Report) { Position = 9 };

        await fixture.Storage.UploadAsync(key, content, "application/pdf", Cancel);

        await using var file = (await fixture.Storage.DownloadAsync(key, Cancel)).Value;
        Assert.Equal(Report[9..], await ReadAll(file.Content));
        Assert.True(content.CanRead);
    }

    [Fact]
    public async Task A_presigned_url_requested_with_a_cancelled_token_is_not_returned()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Storage.GetPresignedUrlAsync(Key(), TimeSpan.FromMinutes(5), cancelled.Token));
    }

    [Fact]
    public async Task A_deleted_file_downloads_as_the_not_found_error_and_deleting_it_again_succeeds()
    {
        var key = Key();
        await fixture.Storage.UploadAsync(key, new MemoryStream(Report), "application/pdf", Cancel);

        Assert.True((await fixture.Storage.DeleteAsync(key, Cancel)).IsSuccess);
        var download = await fixture.Storage.DownloadAsync(key, Cancel);

        Assert.Equal(StorageErrors.FileNotFound, Assert.Single(download.Errors));
        Assert.Equal(ErrorKind.NotFound, download.Errors[0].Kind);
        Assert.True((await fixture.Storage.DeleteAsync(key, Cancel)).IsSuccess);
    }

    [Fact]
    public async Task A_presigned_url_reads_the_file_without_credentials_until_it_expires()
    {
        var key = Key();
        await fixture.Storage.UploadAsync(key, new MemoryStream(Report), "application/pdf", Cancel);

        var link = await fixture.Storage.GetPresignedUrlAsync(key, TimeSpan.FromMinutes(5), Cancel);
        var expiring = await fixture.Storage.GetPresignedUrlAsync(key, TimeSpan.FromSeconds(1), Cancel);

        using var http = new HttpClient();
        using var response = await http.GetAsync(link.Value, Cancel);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Cancel)}");
        Assert.Equal(Report, await response.Content.ReadAsByteArrayAsync(Cancel));
        await Task.Delay(TimeSpan.FromSeconds(3), Cancel);
        using var expired = await http.GetAsync(expiring.Value, Cancel);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, expired.StatusCode);
    }

    [Fact]
    public async Task A_host_with_invalid_storage_options_fails_to_start_naming_each_problem()
    {
        using var host = S3StoreFixture.ComposeHost(new() { ["PouryaKit:Storage:S3:ServiceUrl"] = "minio:9000", ["PouryaKit:Storage:S3:AccessKey"] = "kit" });

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Cancel));

        Assert.Equal(
            [
                "PouryaKitS3StorageOptions.Bucket names no bucket to store files in.",
                "PouryaKitS3StorageOptions.Region names no region to reach or sign for.",
                "PouryaKitS3StorageOptions.ServiceUrl is not an absolute HTTP or HTTPS address; leave it null for AWS itself.",
                "PouryaKitS3StorageOptions.AccessKey and SecretKey are set together, or neither to use the AWS SDK's default credentials.",
            ],
            failure.Failures
        );
    }

    [Theory]
    [InlineData(604_801)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_presigned_url_whose_lifetime_is_not_positive_or_over_seven_days_is_refused_with_its_error(int seconds)
    {
        var refused = await fixture.Storage.GetPresignedUrlAsync(Key(), TimeSpan.FromSeconds(seconds), Cancel);

        Assert.Equal(StorageErrors.LinkLifetimeOutOfRange, Assert.Single(refused.Errors));
    }

    [Fact]
    public async Task A_presigned_url_of_seven_days_is_signed()
    {
        var link = await fixture.Storage.GetPresignedUrlAsync(Key(), TimeSpan.FromDays(7), Cancel);

        Assert.True(link.IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_host_that_registers_another_file_storage_provider_fails_to_start_naming_both(bool registeredFirst)
    {
        var builder = Host.CreateApplicationBuilder();
        if (registeredFirst)
        {
            builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();
        }

        builder.Services.AddPouryaKitS3Storage(s3 =>
        {
            s3.Bucket = "files";
            s3.Region = "us-east-1";
        });
        if (!registeredFirst)
        {
            builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();
        }

        using var host = builder.Build();

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Cancel));

        string[] providers = registeredFirst
            ? [typeof(LocalFileStorage).FullName!, "PouryaKit.Storage.S3.S3FileStorage"]
            : ["PouryaKit.Storage.S3.S3FileStorage", typeof(LocalFileStorage).FullName!];
        Assert.Equal(
            $"The Host registers 2 file storage providers, {string.Join(", ", providers)}, and composes exactly one IFileStorage: remove all but one of its registrations.",
            Assert.Single(failure.Failures)
        );
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string Key() => $"reports/{Guid.CreateVersion7()}.pdf";

    private static async Task<byte[]> ReadAll(Stream content)
    {
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        return copy.ToArray();
    }

    // Another provider a Host could register, never called.
    private sealed class LocalFileStorage : IFileStorage
    {
        public Task<Result> UploadAsync(string key, Stream content, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<StoredFile>> DownloadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result> DeleteAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<Uri>> GetPresignedUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
