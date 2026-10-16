using System.Text;
using System.Text.RegularExpressions;

namespace PouryaKit.ArchitectureTests;

/// <summary>The code of a PostgreSQL text, as the structure rules read it.</summary>
internal static partial class Sql
{
    /// <summary>
    /// <paramref name="sql"/> with its <c>--</c> and <c>/* */</c> comments replaced by a space and the bodies of its
    /// single-quoted strings removed (the quotes stay). A dollar-quoted block (<c>$$ … $$</c>, <c>$tag$ … $tag$</c>) is
    /// replaced by a space too, unless <paramref name="keepDollarQuoted"/>, when its body is read as code. Double-quoted
    /// identifiers stay as they are.
    /// </summary>
    public static string Code(string sql, bool keepDollarQuoted)
    {
        var code = new StringBuilder(sql.Length);
        var at = 0;
        while (at < sql.Length)
        {
            if (sql.AsSpan(at).StartsWith("--"))
            {
                var end = sql.IndexOf('\n', at);
                at = end < 0 ? sql.Length : end;
                code.Append(' ');
            }
            else if (sql.AsSpan(at).StartsWith("/*"))
            {
                at = Closed(sql, at, "*/", from: at + 2);
                code.Append(' ');
            }
            else if (sql[at] is '\'' or '"')
            {
                var quote = sql[at];
                var end = at + 1;
                while (end < sql.Length)
                {
                    if (sql[end] == quote)
                    {
                        // A doubled quote stands for one quote inside.
                        if (end + 1 < sql.Length && sql[end + 1] == quote)
                        {
                            end += 2;
                            continue;
                        }

                        break;
                    }

                    end++;
                }

                code.Append(quote == '"' ? sql[at..Math.Min(end + 1, sql.Length)] : "''");
                at = end + 1;
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
    /// <paramref name="code"/>, as <see cref="Code"/> returns it, with what each pair of parentheses holds replaced by
    /// spaces and the parentheses kept, so what is left is the statement's own clauses at the same positions.
    /// </summary>
    public static string TopLevel(string code)
    {
        var top = new StringBuilder(code.Length);
        var depth = 0;
        foreach (var character in code)
        {
            if (character == ')' && depth > 0)
            {
                depth--;
            }

            top.Append(depth > 0 ? ' ' : character);
            if (character == '(')
            {
                depth++;
            }
        }

        return top.ToString();
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
