using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Triage.Engine;

namespace Triage.Copilot;

/// <summary>The JSON Schemas sent with each request. <see cref="Reader"/> enforces the same rules on whatever comes back.</summary>
internal static class Schemas
{
    public const string Classification = """
        {"type":"object","required":["category","priority","suggestedSkill","confidence","rationale"],
         "properties":{
           "category":{"type":"string","pattern":"^[a-z0-9-]{1,24}$"},
           "priority":{"enum":["Low","Normal","High","Urgent"]},
           "suggestedSkill":{"type":["string","null"],"pattern":"^[a-z0-9-]{1,24}$"},
           "confidence":{"type":"number","minimum":0,"maximum":1},
           "rationale":{"type":"string","minLength":1,"maxLength":200}}}
        """;

    public const string Summary = """
        {"type":"object","required":["summary","confidence","rationale"],
         "properties":{
           "summary":{"type":"string","minLength":1,"maxLength":800},
           "confidence":{"type":"number","minimum":0,"maximum":1},
           "rationale":{"type":"string","minLength":1,"maxLength":200}}}
        """;

    public const string Reply = """
        {"type":"object","required":["reply","confidence","rationale"],
         "properties":{
           "reply":{"type":"string","minLength":1,"maxLength":2000},
           "confidence":{"type":"number","minimum":0,"maximum":1},
           "rationale":{"type":"string","minLength":1,"maxLength":200}}}
        """;

    public const string Assignee = """
        {"type":"object","required":["agentId","confidence","rationale"],
         "properties":{
           "agentId":{"type":"string","format":"uuid"},
           "confidence":{"type":"number","minimum":0,"maximum":1},
           "rationale":{"type":"string","minLength":1,"maxLength":200}}}
        """;
}

/// <summary>Reads a model's answer field by field. The first problem is kept so it can be shown to the model on the one retry.</summary>
internal sealed class Reader
{
    private static readonly Regex SlugShape = new("^[a-z0-9-]{1,24}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly JsonElement _root;

    private Reader(JsonElement root, string? error)
    {
        _root = root;
        Error = error;
    }

    public string? Error { get; private set; }

    public static Reader Parse(string raw)
    {
        var s = (raw ?? "").Trim();
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return new Reader(default, "the answer was not a JSON object");
        }

        try
        {
            using var doc = JsonDocument.Parse(s[start..(end + 1)]);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? new Reader(doc.RootElement.Clone(), null)
                : new Reader(default, "the answer was not a JSON object");
        }
        catch (JsonException)
        {
            return new Reader(default, "the answer was not valid JSON");
        }
    }

    public string Text(string name, int max)
    {
        var v = Property(name);
        if (v is null)
        {
            return "";
        }

        if (v.Value.ValueKind != JsonValueKind.String)
        {
            return Fail(name, "must be a string");
        }

        var text = v.Value.GetString()!.Trim();
        return text.Length is >= 1 && text.Length <= max ? text : Fail(name, $"must be 1 to {max} characters");
    }

    public string? OptionalSlug(string name)
    {
        if (Error is not null)
        {
            return null;
        }

        if (!_root.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return v.ValueKind == JsonValueKind.String ? Slug(name, v.GetString()!) : FailNull(name, "must be a string or null");
    }

    public string RequiredSlug(string name)
    {
        var v = Property(name);
        if (v is null)
        {
            return "";
        }

        return v.Value.ValueKind == JsonValueKind.String ? Slug(name, v.Value.GetString()!) : Fail(name, "must be a string");
    }

    public double Confidence()
    {
        var v = Property("confidence");
        if (v is null)
        {
            return 0;
        }

        if (v.Value.ValueKind != JsonValueKind.Number || !v.Value.TryGetDouble(out var d) || d < 0 || d > 1)
        {
            Fail("confidence", "must be a number from 0 to 1");
            return 0;
        }

        return d;
    }

    public string Rationale() => Text("rationale", 200);

    public Priority Priority(string name)
    {
        var v = Property(name);
        if (v is null)
        {
            return Triage.Engine.Priority.Normal;
        }

        var text = v.Value.ValueKind == JsonValueKind.String ? v.Value.GetString() : null;
        foreach (var option in Enum.GetValues<Priority>())
        {
            if (string.Equals(option.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }
        }

        Fail(name, "must be Low, Normal, High or Urgent");
        return Triage.Engine.Priority.Normal;
    }

    public Guid Id(string name)
    {
        var v = Property(name);
        if (v is null)
        {
            return Guid.Empty;
        }

        if (v.Value.ValueKind == JsonValueKind.String && Guid.TryParse(v.Value.GetString(), out var id))
        {
            return id;
        }

        Fail(name, "must be an id from the list");
        return Guid.Empty;
    }

    public void Reject(string message) => Error ??= message;

    private JsonElement? Property(string name)
    {
        if (Error is not null)
        {
            return null;
        }

        if (_root.TryGetProperty(name, out var v))
        {
            return v;
        }

        Error = $"'{name}' is missing";
        return null;
    }

    private string Slug(string name, string raw)
    {
        var slug = raw.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
        return SlugShape.IsMatch(slug) ? slug : Fail(name, "must be lowercase letters, numbers and hyphens, up to 24 characters");
    }

    private string Fail(string name, string why)
    {
        Error ??= $"'{name}' {why}";
        return "";
    }

    private string? FailNull(string name, string why)
    {
        Error ??= $"'{name}' {why}";
        return null;
    }
}

/// <summary>Turns a ticket into the redacted, size-limited text a model is allowed to see.</summary>
internal static class TicketText
{
    private const int MaxSubject = 200;

    public static string Build(Ticket ticket, int budget)
    {
        var head = new StringBuilder();
        head.Append("Subject: ").AppendLine(Clip(Redactor.Redact(Flat(ticket.Subject)), MaxSubject));
        head.AppendLine($"Current priority: {ticket.Priority}");
        head.AppendLine($"Status: {ticket.Status}");
        if (ticket.Tags.Count > 0)
        {
            head.AppendLine("Tags: " + string.Join(", ", ticket.Tags));
        }

        head.Append("Messages (oldest first):");

        var room = budget - head.Length - 1 - OmittedMarker.Length - 1;
        var kept = new List<string>();
        var omitted = false;
        for (var i = ticket.Messages.Count - 1; i >= 0; i--)
        {
            var m = ticket.Messages[i];
            var line = $"[{m.Author}] {Redactor.Redact(Flat(m.Text))}";
            if (line.Length + 1 > room)
            {
                if (kept.Count == 0 && room > 20)
                {
                    kept.Add(Clip(line, room - 1));
                }

                omitted = true;
                break;
            }

            kept.Add(line);
            room -= line.Length + 1;
        }

        kept.Reverse();
        var body = new StringBuilder(head.ToString());
        if (omitted)
        {
            body.Append('\n').Append(OmittedMarker);
        }

        foreach (var line in kept)
        {
            body.Append('\n').Append(line);
        }

        return body.ToString();
    }

    public const string OmittedMarker = "[earlier messages omitted]";

    private static string Flat(string text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";
}
