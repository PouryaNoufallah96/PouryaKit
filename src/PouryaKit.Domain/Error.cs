namespace PouryaKit.Domain;

/// <summary>A named business reason an Operation did not succeed.</summary>
public sealed record Error
{
    private Error(string code, string message, ErrorKind kind, string? target = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(message);
        Code = code;
        Message = message;
        Kind = kind;
        Target = target;
    }

    /// <summary>The stable name of the reason, such as <c>Product.NameRequired</c>.</summary>
    public string Code { get; }

    /// <summary>The reason in words, for a person to read.</summary>
    public string Message { get; }

    /// <summary>What kind of reason this is, which a transport maps to its own codes.</summary>
    public ErrorKind Kind { get; }

    /// <summary>
    /// The input a validation Error is about, such as <c>Name</c>, so a transport can group Errors by it;
    /// null on every other Error.
    /// </summary>
    public string? Target { get; }

    /// <summary>A <see cref="ErrorKind.Validation"/> Error about no single input.</summary>
    public static Error Validation(string code, string message) => new(code, message, ErrorKind.Validation);

    /// <summary>A validation Error about one input, named by <paramref name="target"/>.</summary>
    public static Error Validation(string code, string message, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        return new(code, message, ErrorKind.Validation, target);
    }

    /// <summary>A <see cref="ErrorKind.NotFound"/> Error: something the Operation needs does not exist.</summary>
    public static Error NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);

    /// <summary>
    /// A <see cref="ErrorKind.Conflict"/> Error: the Operation conflicts with the current state, such as a unique value
    /// already in use or a stale version.
    /// </summary>
    public static Error Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);

    /// <summary>A <see cref="ErrorKind.Forbidden"/> Error: the Actor may not run the Operation.</summary>
    public static Error Forbidden(string code, string message) => new(code, message, ErrorKind.Forbidden);

    /// <summary>An <see cref="ErrorKind.Unexpected"/> Error: a failure no other kind describes.</summary>
    public static Error Unexpected(string code, string message) => new(code, message, ErrorKind.Unexpected);
}
