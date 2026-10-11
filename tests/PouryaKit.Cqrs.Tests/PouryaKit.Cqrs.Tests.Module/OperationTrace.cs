namespace PouryaKit.Cqrs.Tests.Module;

/// <summary>Records, in order, what ran in one scope, so a test can assert the pipeline sequence.</summary>
public sealed class OperationTrace
{
    private readonly List<string> _steps = [];

    public IReadOnlyList<string> Steps => _steps;

    public void Add(string step) => _steps.Add(step);
}

/// <summary>Lets a test's stand-in validation step fail an operation whose type it cannot see.</summary>
public interface IValidatedOperation
{
    bool IsValid { get; }
}
