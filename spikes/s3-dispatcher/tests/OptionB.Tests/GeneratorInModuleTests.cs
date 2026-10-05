using Mediator;
using Microsoft.Extensions.DependencyInjection;
using OptionB.SelfGenerated.Alpha;
using OptionB.SelfGenerated.Beta;
using Xunit;

namespace OptionB.Tests;

// The alternative to InternalsVisibleTo: each Module runs Mediator's generator over its own internal types.
// Each registration adds IMediator/ISender with TryAdd, so in one Host container the first Module wins.
public sealed class GeneratorInModuleTests
{
    [Fact]
    public async Task Two_modules_each_generating_a_mediator_share_one_ISender_that_knows_only_the_first()
    {
        await using var host = new ServiceCollection().AddAlphaModule().AddBetaModule()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = host.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Equal("alpha", (await AlphaModule.Ping(sender)).Value);
        await Assert.ThrowsAsync<MissingMessageHandlerException>(async () => await BetaModule.Ping(sender));
    }
}
