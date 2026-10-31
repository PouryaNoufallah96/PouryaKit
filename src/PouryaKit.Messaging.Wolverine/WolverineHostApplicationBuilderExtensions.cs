using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using Wolverine;
using Wolverine.Util;

namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Composes the Messaging Capability's routing, delivery and retries on Wolverine, without an Outbox, on a Host's
/// application builder.
/// </summary>
public static class WolverineHostApplicationBuilderExtensions
{
    extension(IHostApplicationBuilder builder)
    {
        /// <summary>
        /// Sets up the Host's routing, delivery and retry on Wolverine, but no Outbox: every Notification Handler and
        /// Integration Event Handler a Composition Root subscribed gets its own durable local queue and is delivered,
        /// retried and acknowledged on its own, in a scope begun as a continuation of the Operation that published the
        /// message, in the subscribing Module. A failed Result or an exception is retried after
        /// <see cref="PouryaKitMessagingOptions.RetryDelays"/>, then that subscription's delivery alone is set aside as a
        /// dead letter in Wolverine's dead-letter storage, received at <c>local://{subscription}/</c>, to inspect and
        /// replay there. A delayed command (<see cref="IDelayedCommands"/>) waits in its own durable queue until it is due,
        /// then runs and retries the same way, and so does each occurrence of a recurring command
        /// (<c>AddPouryaKitRecurringCommand</c>). A stored delivery whose type or subscription no longer exists is set aside
        /// the same way. None of
        /// them is ever treated as handled. Wolverine discovers no handlers of its own. Messages are stored and delivered
        /// in the Kit's JSON representation, the one <c>AddPouryaKitWeb</c> gives HTTP bodies, read strictly
        /// (<c>UsePouryaKitWireJson</c>): a body that lacks a member its type requires, carries null where the type
        /// forbids it, or names one of its members twice, fails to read and is set aside as a dead letter with the error,
        /// never handled.
        /// It installs the Kit's composition checks (<c>AddPouryaKitCompositionChecks</c>), so a Host with Wolverine has
        /// them with or without Modules. Nothing this overload registers writes an Outbox
        /// (<see cref="IOutbox"/>), so a Host that declares Notification Handlers, Integration Event Handlers
        /// or published Integration Events calls the overload of <c>PouryaKit.Messaging.Wolverine.EFCore</c> instead, or
        /// fails when it is built; so does a Host with a Notification Handler in the Feature assemblies of a Module it
        /// composed, or of its own services, that no <c>AddPouryaKitNotificationHandlers</c> declares, naming that call.
        /// The Host also fails when it is built, naming each problem, for a subscription whose Integration Event or
        /// Snapshot no Module publishes and no <c>Receive</c> declares, a type received from another Host that no
        /// subscription or Saga handles, a message type Wolverine would also route where another type goes, a delayed command
        /// whose name a subscription has, a wire name this process already sends the type under, or one Wolverine gave
        /// the type before it was declared, and for a subscription whose Module, or the application, registers no Unit of
        /// Work. Wolverine generates its handler
        /// code at run time in Development and, in every other environment, loads it from the Host's own assembly, where
        /// <c>dotnet run -- codegen write</c> wrote it before the build (<see cref="TypeLoadMode.Static"/>): a Host there needs no
        /// runtime compiler, and one whose handler chain has no generated type fails when it starts.
        /// <paramref name="configure"/> adds Host-specific Wolverine configuration as Wolverine is registered, after the
        /// settings this call makes there, which it may override, but before the routes, handlers and retry policy, and a
        /// message store's durability mode and inbox stale time, which the Kit applies when Wolverine first resolves its
        /// options, so those are set through <see cref="PouryaKitMessagingOptions"/> and <c>PouryaKitMessageStoreOptions</c>.
        /// Call it once: a second call throws Wolverine's <see cref="InvalidOperationException"/>, since <c>UseWolverine</c> runs
        /// once per service collection.
        /// </summary>
        public IHostApplicationBuilder UsePouryaKitWolverine(
            Action<WolverineOptions>? configure = null
        )
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.AddPouryaKitCompositionChecks();
            WolverineMessageNaming.InsertFirst<WireNaming>();
            Subscriptions.In(builder.Services);
            builder
                .Services.AddOptions<PouryaKitMessagingOptions>()
                .Validate(
                    messaging => messaging.RetryDelays is not null,
                    "PouryaKitMessagingOptions.RetryDelays is null; set it to the delays a failed delivery waits before each further attempt."
                )
                .Validate(
                    messaging => messaging.RetryDelays is null || messaging.RetryDelays.Length > 0,
                    "PouryaKitMessagingOptions.RetryDelays is empty; set at least one delay, where a zero delay retries at once."
                )
                .Validate(
                    messaging =>
                        messaging.RetryDelays is null
                        || messaging.RetryDelays.All(delay => delay >= TimeSpan.Zero),
                    "PouryaKitMessagingOptions.RetryDelays holds a negative delay; set each delay to zero or longer."
                )
                .ValidateOnStart();
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<
                    ICompositionCheck,
                    UnregisteredNotificationHandlersCheck
                >()
            );
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<ICompositionCheck, RoutingCheck>()
            );
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<ICompositionCheck, SubscriptionUnitOfWorkCheck>()
            );
            builder.Services.TryAddScoped<CommandHandler<Deliver>, DeliverHandler>();
            builder.Services.TryAddScoped<NotificationDelivery>();
            builder.Services.TryAddScoped<INotificationDelivery>(scope =>
                scope.GetRequiredService<NotificationDelivery>()
            );
            builder.Services.TryAddScoped<IIntegrationEventPublisher, IntegrationEventPublisher>();
            builder.Services.TryAddScoped<ISnapshotMappers, SnapshotMappers>();
            DelayedCommandDeclarations.In(builder.Services);
            builder.Services.TryAddScoped<IDelayedCommands, DelayedCommands>();
            builder.Services.TryAddScoped<
                CommandHandler<RunDelayedCommand>,
                RunDelayedCommandHandler
            >();
            builder.Services.TryAddScoped<
                CommandHandler<StartRecurringCommand>,
                StartRecurringCommandHandler
            >();
            builder.Services.AddSingleton<IWolverineExtension, PouryaKitWolverineExtension>();
            builder.Services.AddSingleton<IWolverineExtension, DelayedCommandExtension>();
            builder.Services.CritterStackDefaults(critterStack =>
            {
                critterStack.Production.GeneratedCodeMode = TypeLoadMode.Static;
                critterStack.Production.AssertAllPreGeneratedTypesExist = true;
            });
            builder.UseWolverine(wolverine =>
            {
                // Wolverine would take the first assembly on the stack outside its own, this Kit package, and load the
                // generated code from there; codegen write puts it in the Host's.
                if (Assembly.GetEntryAssembly() is { } host)
                {
                    wolverine.ApplicationAssembly = host;
                }

                wolverine.Discovery.DisableConventionalDiscovery();
                wolverine.UnknownMessageBehavior = UnknownMessageBehavior.DeadLetterQueue;
                wolverine.Policies.UseDurableLocalQueues();
                wolverine.UseSystemTextJsonForSerialization(json => json.UsePouryaKitWireJson());
                configure?.Invoke(wolverine);
            });
            return builder;
        }
    }
}
