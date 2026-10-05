using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Spike.Common;

namespace OptionB.SelfGenerated.Beta;

public static class BetaModule
{
    public static IServiceCollection AddBetaModule(this IServiceCollection services) =>
        services.AddMediator(options =>
        {
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.GenerateTypesAsInternal = true;
        });

    public static ValueTask<Result<string>> Ping(ISender sender) => sender.Send(new BetaPing());
}

internal sealed record BetaPing : ICommand<Result<string>>;

internal sealed class BetaPingHandler : ICommandHandler<BetaPing, Result<string>>
{
    public ValueTask<Result<string>> Handle(BetaPing command, CancellationToken ct) =>
        ValueTask.FromResult<Result<string>>("beta");
}
