using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Domain;
using PouryaKit.Grpc;

// The packed gRPC package, registered and used as a Host would: a client of another Host over options it binds and the
// Kit validates when the Host starts, a declared service, and a failed Result answered as its status with the trailer.
var services = new ServiceCollection();
services.AddPouryaKitGrpcClient<StockClient>("stock");
services.Configure<PouryaKitGrpcClientOptions>(
    "stock",
    options =>
    {
        options.Address = new Uri("https://stock.example");
        options.Deadline = TimeSpan.FromSeconds(5);
    }
);
services.AddPouryaKitGrpcService<StockService>();
using var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<StockClient>();
Result missing = Error.NotFound("Stock.NotFound", "The item is not stocked.");
var failure = Assert(() => missing.ToGrpcResponse("unused"));
Console.WriteLine(
    client is not null
    && failure is { StatusCode: StatusCode.NotFound }
    && failure.Trailers.Get(ResultGrpcExtensions.ErrorsTrailer) is { IsBinary: true }
        ? 1
        : 0
);

static RpcException? Assert(Func<string> answer)
{
    try
    {
        answer();
        return null;
    }
    catch (RpcException failure)
    {
        return failure;
    }
}

// What Grpc.Tools generates for a client, written out.
internal sealed class StockClient : ClientBase<StockClient>
{
    public StockClient(CallInvoker invoker)
        : base(invoker) { }

    private StockClient(ClientBaseConfiguration configuration)
        : base(configuration) { }

    protected override StockClient NewInstance(ClientBaseConfiguration configuration) =>
        new(configuration);
}

internal sealed class StockService;
