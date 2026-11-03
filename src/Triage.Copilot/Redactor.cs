using System.Text.RegularExpressions;

namespace Triage.Copilot;

/// <summary>
/// Removes email addresses and phone numbers before any text leaves the process.
/// It does not find names, street addresses or card numbers, and a long order number can be mistaken for a phone number.
/// </summary>
public static class Redactor
{
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // International numbers (+44 20 7946 0958) or North American shapes (555-123-4567, (555) 123 4567, 5551234567).
    private static readonly Regex Phone = new(@"(?<!\w)(?:\+\d[\d\s().\-]{7,16}\d|(?:\(\d{3}\)|\d{3})[\s.\-]?\d{3}[\s.\-]?\d{4})(?!\w)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return Phone.Replace(Email.Replace(text, "[email]"), "[phone]");
    }
}
