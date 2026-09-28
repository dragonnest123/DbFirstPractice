using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shared.Utils;

/// <summary>
/// One JSON object per line. Payload-like fields never reach the sink: they are
/// redacted, not truncated, so a sensitive value cannot leak through a partial
/// log line.
/// </summary>
public static class StructuredLog
{
    public const string Redacted = "[redacted]";

    public static readonly IReadOnlySet<string> ForbiddenFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "authorization", "body", "callback", "callbackBody", "callback_body", "capability",
        "credential", "credentials", "hmac", "password", "payload", "providerMessage",
        "provider_message", "raw", "receipt", "receiptBody", "receipt_body", "reason",
        "secret", "signature", "token"
    };

    private static readonly Regex Jwt = new(
        @"\beyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Lock ConsoleLock = new();

    public static void Write(string eventName, IReadOnlyDictionary<string, object?>? fields = null)
    {
        var record = new StringBuilder();
        var seconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        record.Append("{\"ts\":").Append(seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        record.Append(",\"event\":").Append(JsonSerializer.Serialize(eventName));

        if (fields is not null)
        {
            foreach (var (key, value) in fields)
            {
                record.Append(',').Append(JsonSerializer.Serialize(key)).Append(':');
                if (ForbiddenFields.Contains(key))
                    record.Append(JsonSerializer.Serialize(Redacted));
                else
                    record.Append(Scrub(value));
            }
        }

        record.Append('}');
        lock (ConsoleLock)
        {
            Console.Out.WriteLine(record.ToString());
            Console.Out.Flush();
        }
    }

    private static string Scrub(object? value) => value switch
    {
        null => "null",
        bool or int or long or short or byte or uint or ulong or double or float or decimal
            => JsonSerializer.Serialize(value),
        string text => JsonSerializer.Serialize(Jwt.Replace(text, Redacted)),
        IEnumerable<string> items => "[" + string.Join(",", items.Select(i => Scrub(i))) + "]",
        _ => JsonSerializer.Serialize(Jwt.Replace(value.ToString() ?? "", Redacted))
    };
}
