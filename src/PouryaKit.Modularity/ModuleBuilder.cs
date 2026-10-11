using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Modularity;

/// <summary>
/// What a Module's Composition Root registers on: the Host container's services, seen from one Module
/// (<see cref="IModuleServices"/>). Every Kit registration (<c>AddPouryaKitCqrs</c>, <c>AddPouryaKitValidation</c>,
/// <c>UsePouryaKitPostgres</c>, <c>Subscribe</c>) is called on it, and belongs to this Module without naming it: one that
/// scans Features scans the assembly of the Module's definition unless it names assemblies, and a declaration whose
/// deliveries run in a Module runs in this one. The Unit of Work registered here belongs to this Module alone: an Operation
/// reaches it only through the Module's Gateway, and the commands and queries whose handlers are registered here belong to
/// this Module: its Gateway is the only one that runs them, and another Module that registers or removes one of their
/// handlers fails while the Host is composed.
/// </summary>
public sealed class ModuleBuilder : IModuleServices
{
    private readonly IServiceCollection _services;
    private readonly HostModules _modules;

    internal ModuleBuilder(Type module, IServiceCollection services, HostModules modules)
    {
        Module = module;
        _services = services;
        _modules = modules;
    }

    /// <inheritdoc />
    public Type Module { get; }

    /// <inheritdoc />
    public int Count => _services.Count;

    /// <inheritdoc />
    public bool IsReadOnly => _services.IsReadOnly;

    /// <inheritdoc />
    public ServiceDescriptor this[int index]
    {
        get => _services[index];
        set
        {
            _modules.Release(Module, _services[index]);
            _services[index] = Own(value);
        }
    }

    /// <inheritdoc />
    public void Add(ServiceDescriptor item) => _services.Add(Own(item));

    /// <inheritdoc />
    public void Insert(int index, ServiceDescriptor item) => _services.Insert(index, Own(item));

    /// <inheritdoc />
    public void Clear()
    {
        foreach (var descriptor in _services)
        {
            _modules.Release(Module, descriptor);
        }

        _services.Clear();
    }

    /// <inheritdoc />
    public bool Contains(ServiceDescriptor item) => _services.Contains(item);

    /// <inheritdoc />
    public void CopyTo(ServiceDescriptor[] array, int arrayIndex) => _services.CopyTo(array, arrayIndex);

    /// <inheritdoc />
    public IEnumerator<ServiceDescriptor> GetEnumerator() => _services.GetEnumerator();

    /// <inheritdoc />
    public int IndexOf(ServiceDescriptor item) => _services.IndexOf(item);

    /// <inheritdoc />
    public bool Remove(ServiceDescriptor item)
    {
        if (_services.Contains(item))
        {
            _modules.Release(Module, item);
        }

        return _services.Remove(item);
    }

    /// <inheritdoc />
    public void RemoveAt(int index)
    {
        _modules.Release(Module, _services[index]);
        _services.RemoveAt(index);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // A persistence package registers its Unit of Work unkeyed, as it would in a single-Module application. Here it
    // is keyed by the Module instead, and the one unkeyed IUnitOfWork routes each Gateway scope to its own Module's.
    private ServiceDescriptor Own(ServiceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _modules.Record(Module, descriptor);
        if (descriptor.ServiceType != typeof(IUnitOfWork) || descriptor.IsKeyedService)
        {
            return descriptor;
        }

        if (descriptor.ImplementationInstance is { } instance)
        {
            return new ServiceDescriptor(descriptor.ServiceType, Module, instance);
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return new ServiceDescriptor(
                descriptor.ServiceType,
                Module,
                (services, _) => factory(services),
                descriptor.Lifetime
            );
        }

        return new ServiceDescriptor(
            descriptor.ServiceType,
            Module,
            descriptor.ImplementationType!,
            descriptor.Lifetime
        );
    }
}
