using Microsoft.Extensions.Time.Testing;
using PouryaKit.Domain;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

public sealed class TestClockTests
{
    [Fact]
    public void A_test_clock_stamps_domain_events_and_each_disposed_binding_restores_the_clock_bound_before()
    {
        var monday = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

        using (var clock = new TestClock(monday))
        {
            var raisedBefore = new ClockedDomainEvent();
            clock.Time.Advance(TimeSpan.FromHours(1));
            var raisedAfter = new ClockedDomainEvent();

            Assert.Equal(monday, raisedBefore.OccurredOn);
            Assert.Equal(monday.AddHours(1), raisedAfter.OccurredOn);

            var nested = new FakeTimeProvider(monday.AddDays(1));
            using (DomainClock.Use(nested))
            {
                Assert.Same(nested, DomainClock.Current);
            }

            Assert.Same(clock.Time, DomainClock.Current);
        }

        Assert.Same(TimeProvider.System, DomainClock.Current);
    }
}

internal sealed record ClockedDomainEvent : DomainEvent;
