using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Storage.AzureBlob;
using PouryaKit.Storage.S3;

namespace PouryaKit.Storage.Tests;

// The Azure Blob provider against Azurite in Testcontainers, through the contract a Feature uses.
public sealed class AzureBlobStorageTests(AzuriteFixture fixture) : IClassFixture<AzuriteFixture>
{
    private static readonly byte[] Report = Encoding.UTF8.GetBytes("%PDF-1.7 quarterly report");

    [Fact]
    public async Task An_uploaded_file_downloads_with_its_content_and_content_type()
    {
        var key = Key();
        Assert.True((await fixture.Storage.UploadAsync(key, new MemoryStream(Report), "application/pdf", Cancel)).IsSuccess);

        await using var file = (await fixture.Storage.DownloadAsync(key, Cancel)).Value;

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
    public async Task An_upload_under_a_stored_key_replaces_the_file()
    {
        var key = Key();
        await fixture.Storage.UploadAsync(key, new MemoryStream(Report), "application/pdf", Cancel);

        await fixture.Storage.UploadAsync(key, new MemoryStream(Report[..4]), "text/plain", Cancel);

        await using var file = (await fixture.Storage.DownloadAsync(key, Cancel)).Value;
        Assert.Equal("text/plain", file.ContentType);
        Assert.Equal(Report[..4], await ReadAll(file.Content));
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
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
    }

    [Fact]
    public async Task A_presigned_url_requested_with_a_cancelled_token_is_not_returned()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Storage.GetPresignedUrlAsync(Key(), TimeSpan.FromMinutes(5), cancelled.Token));
    }

    [Theory]
    [InlineData(604_801)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_presigned_url_whose_lifetime_is_not_positive_or_over_seven_days_is_refused_with_its_error(int seconds)
    {
        var refused = await fixture.Storage.GetPresignedUrlAsync(Key(), TimeSpan.FromSeconds(seconds), Cancel);

        Assert.Equal(StorageErrors.LinkLifetimeOutOfRange(TimeSpan.FromDays(7)), Assert.Single(refused.Errors));
    }

    [Fact]
    public async Task A_presigned_url_of_seven_days_is_signed()
    {
        Assert.True((await fixture.Storage.GetPresignedUrlAsync(Key(), TimeSpan.FromDays(7), Cancel)).IsSuccess);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task A_host_bound_to_the_service_url_and_account_key_stores_a_file_and_signs_its_link(string? connectionString)
    {
        using var host = AzuriteFixture.ComposeHost(
            new()
            {
                ["PouryaKit:Storage:AzureBlob:Container"] = AzuriteFixture.Container,
                ["PouryaKit:Storage:AzureBlob:ConnectionString"] = connectionString,
                ["PouryaKit:Storage:AzureBlob:ServiceUrl"] = fixture.BlobEndpoint,
                ["PouryaKit:Storage:AzureBlob:AccountName"] = AzuriteFixture.AccountName,
                ["PouryaKit:Storage:AzureBlob:AccountKey"] = AzuriteFixture.AccountKey,
            }
        );
        await host.StartAsync(Cancel);
        var storage = host.Services.GetRequiredService<IFileStorage>();
        var key = Key();

        await storage.UploadAsync(key, new MemoryStream(Report), "application/pdf", Cancel);
        var link = await storage.GetPresignedUrlAsync(key, TimeSpan.FromMinutes(5), Cancel);

        using var http = new HttpClient();
        Assert.Equal(Report, await http.GetByteArrayAsync(link.Value, Cancel));
    }

    [Theory]
    [InlineData("not a connection string")]
    [InlineData("BlobEndpoint=https://acme.blob.core.windows.net;SharedAccessSignature=sv=2025-11-05&sig=abc")]
    public async Task A_host_with_invalid_storage_options_fails_to_start_naming_each_problem(string connectionString)
    {
        using var host = AzuriteFixture.ComposeHost(
            new()
            {
                ["PouryaKit:Storage:AzureBlob:ConnectionString"] = connectionString,
                ["PouryaKit:Storage:AzureBlob:ServiceUrl"] = "azurite:10000",
                ["PouryaKit:Storage:AzureBlob:AccountName"] = AzuriteFixture.AccountName,
            }
        );

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Cancel));

        Assert.Equal(
            [
                "PouryaKitAzureBlobStorageOptions.Container names no container to store files in.",
                "PouryaKitAzureBlobStorageOptions.ConnectionString or ServiceUrl is set, one and not both.",
                "PouryaKitAzureBlobStorageOptions.ConnectionString is not a storage connection string with an account key, which signs the presigned links.",
                "PouryaKitAzureBlobStorageOptions.ServiceUrl is not an absolute HTTP or HTTPS address.",
                "PouryaKitAzureBlobStorageOptions.AccountName and AccountKey are set with ServiceUrl, and only with it.",
            ],
            failure.Failures
        );
    }

    // PR #143: a key that is not base64 fails the start, not the first resolution of the file storage.
    [Fact]
    public async Task A_host_whose_account_key_is_not_base64_fails_to_start_naming_the_option()
    {
        using var host = AzuriteFixture.ComposeHost(
            new()
            {
                ["PouryaKit:Storage:AzureBlob:Container"] = AzuriteFixture.Container,
                ["PouryaKit:Storage:AzureBlob:ServiceUrl"] = fixture.BlobEndpoint,
                ["PouryaKit:Storage:AzureBlob:AccountName"] = AzuriteFixture.AccountName,
                ["PouryaKit:Storage:AzureBlob:AccountKey"] = "not-a-base64-key!",
            }
        );

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Cancel));

        Assert.Equal(["PouryaKitAzureBlobStorageOptions.AccountKey is not a base64 account key."], failure.Failures);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_host_that_registers_both_providers_fails_when_it_is_built_naming_both(bool s3First)
    {
        const string S3 = "the factory PouryaKit.Storage.S3.S3StorageServiceCollectionExtensions.Create";
        const string AzureBlob = "the factory PouryaKit.Storage.AzureBlob.AzureBlobStorageServiceCollectionExtensions.Create";
        var builder = Host.CreateApplicationBuilder();
        builder.AddPouryaKitCompositionChecks();
        if (s3First)
        {
            builder.Services.AddPouryaKitS3Storage();
            builder.Services.AddPouryaKitAzureBlobStorage();
        }
        else
        {
            builder.Services.AddPouryaKitAzureBlobStorage();
            builder.Services.AddPouryaKitS3Storage();
        }

        var failure = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            $"The Host registers 2 IFileStorage providers, {(s3First ? $"{S3}, {AzureBlob}" : $"{AzureBlob}, {S3}")}, and composes exactly one: remove all but one of its file storage registrations.",
            failure.Message
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
}
