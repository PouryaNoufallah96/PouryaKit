namespace PouryaKit.Domain;

/// <summary>A named business reason an Operation did not succeed.</summary>
public sealed record Error
{
    private Error(string code, string message, ErrorKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(message);
        Code = code;
        Message = message;
        Kind = kind;
    }

    /// <summary>The stable name of the reason, such as <c>Product.NameRequired</c>.</summary>
    public string Code { get; }

    public string Message { get; }

    public ErrorKind Kind { get; }

    public static Error Validation(string code, string message) => new(code, message, ErrorKind.Validation);

    public static Error NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorKind.Forbidden);

    public static Error Unexpected(string code, string message) => new(code, message, ErrorKind.Unexpected);
}
