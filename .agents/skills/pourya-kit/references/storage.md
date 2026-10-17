# File storage

`PouryaKit.Storage`: one contract, `IFileStorage`, that uploads, downloads and deletes a file under a key and grants a time-limited link to read it, every call cancellable and returning a `Result`, so Feature code never names a cloud SDK. Namespace `PouryaKit.Storage`; it references `PouryaKit.Domain` only. A provider package supplies the Host's one implementation: `PouryaKit.Storage.S3` (namespace `PouryaKit.Storage.S3`) over `AWSSDK.S3`, for AWS S3 and S3-compatible stores such as MinIO.

- [Registering](#registering)
- [Store, then record](#store-then-record)
- [The calls](#the-calls)
- [What the Kit guarantees](#what-the-kit-guarantees)
- [Limits](#limits)
- [Testing](#testing)

## Registering

On the Host, bind the provider's options from configuration, then register it:

```csharp
using PouryaKit.Storage.S3;

builder.Services.Configure<PouryaKitS3StorageOptions>(builder.Configuration.GetSection("PouryaKit:Storage:S3"));
builder.Services.AddPouryaKitS3Storage();
```

```json
{
  "PouryaKit": {
    "Storage": {
      "S3": { "Bucket": "acme-files", "Region": "eu-central-1" }
    }
  }
}
```

- `Bucket` and `Region` are required. On AWS, leave `ServiceUrl` unset and credentials come from the AWS SDK's default chain (environment, profile, the instance's or task's role). For an S3-compatible store, set `ServiceUrl` (`http://minio:9000`), `ForcePathStyle` to `true` for MinIO, and `AccessKey` with `SecretKey`; `Region` is then the region requests are signed for, such as `us-east-1`.
- The bucket must exist: the Kit creates none.
- The options are validated when the Host starts, which fails naming each problem: no bucket or region, a service URL that is not an absolute HTTP or HTTPS address, or an access key without its secret key or the reverse. The client is built from them when `IFileStorage` is first resolved.
- A Host composes exactly one provider. `AddPouryaKitS3Storage` adds a check to its options' validation, so a Host that registers another `IFileStorage`, before or after the call, a second call of its own included, fails when it starts, naming each implementation. A Host whose code needs `IFileStorage` and that registers none fails when its container is built: a Kit Host validates its container then, and `IFileStorage` has no registration to resolve.

## Store, then record

No file store joins the Unit of Work's commit, so a file is never transactional. Store the file first, under a key the Feature chooses, and only then record the key in the command's own state:

```csharp
internal sealed class AttachContractCommandHandler(IFileStorage files, IRepository<Customer, CustomerId> customers)
    : CommandHandler<AttachContractCommand>
{
    public override async Task<Result> Handle(AttachContractCommand command, CancellationToken cancellationToken)
    {
        var customer = await customers.FindAsync(new CustomerId(command.CustomerId), cancellationToken);
        if (customer is null)
        {
            return CustomerErrors.NotFound;
        }

        var key = $"contracts/{command.CustomerId}/{Guid.CreateVersion7()}.pdf";
        var stored = await files.UploadAsync(key, command.Content, "application/pdf", cancellationToken);
        return stored.IsFailure ? stored : customer.AttachContract(key);
    }
}
```

- A recorded key always names a stored file: the command records it only after the upload returned.
- A command that fails or rolls back after the upload leaves its object behind, unnamed by any record. The application's retention removes such objects, for example a recurring command that deletes the keys of attempts it can derive from its own records, or a lifecycle rule of the store on a prefix the application stages files under. `IFileStorage` has no listing.
- A fresh key per upload keeps a retried or concurrent command from replacing a file another record names; an upload under an existing key replaces that file.
- Deleting a recorded file follows the same order in reverse: remove the record in the command, then delete the object once the Operation committed, for example from a Notification Handler, so a rolled-back command never loses a file its record still names.

## The calls

- `UploadAsync(key, content, contentType, cancellationToken)` stores the stream from its current position to its end. The caller keeps and disposes the stream.
- `DownloadAsync(key, cancellationToken)` returns a `StoredFile` with its `Content` stream and `ContentType`, or `StorageErrors.FileNotFound`, an `ErrorKind.NotFound` Error, when no file is stored under the key. Dispose the `StoredFile`: its stream is read from the store as the caller reads it.
- `DeleteAsync(key, cancellationToken)` succeeds whether or not a file was stored under the key, so retention can run again.
- `GetPresignedUrlAsync(key, lifetime, cancellationToken)` returns a link that reads the file with a plain HTTP GET, without credentials, until `lifetime` has passed. The store is not asked whether the file exists: a link to a missing file answers not found when followed. A token cancelled before the call fails it; one cancelled while the SDK resolves credentials stops the wait, though the SDK may still sign. A lifetime of zero or less, or over seven days, the longest S3 signs a link for, fails with `StorageErrors.LinkLifetimeOutOfRange`, an `ErrorKind.Validation` Error, and signs nothing.
- A blank key or content type is a bug and throws `ArgumentException`. A failure of the store itself (a refused credential, a missing bucket, an unreachable endpoint) throws the provider's exception: it is infrastructure, not a business outcome.

## What the Kit guarantees

- **The contract and the provider's behaviour.** Upload, download, delete and the presigned link behave as above against a real S3-compatible store; a missing file is the not-found Error, never an exception.
- **One provider, validated before serving.** A Host that registers a second provider, in either order, fails when it starts, naming both, as invalid options do.
- **Not a transactional file.** The Kit never stores a file in the Unit of Work's commit, and never removes an object a failed command left behind: that is store-then-record and the application's retention.

## Limits

- No listing, metadata or existence call: a Feature records what it stores.
- The key is the application's: the Kit neither scopes it by tenant nor checks it beyond refusing a blank one. A tenant-scoped Module puts the tenant in its keys, or keeps them in its tenant-scoped records only.
- A presigned link grants anyone who holds it read access until it expires; it cannot be revoked short of deleting the file.

## Testing

`S3StorageTests` in `tests/PouryaKit.Storage.Tests` runs the provider against RustFS, an S3-compatible store with no Testcontainers module, through `Testcontainers`' generic `ContainerBuilder("rustfs/rustfs:1.0.2")`: set its credentials with `RUSTFS_ACCESS_KEY` and `RUSTFS_SECRET_KEY`, wait for an HTTP 200 from `/health/ready` on port 9000, create the bucket with those credentials, then compose a Host with `ServiceUrl` the mapped `http://host:port`, `ForcePathStyle` true and `Region` `us-east-1`. A presigned link to a local store is HTTP, since the service URL is; fetch it with a plain `HttpClient` to prove it reads the file without credentials.
