using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace DotNotes.Core.Tasks;

/// <summary>
/// Static parse/serialize for task notes (frontmatter + Backlog.md-style
/// body sections), per docs/features/tasks-kanban/PLAN.md §2/§3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Frontmatter</b> is parsed with YamlDotNet (structure only) but
/// always <i>rebuilt from scratch</i> on <see cref="Serialize"/> by
/// <see cref="TaskFrontmatterData"/>'s field values, in Backlog.md's fixed
/// key order, using this type's own quoting rules - YamlDotNet's emitter is
/// never used for output (see the csproj comment on the YamlDotNet
/// reference for why). Because the rebuild is a deterministic function of
/// the field values, parsing a file this type previously wrote and
/// serializing it again with no edits reproduces the exact same text
/// (idempotent) - that is what "byte-stable when nothing changed" means in
/// practice for files this app owns. A hand-written file in a different
/// (but still valid) YAML style round-trips to an equivalent, not
/// necessarily byte-identical, frontmatter block.
/// </para>
/// <para>
/// <b>Unknown keys</b> (and known keys whose value has an unexpected
/// shape or can't be parsed) are captured as their raw source text - the
/// lines from the key up to the next top-level entry, see
/// <c>FrontmatterSegments</c> - and re-emitted verbatim (LF line
/// endings) after the known keys, in original order; see
/// <see cref="TaskFrontmatterField"/>.
/// </para>
/// <para>
/// <b>The body</b> is never rebuilt - it is edited in place, by character
/// span, by the section helpers in the other half of this partial class
/// (TaskMarkdownSections.cs), so any content this feature doesn't
/// recognise (DoD, Comments, arbitrary prose) is left byte-for-byte
/// untouched. Fenced-code-block exclusion for marker/heading detection
/// (PLAN §2's "if feasible" note) is not implemented in v1 - a heading- or
/// marker-shaped line inside a code fence would be (mis)treated as a real
/// section; this is a documented, accepted limitation given the feature's
/// scope.
/// </para>
/// </remarks>
public static partial class TaskMarkdown
{
    private static readonly Regex FrontmatterPattern = new(
        @"\A﻿?---[ \t]*\r?\n(?<fm>.*?)\r?\n---[ \t]*(?:\r?\n|\z)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-dd",
    };

    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "id", "title", "status", "assignee", "reporter", "created_date", "updated_date",
        "labels", "milestone", "dependencies", "priority", "ordinal",
    };

    /// <summary>
    /// Cheap, allocation-light rejection test for notes that plainly can't
    /// be tasks: the content (after an optional BOM) doesn't even start
    /// with a frontmatter delimiter. Used by <see cref="InMemoryTaskIndex"/>
    /// to avoid running the full YAML parse on every note in the vault.
    /// </summary>
    public static bool QuickLooksLikeFrontmatter(string content)
    {
        var span = content.AsSpan();
        if (span.Length > 0 && span[0] == '﻿')
        {
            span = span[1..];
        }

        return span.StartsWith("---", StringComparison.Ordinal);
    }

    /// <summary>
    /// <see langword="true"/> if <paramref name="content"/> is a task note:
    /// a leading frontmatter block (optionally after a BOM) that parses as
    /// a YAML mapping with non-empty scalar <c>id</c> and <c>status</c>
    /// keys, per docs/features/tasks-kanban/PLAN.md §2's "Detection" rule.
    /// </summary>
    public static bool IsTask(string content) => TryParse(content, out _);

    /// <summary>Parses <paramref name="content"/> as a task note. See <see cref="IsTask"/> for the detection rule.</summary>
    public static bool TryParse(string content, out TaskDocument? document) =>
        TryParseCore(content, requireTaskKeys: true, out document);

    /// <summary>
    /// Parses any leading YAML-mapping frontmatter block, even one lacking
    /// <c>id</c>/<c>status</c> (those come back as empty strings). Used when
    /// converting an ordinary note that already has frontmatter (e.g.
    /// <c>tags:</c>) into a task, so its existing keys are merged rather
    /// than left behind as literal body text.
    /// </summary>
    public static bool TryParseAnyFrontmatter(string content, out TaskDocument? document) =>
        TryParseCore(content, requireTaskKeys: false, out document);

    private static bool TryParseCore(string content, bool requireTaskKeys, out TaskDocument? document)
    {
        document = null;

        if (string.IsNullOrEmpty(content) || !QuickLooksLikeFrontmatter(content))
        {
            return false;
        }

        var match = FrontmatterPattern.Match(content);
        if (!match.Success)
        {
            return false;
        }

        var hasBom = content.Length > 0 && content[0] == '﻿';
        var frontmatterText = match.Groups["fm"].Value;
        var body = content[match.Length..];

        YamlMappingNode? root;
        try
        {
            using var reader = new StringReader(frontmatterText);
            var yamlStream = new YamlStream();
            yamlStream.Load(reader);
            if (yamlStream.Documents.Count == 0)
            {
                return false;
            }

            root = yamlStream.Documents[0].RootNode as YamlMappingNode;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return false;
        }

        if (root is null)
        {
            return false;
        }

        string? id = null, title = null, status = null, reporter = null, milestone = null, priority = null;
        DateTimeOffset? createdDate = null, updatedDate = null;
        List<string> assignee = new(), labels = new(), dependencies = new();
        double? ordinal = null;
        var unknownFields = new List<TaskFrontmatterField>();

        var entries = root.Children.ToList();
        var segments = new FrontmatterSegments(frontmatterText, entries);

        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Key is not YamlScalarNode keyNode || keyNode.Value is null)
            {
                continue;
            }

            var key = keyNode.Value;
            var valueNode = entries[i].Value;

            // Known keys whose value has an unexpected shape (or can't be
            // parsed) are kept verbatim under their original key instead of
            // being dropped on the next rewrite; BuildFrontmatterText emits
            // that raw text in the key's normal slot whenever the typed
            // value is empty.
            void KeepRaw() => unknownFields.Add(new TaskFrontmatterField(key, segments.Get(i)));

            switch (key)
            {
                case "id": id = AsScalar(valueNode); break;
                case "status": status = AsScalar(valueNode); break;
                case "title": if (valueNode is YamlScalarNode) { title = AsScalar(valueNode); } else { KeepRaw(); } break;
                case "reporter": if (valueNode is YamlScalarNode) { reporter = AsScalar(valueNode); } else { KeepRaw(); } break;
                case "milestone": if (valueNode is YamlScalarNode) { milestone = AsScalar(valueNode); } else { KeepRaw(); } break;
                case "priority": if (valueNode is YamlScalarNode) { priority = AsScalar(valueNode); } else { KeepRaw(); } break;
                case "assignee": if (TryAsList(valueNode, out var parsedAssignee)) { assignee = parsedAssignee; } else { KeepRaw(); } break;
                case "labels": if (TryAsList(valueNode, out var parsedLabels)) { labels = parsedLabels; } else { KeepRaw(); } break;
                case "dependencies": if (TryAsList(valueNode, out var parsedDependencies)) { dependencies = parsedDependencies; } else { KeepRaw(); } break;
                case "created_date":
                    if (!TryParseDateField(valueNode, out createdDate)) { KeepRaw(); }
                    break;
                case "updated_date":
                    if (!TryParseDateField(valueNode, out updatedDate)) { KeepRaw(); }
                    break;
                case "ordinal":
                    if (!TryParseOrdinalField(valueNode, out ordinal)) { KeepRaw(); }
                    break;
                default:
                    KeepRaw();
                    break;
            }
        }

        if (requireTaskKeys && (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(status)))
        {
            return false;
        }

        document = new TaskDocument
        {
            Frontmatter = new TaskFrontmatterData
            {
                Id = id ?? string.Empty,
                Title = title,
                Status = status ?? string.Empty,
                Assignee = assignee,
                Reporter = string.IsNullOrEmpty(reporter) ? null : reporter,
                CreatedDate = createdDate,
                UpdatedDate = updatedDate,
                Labels = labels,
                Milestone = string.IsNullOrEmpty(milestone) ? null : milestone,
                Dependencies = dependencies,
                Priority = string.IsNullOrEmpty(priority) ? null : priority,
                Ordinal = ordinal,
                UnknownFields = unknownFields,
            },
            Body = body,
            HasBom = hasBom,
        };

        return true;
    }

    /// <summary>
    /// Rebuilds the full note content: a fresh frontmatter block (from
    /// <see cref="TaskDocument.Frontmatter"/>'s field values, per this
    /// type's remarks) followed by <see cref="TaskDocument.Body"/>
    /// unchanged.
    /// </summary>
    public static string Serialize(TaskDocument document)
    {
        var bom = document.HasBom ? "﻿" : string.Empty;
        var frontmatterText = BuildFrontmatterText(document.Frontmatter);
        return $"{bom}---\n{frontmatterText}\n---\n{document.Body}";
    }

    private static string? AsScalar(YamlNode node) => node is YamlScalarNode scalar ? scalar.Value : null;

    private static bool IsNullScalar(YamlScalarNode scalar) =>
        scalar.Style == YamlDotNet.Core.ScalarStyle.Plain && scalar.Value is null or "" or "~" or "null" or "Null" or "NULL";

    /// <summary>
    /// Reads a list-typed key. A plain scalar is a one-item list (never
    /// comma-split); a null/empty value is an empty list; a sequence of
    /// scalars is the list. Anything else (mapping, sequence with
    /// non-scalar items) returns <see langword="false"/> so the caller keeps
    /// the raw text.
    /// </summary>
    private static bool TryAsList(YamlNode node, out List<string> result)
    {
        result = new List<string>();
        switch (node)
        {
            case YamlScalarNode scalar:
                if (!IsNullScalar(scalar) && scalar.Value is not null)
                {
                    result.Add(scalar.Value);
                }

                return true;
            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    if (child is not YamlScalarNode { Value: not null } item)
                    {
                        result = new List<string>();
                        return false;
                    }

                    result.Add(item.Value);
                }

                return true;
            default:
                return false;
        }
    }

    private static bool TryParseDateField(YamlNode node, out DateTimeOffset? date)
    {
        date = null;
        if (node is not YamlScalarNode scalar)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(scalar.Value) || IsNullScalar(scalar))
        {
            return true;
        }

        date = ParseDate(scalar.Value);
        return date is not null;
    }

    private static bool TryParseOrdinalField(YamlNode node, out double? ordinal)
    {
        ordinal = null;
        if (node is not YamlScalarNode scalar)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(scalar.Value) || IsNullScalar(scalar))
        {
            return true;
        }

        if (double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed))
        {
            ordinal = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Slices the frontmatter block's raw text per top-level entry: each
    /// entry runs from the start of its key's line to the start of the next
    /// entry's line (or the end of the block), which is robust for any value
    /// shape (block/flow mappings, nested sequences, block and multi-line
    /// quoted scalars) without relying on YamlDotNet's node end marks -
    /// those are unreliable for collections. Trailing blank
    /// lines/whitespace are trimmed, CR/CRLF is normalised to LF (so the
    /// rebuilt block never has mixed line endings), and a consistently
    /// indented root mapping is dedented. A flow-style root
    /// (<c>{id: x, ...}</c>) is sliced key-start to key-start instead.
    /// </summary>
    private sealed class FrontmatterSegments
    {
        private readonly string _text;
        private readonly int[] _starts;
        private readonly bool _blockRoot;

        public FrontmatterSegments(string text, IReadOnlyList<KeyValuePair<YamlNode, YamlNode>> entries)
        {
            _text = text;
            _starts = new int[entries.Count];
            _blockRoot = true;
            for (var i = 0; i < entries.Count; i++)
            {
                var index = (int)Math.Min(entries[i].Key.Start.Index, text.Length);
                _starts[i] = index;
                if (!IsIndentOnly(LineStart(index), index))
                {
                    _blockRoot = false;
                }
            }
        }

        public string Get(int i)
        {
            var keyStart = _starts[i];
            string raw;
            var indent = 0;

            if (_blockRoot)
            {
                var start = LineStart(keyStart);
                indent = keyStart - start;
                var end = i + 1 < _starts.Length ? LineStart(_starts[i + 1]) : _text.Length;
                raw = end > start ? _text[start..end] : string.Empty;
            }
            else
            {
                var end = i + 1 < _starts.Length ? _starts[i + 1] : _text.Length;
                raw = end > keyStart ? _text[keyStart..end] : string.Empty;
                raw = raw.TrimEnd();
                if (raw.EndsWith(',') )
                {
                    raw = raw[..^1];
                }
                else if (i + 1 == _starts.Length && raw.EndsWith('}'))
                {
                    raw = raw[..^1];
                }
            }

            raw = LineEndings.Replace(raw, "\n").TrimEnd();

            if (indent > 0)
            {
                raw = string.Join('\n', raw.Split('\n').Select(line => Dedent(line, indent)));
            }

            return raw;
        }

        private static readonly Regex LineEndings = new(@"\r\n?", RegexOptions.Compiled);

        private static string Dedent(string line, int indent)
        {
            var remove = 0;
            while (remove < indent && remove < line.Length && line[remove] == ' ')
            {
                remove++;
            }

            return line[remove..];
        }

        private int LineStart(int index)
        {
            var i = Math.Min(index, _text.Length);
            while (i > 0 && _text[i - 1] != '\n' && _text[i - 1] != '\r')
            {
                i--;
            }

            return i;
        }

        private bool IsIndentOnly(int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                if (_text[i] is not (' ' or '\t'))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private static DateTimeOffset? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        foreach (var format in DateFormats)
        {
            if (DateTimeOffset.TryParseExact(
                    trimmed,
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var exact))
            {
                return exact;
            }
        }

        return DateTimeOffset.TryParse(
            trimmed,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var general)
            ? general
            : null;
    }

    private static string BuildFrontmatterText(TaskFrontmatterData fm)
    {
        var lines = new List<string>();

        // A known key whose on-disk value couldn't be represented in the
        // typed model (unparseable date/ordinal, non-scalar title, ...) is
        // held in UnknownFields under its own key; it is re-emitted here, in
        // the key's normal slot, only while the typed value is empty - so a
        // real edit always wins and the key is never written twice.
        void Slot(string key, IEnumerable<string>? typedLines)
        {
            var typed = typedLines?.ToList();
            if (typed is { Count: > 0 })
            {
                lines.AddRange(typed);
                return;
            }

            var raw = fm.UnknownFields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
            if (raw is not null)
            {
                lines.Add(raw.RawText);
            }
        }

        static IEnumerable<string>? ScalarLines(string key, string? value) =>
            string.IsNullOrEmpty(value) ? null : new[] { FormatScalarField(key, value) };

        static IEnumerable<string>? ListLines(string key, IReadOnlyList<string> items) =>
            items.Count == 0 ? null : FormatListField(key, items);

        lines.Add(FormatScalarField("id", fm.Id));

        // The title line is always written (an empty title is a legitimate
        // "derive from filename" state), unless a raw non-scalar title is
        // being preserved instead.
        if (string.IsNullOrEmpty(fm.Title) && fm.UnknownFields.Any(f => f.Key == "title"))
        {
            Slot("title", null);
        }
        else
        {
            lines.Add(FormatScalarField("title", fm.Title ?? string.Empty));
        }

        lines.Add(FormatScalarField("status", fm.Status));

        SlotEmptyList("assignee", fm.Assignee);
        Slot("reporter", ScalarLines("reporter", fm.Reporter));
        Slot("created_date", fm.CreatedDate is { } created ? new[] { FormatDateField("created_date", created) } : null);
        Slot("updated_date", fm.UpdatedDate is { } updated ? new[] { FormatDateField("updated_date", updated) } : null);
        SlotEmptyList("labels", fm.Labels);
        Slot("milestone", ScalarLines("milestone", fm.Milestone));
        SlotEmptyList("dependencies", fm.Dependencies);
        Slot("priority", ScalarLines("priority", fm.Priority));
        Slot("ordinal", fm.Ordinal is { } ordinal ? new[] { $"ordinal: {TaskOrdering.FormatOrdinal(ordinal)}" } : null);

        foreach (var unknown in fm.UnknownFields)
        {
            if (!KnownKeys.Contains(unknown.Key))
            {
                lines.Add(unknown.RawText);
            }
        }

        return string.Join("\n", lines);

        // Lists are always present: `key: []` when empty - unless a raw
        // preserved value exists for the key.
        void SlotEmptyList(string key, IReadOnlyList<string> items)
        {
            if (items.Count == 0 && !fm.UnknownFields.Any(f => f.Key == key))
            {
                lines.Add($"{key}: []");
                return;
            }

            Slot(key, ListLines(key, items));
        }
    }

    private static string FormatScalarField(string key, string value) => $"{key}: {YamlScalarFormatting.Format(value)}";

    private static string FormatDateField(string key, DateTimeOffset value) =>
        $"{key}: {YamlScalarFormatting.Quote(value.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))}";

    private static IEnumerable<string> FormatListField(string key, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            yield return $"{key}: []";
            yield break;
        }

        yield return $"{key}:";
        foreach (var item in items)
        {
            yield return $"  - {YamlScalarFormatting.Format(item)}";
        }
    }
}
