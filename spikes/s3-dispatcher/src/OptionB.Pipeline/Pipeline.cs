using System.Reflection;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Spike.Common;

namespace OptionB.Pipeline;

// Outermost behaviour, so a failure from any later step of a nested command marks the Operation rollback-only.
public sealed class FailureTrackingBehavior<TMessage, TResponse>(OperationState state) : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
    where TResponse : IResult
{
    public async ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken ct)
    {
        state.Depth++;
        TResponse response;
        try { response = await next(message, ct); }
        finally { state.Depth--; }

        if (message is IBaseCommand && response.IsFailure) state.MarkRollbackOnly();
        return response;
    }
}

public sealed class LoggingBehavior<TMessage, TResponse>(OperationTrace trace) : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    public async ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken ct)
    {
        trace.Add($"log:start {typeof(TMessage).Name}");
        try { return await next(message, ct); }
        finally { trace.Add($"log:end {typeof(TMessage).Name}"); }
    }
}

public sealed class AuthorizationBehavior<TMessage, TResponse>(IEnumerable<IAuthorizer<TMessage>> authorizers)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
    where TResponse : IResult, IFailable<TResponse>
{
    public ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken ct)
    {
        foreach (var authorizer in authorizers)
            if (authorizer.Authorize(message) is { } denied) return ValueTask.FromResult(TResponse.Fail([denied]));

        return next(message, ct);
    }
}

public sealed class ValidationBehavior<TMessage, TResponse>(IEnumerable<IValidator<TMessage>> validators)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
    where TResponse : IResult, IFailable<TResponse>
{
    public ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken ct)
    {
        var errors = validators.SelectMany(v => v.Validate(message)).ToList();
        return errors.Count > 0 ? ValueTask.FromResult(TResponse.Fail(errors)) : next(message, ct);
    }
}

// Constrained to commands: queries skip the Unit of Work.
public sealed class UnitOfWorkBehavior<TMessage, TResponse>(UnitOfWorkStandIn unitOfWork) : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IBaseCommand
    where TResponse : IResult
{
    public async ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken ct)
    {
        var owner = unitOfWork.Begin();
        var response = await next(message, ct);
        if (owner) unitOfWork.Complete(response);
        return response;
    }
}

public static class PipelineRegistration
{
    /// <summary>The fixed pipeline order. Mediator's generator cannot see this list; the Host must pass it on.</summary>
    public static readonly Type[] Behaviors =
    [
        typeof(FailureTrackingBehavior<,>), typeof(LoggingBehavior<,>), typeof(AuthorizationBehavior<,>),
        typeof(ValidationBehavior<,>), typeof(UnitOfWorkBehavior<,>),
    ];

    /// <summary>Mediator does not discover validators or authorizers: the Kit still scans for them.</summary>
    public static IServiceCollection AddKitPipeline(this IServiceCollection services, params Assembly[] moduleAssemblies)
    {
        services.AddOperationStandIns();
        foreach (var behavior in Behaviors) services.AddScoped(typeof(IPipelineBehavior<,>), behavior);

        foreach (var type in moduleAssemblies.SelectMany(a => a.GetTypes()).Where(t => t is { IsClass: true, IsAbstract: false }))
        foreach (var service in type.GetInterfaces().Where(i => i.IsGenericType &&
                     (i.GetGenericTypeDefinition() == typeof(IValidator<>) || i.GetGenericTypeDefinition() == typeof(IAuthorizer<>))))
            services.AddScoped(service, type);

        return services;
    }
}
