namespace PouryaKit.Domain;

/// <summary>A business rule. Check it with <c>CheckRule</c>, which returns a failed Result, never an exception, when it is broken.</summary>
public interface IBusinessRule
{
    /// <summary>True when the rule does not hold.</summary>
    bool IsBroken();

    /// <summary>The Error a broken rule returns.</summary>
    Error Error { get; }
}

internal static class BusinessRule
{
    public static Result Check(IBusinessRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.IsBroken() ? rule.Error : Result.Success();
    }
}
