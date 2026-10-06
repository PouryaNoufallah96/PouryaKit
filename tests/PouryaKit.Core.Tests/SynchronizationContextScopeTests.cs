namespace PouryaKit.Core.Tests;

// Each test installs its own prior context and puts back whatever the runner had in finally, so no test depends on, or
// leaks into, the runner's context.
public class SynchronizationContextScopeTests
{
    private readonly SynchronizationContext prior = new();

    [Fact]
    public void The_scope_sets_the_given_context_or_none_and_restores_the_prior_one_when_it_ends()
    {
        var inner = new SynchronizationContext();

        WithPrior(() =>
        {
            Assert.Same(prior, SynchronizationContext.Current);
            using (SynchronizationContextScope.Enter(inner))
            {
                Assert.Same(inner, SynchronizationContext.Current);
            }

            Assert.Same(prior, SynchronizationContext.Current);
            using (SynchronizationContextScope.Enter())
            {
                Assert.Null(SynchronizationContext.Current);
            }

            Assert.Same(prior, SynchronizationContext.Current);
        });
    }

    [Fact]
    public void The_scope_restores_the_prior_context_when_an_exception_ends_it()
    {
        WithPrior(() =>
        {
            Assert.Same(prior, SynchronizationContext.Current);
            var thrown = Assert.Throws<InvalidOperationException>(ThrowInsideTheScope);

            Assert.Equal("inside the scope", thrown.Message);
            Assert.Same(prior, SynchronizationContext.Current);
        });

        static void ThrowInsideTheScope()
        {
            using var scope = SynchronizationContextScope.Enter();
            Assert.Null(SynchronizationContext.Current);
            throw new InvalidOperationException("inside the scope");
        }
    }

    [Fact]
    public void Disposing_a_scope_that_was_never_entered_changes_nothing()
    {
        WithPrior(() =>
        {
            default(SynchronizationContextScope).Dispose();

            Assert.Same(prior, SynchronizationContext.Current);
        });
    }

    private void WithPrior(Action test)
    {
        var runner = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(prior);
        try
        {
            test();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(runner);
        }
    }
}
