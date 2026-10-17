using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace PouryaKit.Storage.S3;

/// <summary>Composes the file storage Capability on a Host's services with an S3 bucket as its provider.</summary>
public static class S3StorageServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Makes an S3 bucket, or a bucket of an S3-compatible store such as MinIO, the Host's one
        /// <see cref="IFileStorage"/>, a singleton built from <see cref="PouryaKitS3StorageOptions"/> when first resolved.
        /// The Host binds the options from configuration; <paramref name="configure"/> sets them in code instead. The
        /// Host fails when it starts, naming each problem, when the options name no bucket or region, a service URL
        /// that is not an absolute HTTP or HTTPS address, or an access key without its secret key or the reverse, and
        /// when it registers another <see cref="IFileStorage"/>, before or after this call, naming each implementation:
        /// a Host composes exactly one.
        /// </summary>
        public IServiceCollection AddPouryaKitS3Storage(Action<PouryaKitS3StorageOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            var options = services
                .AddOptions<PouryaKitS3StorageOptions>()
                .Validate(s3 => !string.IsNullOrWhiteSpace(s3.Bucket), "PouryaKitS3StorageOptions.Bucket names no bucket to store files in.")
                .Validate(s3 => !string.IsNullOrWhiteSpace(s3.Region), "PouryaKitS3StorageOptions.Region names no region to reach or sign for.")
                .Validate(
                    s3 => s3.ServiceUrl is null || (s3.ServiceUrl.IsAbsoluteUri && (s3.ServiceUrl.Scheme == Uri.UriSchemeHttp || s3.ServiceUrl.Scheme == Uri.UriSchemeHttps)),
                    "PouryaKitS3StorageOptions.ServiceUrl is not an absolute HTTP or HTTPS address; leave it null for AWS itself."
                )
                .Validate(
                    s3 => string.IsNullOrWhiteSpace(s3.AccessKey) == string.IsNullOrWhiteSpace(s3.SecretKey),
                    "PouryaKitS3StorageOptions.AccessKey and SecretKey are set together, or neither to use the AWS SDK's default credentials."
                )
                .ValidateOnStart();
            if (configure is not null)
            {
                options.Configure(configure);
            }

            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PouryaKitS3StorageOptions>>(new OneFileStorage(services)));
            services.AddSingleton<IFileStorage>(Create);
            return services;
        }
    }

    // Typed as the implementation, so that the one-provider check names it.
    private static S3FileStorage Create(IServiceProvider provider) => new(provider.GetRequiredService<IOptions<PouryaKitS3StorageOptions>>().Value);
}
