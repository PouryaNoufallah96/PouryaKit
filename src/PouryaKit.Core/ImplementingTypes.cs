using System.Reflection;

namespace PouryaKit.Core;

/// <summary>
/// Finds the concrete types in given assemblies that implement an interface, derive from a class, or close a generic
/// definition. It is the only reflection Core offers.
/// </summary>
public static class ImplementingTypes
{
    /// <summary>
    /// The concrete types of <paramref name="assemblies"/> that <paramref name="contract"/> describes, each once, in
    /// ordinal order of their full names.
    /// </summary>
    /// <remarks>
    /// Input contract:
    /// <list type="bullet">
    /// <item><paramref name="contract"/> is an interface or a class, either non-generic or closed, and a type is
    /// returned when it can be assigned to it; or <paramref name="contract"/> is an open generic definition, such as
    /// <c>typeof(IHandler&lt;&gt;)</c>, and a type is returned when it implements, or derives from, a closed form of
    /// it.</item>
    /// <item>Only a concrete type is returned: a non-abstract class that is not an open generic definition, public or
    /// not, nested or not. Interfaces, abstract and static classes, structs and open generic classes are left out. A
    /// concrete class given as the contract is returned itself.</item>
    /// <item>Every type of every assembly is read, so an assembly whose types cannot all be loaded throws
    /// <see cref="ReflectionTypeLoadException"/>. An assembly given twice is read once.</item>
    /// </list>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="contract"/> is a generic parameter, or a generic type that is
    /// neither closed nor a generic definition.</exception>
    public static IReadOnlyList<Type> Of(Type contract, params IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(assemblies);
        if (contract.IsGenericParameter || (contract.ContainsGenericParameters && !contract.IsGenericTypeDefinition))
        {
            throw new ArgumentException($"{contract} is neither a closed type nor a generic definition.", nameof(contract));
        }

        return
        [
            .. assemblies
                .Distinct()
                .SelectMany(assembly => assembly.GetTypes())
                .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
                .Where(type => contract.IsGenericTypeDefinition ? Closes(type, contract) : type.IsAssignableTo(contract))
                .OrderBy(type => type.FullName, StringComparer.Ordinal),
        ];
    }

    private static bool Closes(Type type, Type definition) =>
        definition.IsInterface
            ? type.GetInterfaces().Any(implemented => implemented.IsGenericType && implemented.GetGenericTypeDefinition() == definition)
            : BaseTypes(type).Any(baseType => baseType.IsGenericType && baseType.GetGenericTypeDefinition() == definition);

    private static IEnumerable<Type> BaseTypes(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }
}
