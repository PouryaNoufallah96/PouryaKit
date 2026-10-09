using System.Reflection;

namespace PouryaKit.Core;

/// <summary>
/// Finds the types in given assemblies that implement an interface, derive from a class, or close a generic definition:
/// the concrete ones a registration registers, and every candidate it validates first. It is the only reflection Core
/// offers.
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
        ThrowIfNotAContract(contract, assemblies);
        return
        [
            .. assemblies
                .Distinct()
                .SelectMany(assembly => assembly.GetTypes())
                .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
                .Where(type => Describes(contract, type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Every class of <paramref name="assemblies"/> that <paramref name="contract"/> describes, abstract classes and
    /// open generic definitions included, each once, in ordinal order of their full names: the candidates a
    /// registration validates, so it can reject one that <see cref="Of"/> would leave out silently.
    /// </summary>
    /// <remarks>
    /// Input contract:
    /// <list type="bullet">
    /// <item><paramref name="contract"/> is read as <see cref="Of"/> reads it: an interface or a class, either
    /// non-generic or closed, and a class is returned when it can be assigned to it; or an open generic definition,
    /// and a class is returned when it implements, or derives from, a form of it, closed or open.</item>
    /// <item>Every class is returned, abstract, static or open generic, public or not, nested or not, compiler-generated
    /// or not. Interfaces and structs are left out. A non-generic or closed class given as the contract is returned
    /// itself.</item>
    /// <item>Every type of every assembly is read, so an assembly whose types cannot all be loaded throws
    /// <see cref="ReflectionTypeLoadException"/>. An assembly given twice is read once.</item>
    /// </list>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="contract"/> is a generic parameter, or a generic type that is
    /// neither closed nor a generic definition.</exception>
    public static IReadOnlyList<Type> Candidates(Type contract, params IEnumerable<Assembly> assemblies)
    {
        ThrowIfNotAContract(contract, assemblies);
        return
        [
            .. assemblies
                .Distinct()
                .SelectMany(assembly => assembly.GetTypes())
                .Where(type => type.IsClass)
                .Where(type => Describes(contract, type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal),
        ];
    }

    private static void ThrowIfNotAContract(Type contract, IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(assemblies);
        if (contract.IsGenericParameter || (contract.ContainsGenericParameters && !contract.IsGenericTypeDefinition))
        {
            throw new ArgumentException($"{contract} is neither a closed type nor a generic definition.", nameof(contract));
        }
    }

    private static bool Describes(Type contract, Type type) =>
        contract.IsGenericTypeDefinition ? Closes(type, contract) : type.IsAssignableTo(contract);

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
