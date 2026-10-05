using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Spike.Common;

namespace OptionB.SelfGenerated.Alpha;

public static class AlphaModule
{
    public static IServiceCollection AddAlphaModule(this IServiceCollection services) =>
        services.AddMediator(options =>
        {
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.GenerateTypesAsInternal = true;
        });

    public static ValueTask<Result<string>> Ping(ISender sender) => sender.Send(new AlphaPing());
}

internal sealed record AlphaPing : ICommand<Result<string>>;

internal sealed class AlphaPingHandler : ICommandHandler<AlphaPing, Result<string>>
{
    public ValueTask<Result<string>> Handle(AlphaPing command, CancellationToken ct) =>
        ValueTask.FromResult<Result<string>>("alpha");
}
