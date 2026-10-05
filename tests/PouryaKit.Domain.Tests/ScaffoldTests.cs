using System.Reflection;

namespace PouryaKit.Domain.Tests;

// Proves the test pipeline reaches the Domain package. Ticket #6 replaces it with the Domain tests.
public class ScaffoldTests
{
    [Fact]
    public void Domain_package_is_referenced()
    {
        var assembly = Assembly.Load("PouryaKit.Domain");

        Assert.Equal("PouryaKit.Domain", assembly.GetName().Name);
    }
}
