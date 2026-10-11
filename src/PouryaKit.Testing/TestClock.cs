using Microsoft.Extensions.Time.Testing;
using PouryaKit.Domain;

namespace PouryaKit.Testing;

/// <summary>
/// A <see cref="FakeTimeProvider"/> bound as the <see cref="DomainClock"/> until disposed, for domain code a test runs
/// directly. Create it in the test method itself, with <c>using</c>; a binding made in a constructor or fixture is not
/// relied on to reach the test. An Operation sent through a Gateway reads the Host's registered
/// <see cref="TimeProvider"/> instead: replace that when the Host is built.
/// </summary>
public sealed class TestClock : IDisposable
{
    private readonly IDisposable _binding;

    /// <summary>Binds a new clock set to <paramref name="now"/> as the <see cref="DomainClock"/>.</summary>
    public TestClock(DateTimeOffset now)
    {
        Time = new FakeTimeProvider(now);
        _binding = DomainClock.Use(Time);
    }

    /// <summary>The bound clock; advance it to move time on.</summary>
    public FakeTimeProvider Time { get; }

    /// <inheritdoc />
    public void Dispose() => _binding.Dispose();
}
