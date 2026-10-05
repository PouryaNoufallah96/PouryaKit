using System.Reflection;

namespace PouryaKit.Domain.Tests;

// Placeholder until ticket #6 adds the Domain tests; a test project with no tests fails `dotnet test`.
public class ScaffoldTests
{
    [Fact]
    public void Domain_package_is_referenced()
    {
        var assembly = Assembly.Load("PouryaKit.Domain");

        Assert.Equal("PouryaKit.Domain", assembly.GetName().Name);
    }
}
