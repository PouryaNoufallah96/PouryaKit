using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Modularity;

/// <summary>
/// What a Module's Composition Root registers on: the Host container's services, seen from one Module. Every Kit
/// registration (<c>AddPouryaKitCqrs</c>, <c>AddPouryaKitValidation</c>, <c>UsePostgres</c>) is called on it. The
/// Unit of Work registered here belongs to this Module alone: an Operation reaches it only through the Module's
/// Gateway, and the commands and queries whose handlers are registered here belong to this Module: its Gateway is the
/// only one that runs them, and another Module that registers or removes one of their handlers fails while the Host is
/// composed.
/// </summary>
public sealed class ModuleBuilder : IServiceCollection
{
    private readonly Type _module;
    private readonly IServiceCollection _services;
    private readonly HostModules _modules;

    internal ModuleBuilder(Type module, IServiceCollection services, HostModules modules)
    {
        _module = module;
        _services = services;
        _modules = modules;
    }

    public int Count => _services.Count;

    public bool IsReadOnly => _services.IsReadOnly;

    public ServiceDescriptor this[int index]
    {
        get => _services[index];
        set
        {
            _modules.Release(_module, _services[index]);
            _services[index] = Own(value);
        }
    }

    public void Add(ServiceDescriptor item) => _services.Add(Own(item));

    public void Insert(int index, ServiceDescriptor item) => _services.Insert(index, Own(item));

    public void Clear()
    {
        foreach (var descriptor in _services)
        {
            _modules.Release(_module, descriptor);
        }

        _services.Clear();
    }

    public bool Contains(ServiceDescriptor item) => _services.Contains(item);

    public void CopyTo(ServiceDescriptor[] array, int arrayIndex) => _services.CopyTo(array, arrayIndex);

    public IEnumerator<ServiceDescriptor> GetEnumerator() => _services.GetEnumerator();

    public int IndexOf(ServiceDescriptor item) => _services.IndexOf(item);

    public bool Remove(ServiceDescriptor item)
    {
        if (_services.Contains(item))
        {
            _modules.Release(_module, item);
        }

        return _services.Remove(item);
    }

    public void RemoveAt(int index)
    {
        _modules.Release(_module, _services[index]);
        _services.RemoveAt(index);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // A persistence package registers its Unit of Work unkeyed, as it would in a single-Module application. Here it
    // is keyed by the Module instead, and the one unkeyed IUnitOfWork routes each Gateway scope to its own Module's.
    private ServiceDescriptor Own(ServiceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _modules.Record(_module, descriptor);
        if (descriptor.ServiceType != typeof(IUnitOfWork) || descriptor.IsKeyedService)
        {
            return descriptor;
        }

        if (descriptor.ImplementationInstance is { } instance)
        {
            return new ServiceDescriptor(descriptor.ServiceType, _module, instance);
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return new ServiceDescriptor(
                descriptor.ServiceType,
                _module,
                (services, _) => factory(services),
                descriptor.Lifetime
            );
        }

        return new ServiceDescriptor(
            descriptor.ServiceType,
            _module,
            descriptor.ImplementationType!,
            descriptor.Lifetime
        );
    }
}
