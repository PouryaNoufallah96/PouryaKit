namespace PouryaKit.Testing;

/// <summary>
/// The base of a test against the hosted application: the data is reset before each test, so no test sees another's
/// rows. Tests that share one <typeparamref name="TFixture"/> must not run in parallel.
/// </summary>
public abstract class HostTest<TFixture>(TFixture fixture) : IAsyncLifetime
    where TFixture : HostFixture
{
    /// <summary>The fixture whose Host this test runs against.</summary>
    protected TFixture Fixture { get; } = fixture;

    /// <inheritdoc />
    public virtual async ValueTask InitializeAsync() =>
        await Fixture.ResetAsync(TestContext.Current.CancellationToken);

    /// <inheritdoc />
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
