using System.Reflection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;

namespace ModularMonolith.Tests.SameNamed;

/// <summary>
/// The assembly's only public surface. Its query and command are internal, as a Module's are, so the tests' own types
/// of the same names stay the only ones the tests can name.
/// </summary>
public static class SameNamedFeatures
{
    public static Assembly Assembly => typeof(SameNamedFeatures).Assembly;

    public static Query<string> Query(string key) => new HandlingAssemblyQuery(key);

    public static Command<string> Command() => new NameHandlingAssemblyCommand();
}
