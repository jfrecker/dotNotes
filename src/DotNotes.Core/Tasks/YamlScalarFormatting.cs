using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DotNotes.Core.Tasks;

/// <summary>
/// Our own small YAML scalar quoting rules for <see cref="TaskMarkdown"/>'s
/// frontmatter emitter, matching Backlog.md's serializer layout: single-quote
/// any scalar that isn't a "plain safe" one, per
/// docs/features/tasks-kanban/PLAN.md's deliverable list (dates, values
/// starting with <c>@</c>, containing <c>": "</c>/<c>#</c>, with leading/
/// trailing whitespace, YAML-special words, or numeric-looking strings).
/// </summary>
internal static class YamlScalarFormatting
{
    private static readonly HashSet<string> SpecialWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "yes", "no", "on", "off", "null", "~",
    };

    private static readonly Regex DateLikePattern = new(
        @"^\d{4}-\d{2}-\d{2}([ T]\d{2}:\d{2}(:\d{2})?)?$",
        RegexOptions.Compiled);

    private const string LeadingSpecialChars = "-?:,[]{}#&*!|>'\"%@`";

    /// <summary>
    /// Formats <paramref name="value"/> as a plain scalar, single-quoted if
    /// it merely needs quoting, or YAML double-quoted (with <c>\n</c>,
    /// <c>\t</c>, <c>\uXXXX</c> escapes) if it contains a control or
    /// line-break character. Embedded line breaks are therefore never
    /// written raw (a raw one would be folded by YAML, and a raw
    /// <c>---</c> line could forge the closing fence and silently turn
    /// the task back into a plain note), and the parser round-trips the
    /// exact original value.
    /// </summary>
    public static string Format(string value)
    {
        value = ReplaceLoneSurrogates(value);
        if (RequiresDoubleQuote(value))
        {
            return DoubleQuote(value);
        }

        return RequiresSingleQuote(value) ? Quote(value) : value;
    }

    /// <summary>Single-quotes <paramref name="value"/>, escaping embedded <c>'</c> as <c>''</c> per YAML single-quote rules.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// <see langword="true"/> for values that can't be represented in a
    /// single-line plain or single-quoted scalar: any control character
    /// (incl. tab, CR/LF, NEL), U+2028/U+2029 and U+FEFF, or a
    /// <c>:</c> followed by non-space whitespace (<c>": "</c> alone is
    /// handled by single-quoting).
    /// </summary>
    public static bool RequiresDoubleQuote(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c) || c is '\u2028' or '\u2029' or '\uFEFF')
            {
                return true;
            }

            if (c == ':' && i + 1 < value.Length && value[i + 1] != ' ' && char.IsWhiteSpace(value[i + 1]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Double-quotes <paramref name="value"/> using YAML escapes for backslash, quote, and control/line-break characters.</summary>
    public static string DoubleQuote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c) || c is '\u2028' or '\u2029' or '\uFEFF')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    private static string ReplaceLoneSurrogates(string value)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var lone = char.IsHighSurrogate(c)
                ? !(i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                : char.IsLowSurrogate(c) && !(i > 0 && char.IsHighSurrogate(value[i - 1]));

            if (lone)
            {
                sb ??= new StringBuilder(value, 0, i, value.Length);
                sb.Append('\uFFFD');
            }
            else
            {
                sb?.Append(c);
            }
        }

        return sb?.ToString() ?? value;
    }

    public static bool RequiresSingleQuote(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        if (value != value.Trim())
        {
            return true;
        }

        if (SpecialWords.Contains(value))
        {
            return true;
        }

        if (DateLikePattern.IsMatch(value))
        {
            return true;
        }

        if (LooksNumeric(value))
        {
            return true;
        }

        if (value.Contains(": ", StringComparison.Ordinal) || value.EndsWith(':'))
        {
            return true;
        }

        if (value.Contains('#'))
        {
            return true;
        }

        return LeadingSpecialChars.IndexOf(value[0]) >= 0;
    }

    private static bool LooksNumeric(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}
