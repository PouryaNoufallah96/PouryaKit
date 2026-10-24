namespace PouryaKit.Grpc;

/// <summary>
/// Where one gRPC client calls another Host and how long a call may take, named by the client's name and bound from
/// configuration, such as <c>PouryaKit:Grpc:Clients:catalog</c>; validated when the Host starts.
/// </summary>
public sealed class PouryaKitGrpcClientOptions
{
    /// <summary>The other Host's absolute <c>http</c> or <c>https</c> address; never one of this Host's own.</summary>
    public Uri? Address { get; set; }

    /// <summary>
    /// How long a call may take when its caller sets no deadline of its own; positive. A duration that would end after
    /// <see cref="DateTime.MaxValue"/>, such as <see cref="TimeSpan.MaxValue"/>, sends the call with no deadline.
    /// </summary>
    public TimeSpan Deadline { get; set; }
}
