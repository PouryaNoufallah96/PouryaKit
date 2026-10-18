using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Persistence.Marten;

/// <summary>
/// The Stream Modules of one Host, each with its schema, as <c>AddPouryaKitStreams</c> records them: the Host's idempotency
/// key cleanup cleans each one's table, and a messaging package's registration keys each one's Receipts through
/// <see cref="ForEach"/>, whichever is called first. It lives in the Host's service collection, not in a static, so every
/// Composition Root adds to the same one and two Hosts in one process never share it. Applications never use it.
/// </summary>
public sealed class StreamModules
{
    private readonly List<(Type? Module, string Schema)> _modules = [];
    private readonly List<Action<Type?, string>> _registrations = [];

    internal IEnumerable<string> Schemas => _modules.Select(module => module.Schema).Distinct();

    /// <summary>The Stream Modules of the Host whose services <paramref name="services"/> are, recorded there on first use.</summary>
    public static StreamModules In(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(StreamModules)) is { ImplementationInstance: StreamModules existing })
        {
            return existing;
        }

        var modules = new StreamModules();
        services.AddSingleton(modules);
        return modules;
    }

    /// <summary>
    /// Runs <paramref name="register"/> now for each Stream Module recorded, and for each one recorded later, with the
    /// Module's definition type, null for the application's own services, and its schema. For a Kit package's
    /// registration.
    /// </summary>
    public void ForEach(Action<Type?, string> register)
    {
        ArgumentNullException.ThrowIfNull(register);
        _registrations.Add(register);
        foreach (var (module, schema) in _modules)
        {
            register(module, schema);
        }
    }

    internal void Add(Type? module, string schema)
    {
        _modules.Add((module, schema));
        foreach (var register in _registrations)
        {
            register(module, schema);
        }
    }
}
