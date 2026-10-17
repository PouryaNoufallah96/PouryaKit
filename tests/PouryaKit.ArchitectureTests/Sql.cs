using System.Text;
using System.Text.RegularExpressions;

namespace PouryaKit.ArchitectureTests;

/// <summary>The code of a PostgreSQL text, as the structure rules read it.</summary>
internal static partial class Sql
{
    /// <summary>
    /// <paramref name="sql"/> with its <c>--</c> and <c>/* */</c> comments (nested ones as PostgreSQL nests them)
    /// replaced by a space and the bodies of its single-quoted strings removed (the quotes stay). A dollar-quoted block
    /// (<c>$$ … $$</c>, <c>$tag$ … $tag$</c>) is replaced by a space too, unless <paramref name="keepDollarQuoted"/>, when
    /// its body is read as code. Double-quoted identifiers stay as they are.
    /// </summary>
    public static string Code(string sql, bool keepDollarQuoted)
    {
        var code = new StringBuilder(sql.Length);
        var at = 0;
        while (at < sql.Length)
        {
            if (sql.AsSpan(at).StartsWith("--"))
            {
                var end = sql.AsSpan(at).IndexOfAny('\n', '\r');
                end = end < 0 ? -1 : at + end;
                at = end < 0 ? sql.Length : end;
                code.Append(' ');
            }
            else if (sql.AsSpan(at).StartsWith("/*"))
            {
                at = CommentEnd(sql, at);
                code.Append(' ');
            }
            else if (sql[at] is '\'' or '"')
            {
                var quote = sql[at];
                var end = Math.Min(QuoteEnd(sql, at), sql.Length);
                code.Append(quote == '"' ? sql[at..end] : "''");
                at = end;
            }
            else if ((at == 0 || !(char.IsLetterOrDigit(sql[at - 1]) || sql[at - 1] is '_' or '$')) && DollarTag().Match(sql, at) is { Success: true } tag)
            {
                if (keepDollarQuoted)
                {
                    code.Append(' ');
                    at += tag.Length;
                }
                else
                {
                    at = Closed(sql, at, tag.Value, from: at + tag.Length);
                    code.Append(' ');
                }
            }
            else
            {
                code.Append(sql[at++]);
            }
        }

        return code.ToString();
    }

    /// <summary>
    /// The tokens of <paramref name="sql"/>, its <c>--</c> comments dropped, or the first thing in it a rule over tokens
    /// does not read: a block comment, a <c>$</c> (a dollar-quoted block or a positional parameter), a backslash (an
    /// escape string's), a quote that never closes, or a character that starts no word, <c>@</c> parameter, quoted
    /// identifier, string, number or one of the punctuation marks <c>(),.;=&lt;&gt;!+-*/%:[]|~^</c>.
    /// </summary>
    public static (IReadOnlyList<SqlToken> Tokens, string? Fault) Tokens(string sql)
    {
        List<SqlToken> tokens = [];
        var at = 0;
        while (at < sql.Length)
        {
            var character = sql[at];
            var start = at;
            if (char.IsWhiteSpace(character))
            {
                at++;
            }
            else if (sql.AsSpan(at).StartsWith("--"))
            {
                var end = sql.AsSpan(at).IndexOfAny('\n', '\r');
                end = end < 0 ? -1 : at + end;
                at = end < 0 ? sql.Length : end;
            }
            else if (sql.AsSpan(at).StartsWith("/*"))
            {
                return (tokens, "holds a block comment");
            }
            else if (character is '\'' or '"')
            {
                at = QuoteEnd(sql, at);
                if (at > sql.Length)
                {
                    return (tokens, "holds a quote that never closes");
                }

                var body = sql[(start + 1)..(at - 1)].Replace($"{character}{character}", $"{character}", StringComparison.Ordinal);
                if (body.Contains('\\'))
                {
                    return (tokens, "holds a backslash");
                }

                tokens.Add(new(character == '"' ? SqlTokenKind.Quoted : SqlTokenKind.String, body));
            }
            else if (character == '@' && at + 1 < sql.Length && IsWordStart(sql[at + 1]))
            {
                at = WordEnd(sql, at + 1);
                tokens.Add(new(SqlTokenKind.Parameter, sql[start..at]));
            }
            else if (IsWordStart(character))
            {
                at = WordEnd(sql, at);
                tokens.Add(new(SqlTokenKind.Word, sql[start..at]));
            }
            else if (char.IsAsciiDigit(character))
            {
                at = WordEnd(sql, at);
                if (at + 1 < sql.Length && sql[at] == '.' && char.IsAsciiDigit(sql[at + 1]))
                {
                    at = WordEnd(sql, at + 1);
                }

                tokens.Add(new(SqlTokenKind.Number, sql[start..at]));
            }
            else if (Punctuation.Contains(character))
            {
                at++;
                tokens.Add(new(SqlTokenKind.Punctuation, sql[start..at]));
            }
            else
            {
                return (
                    tokens,
                    character switch
                    {
                        '$' => "holds a dollar sign",
                        '\\' => "holds a backslash",
                        _ => $"holds the character {character}",
                    }
                );
            }
        }

        return (tokens, null);
    }

    private const string Punctuation = "(),.;=<>!+-*/%:[]|~^";

    private static bool IsWordStart(char character) => char.IsAsciiLetter(character) || character == '_';

    private static int WordEnd(string sql, int at)
    {
        while (at < sql.Length && (char.IsAsciiLetterOrDigit(sql[at]) || sql[at] == '_'))
        {
            at++;
        }

        return at;
    }

    // The index just past the quote that closes the one at `at`, a doubled quote standing for one inside, or one past the
    // end of the text when it never closes.
    private static int QuoteEnd(string sql, int at)
    {
        var quote = sql[at];
        var end = at + 1;
        while (end < sql.Length)
        {
            if (sql[end] == quote)
            {
                if (end + 1 < sql.Length && sql[end + 1] == quote)
                {
                    end += 2;
                    continue;
                }

                return end + 1;
            }

            end++;
        }

        return sql.Length + 1;
    }

    // The index just past the `*/` that closes the block comment at `at`, counting the comments nested in it, or the end
    // of the text when it never closes.
    private static int CommentEnd(string sql, int at)
    {
        var depth = 0;
        while (at < sql.Length)
        {
            if (sql.AsSpan(at).StartsWith("/*"))
            {
                depth++;
                at += 2;
            }
            else if (sql.AsSpan(at).StartsWith("*/"))
            {
                at += 2;
                if (--depth == 0)
                {
                    return at;
                }
            }
            else
            {
                at++;
            }
        }

        return at;
    }

    // The index just past the first `closing` at or after `from`, or the end of the text when it never closes.
    private static int Closed(string sql, int at, string closing, int from)
    {
        var end = sql.IndexOf(closing, from, StringComparison.Ordinal);
        return end < 0 ? sql.Length : end + closing.Length;
    }

    [GeneratedRegex(@"\G\$(?:[A-Za-z_][A-Za-z0-9_]*)?\$")]
    private static partial Regex DollarTag();
}

/// <summary>What a <see cref="SqlToken"/> is.</summary>
internal enum SqlTokenKind
{
    /// <summary>A keyword or an identifier, unquoted.</summary>
    Word,

    /// <summary>A double-quoted identifier, its body as the token's text.</summary>
    Quoted,

    /// <summary>A Dapper parameter, <c>@name</c>.</summary>
    Parameter,

    /// <summary>A single-quoted string, its body as the token's text.</summary>
    String,

    /// <summary>A number.</summary>
    Number,

    /// <summary>One punctuation mark or operator character.</summary>
    Punctuation,
}

/// <summary>One token of a PostgreSQL text, as <see cref="Sql.Tokens"/> splits it.</summary>
internal readonly record struct SqlToken(SqlTokenKind Kind, string Text)
{
    /// <summary>Whether this is the unquoted word <paramref name="word"/>, in any case.</summary>
    public bool Is(string word) => Kind == SqlTokenKind.Word && Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this is the punctuation mark <paramref name="mark"/>.</summary>
    public bool Is(char mark) => Kind == SqlTokenKind.Punctuation && Text is [var only] && only == mark;

    /// <summary>Whether this can name a table, a column or an alias: a word or a double-quoted identifier.</summary>
    public bool IsName => Kind is SqlTokenKind.Word or SqlTokenKind.Quoted;

    /// <summary>The name this token gives, lower case, as the rules compare names.</summary>
    public string Name => Text.ToLowerInvariant();
}
