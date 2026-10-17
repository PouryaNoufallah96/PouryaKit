using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PouryaKit.Domain;
using PouryaKit.Storage.S3;

namespace PouryaKit.Storage.Tests;

// The S3 provider against a real S3-compatible store, MinIO in Testcontainers, through the contract a Feature uses.
public sealed class S3StorageTests(MinioFixture fixture) : IClassFixture<MinioFixture>
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
        using var host = MinioFixture.ComposeHost(new() { ["PouryaKit:Storage:S3:ServiceUrl"] = "minio:9000", ["PouryaKit:Storage:S3:AccessKey"] = "kit" });

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

    [Fact]
    public void A_second_file_storage_provider_is_refused_when_it_is_registered()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddPouryaKitS3Storage();

        var refusal = Assert.Throws<InvalidOperationException>(() => builder.Services.AddPouryaKitS3Storage());

        Assert.Equal(
            "The Host already registers a file storage provider, and a Host composes exactly one: remove one of its file storage registrations.",
            refusal.Message
        );
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(IFileStorage));
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string Key() => $"reports/{Guid.CreateVersion7()}.pdf";

    private static async Task<byte[]> ReadAll(Stream content)
    {
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        return copy.ToArray();
    }
}
