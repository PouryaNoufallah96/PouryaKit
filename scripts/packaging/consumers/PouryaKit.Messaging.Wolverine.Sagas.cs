using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Messaging.Wolverine.Sagas;

// What a Module's Composition Root calls on the packed package: a Saga with its input and its timeout. Compiled, never
// run: a real Host also composes its message store and the Module that publishes the event.
var services = new ServiceCollection();
services.AddPouryaKitSaga<WelcomeSaga>(
    "consumer.welcome",
    "consumer.welcome",
    saga => saga.Input<Registered>(registered => registered.Id).Timeout<WelcomeTimeout>()
);
Console.WriteLine(services.Count);

/// <summary>An Integration Event the saga takes.</summary>
public sealed record Registered(Guid Id);

/// <summary>A saga started by <see cref="Registered"/>.</summary>
public sealed class WelcomeSaga : PouryaKitSaga
{
    /// <summary>Starts the saga.</summary>
    public static WelcomeSaga Start(SagaInput<WelcomeSaga, Registered> registered) => new();
}

/// <summary>The saga's timeout.</summary>
public sealed record WelcomeTimeout(TimeSpan Within) : SagaTimeout(Within);
