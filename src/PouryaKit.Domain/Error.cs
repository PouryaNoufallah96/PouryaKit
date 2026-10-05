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

    public string Message { get; }

    public ErrorKind Kind { get; }

    /// <summary>
    /// The input a validation Error is about, such as <c>Name</c>, so a transport can group Errors by it;
    /// null on every other Error.
    /// </summary>
    public string? Target { get; }

    public static Error Validation(string code, string message) => new(code, message, ErrorKind.Validation);

    /// <summary>A validation Error about one input, named by <paramref name="target"/>.</summary>
    public static Error Validation(string code, string message, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        return new(code, message, ErrorKind.Validation, target);
    }

    public static Error NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorKind.Forbidden);

    public static Error Unexpected(string code, string message) => new(code, message, ErrorKind.Unexpected);
}
