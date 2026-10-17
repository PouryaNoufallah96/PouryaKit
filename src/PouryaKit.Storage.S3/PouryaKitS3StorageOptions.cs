namespace PouryaKit.Storage.S3;

/// <summary>
/// The S3 bucket the Host stores its files in and how to reach it, bound from configuration by the Host, for example
/// <c>Configure&lt;PouryaKitS3StorageOptions&gt;(configuration.GetSection("PouryaKit:Storage:S3"))</c>.
/// </summary>
public sealed class PouryaKitS3StorageOptions
{
    /// <summary>The bucket every file is stored in; it must exist, since the Kit creates none.</summary>
    public string Bucket { get; set; } = "";

    /// <summary>The bucket's AWS region, such as <c>eu-central-1</c>; with <see cref="ServiceUrl"/>, the region requests are signed for.</summary>
    public string Region { get; set; } = "";

    /// <summary>
    /// The endpoint of an S3-compatible store, such as MinIO's <c>http://localhost:9000</c>; null, the default, for AWS
    /// itself, reached through <see cref="Region"/>.
    /// </summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>
    /// True to name the bucket in the path (<c>host/bucket/key</c>) rather than the host name, as MinIO requires; false
    /// by default.
    /// </summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>
    /// The access key id, set with <see cref="SecretKey"/>; null, the default, to take credentials from the AWS SDK's
    /// default chain, such as the environment or the instance's role.
    /// </summary>
    public string? AccessKey { get; set; }

    /// <summary>The secret access key of <see cref="AccessKey"/>, set with it.</summary>
    public string? SecretKey { get; set; }
}
