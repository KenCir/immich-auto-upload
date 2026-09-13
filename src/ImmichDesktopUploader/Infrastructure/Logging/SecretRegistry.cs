using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;

namespace ImmichDesktopUploader.Infrastructure.Logging;

// Keep retired keys until the application log queue has drained; a queued old-generation
// event can still contain an old key. Never expose this registry in diagnostic summaries.
public sealed class SecretRegistry
{
    private readonly object gate = new();
    private ImmutableArray<string> secrets = [];
    public void Register(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return;
        lock (gate) if (!secrets.Contains(secret)) secrets = secrets.Add(secret).OrderByDescending(s => s.Length).ToImmutableArray();
    }
    internal string Redact(string value)
    {
        ImmutableArray<string> current; lock (gate) current = secrets;
        foreach (var secret in current) value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return value;
    }
    public override string ToString() => "Log secret redaction registry";
}

// Sanitize at the final serialization boundary, on the sink worker. Walking JSON strings
// also covers escaped values, structured properties, message templates and exception text
// without corrupting JSON syntax (a numeric key must not replace a numeric JSON token).
internal sealed class RedactingJsonFormatter(SecretRegistry secrets) : ITextFormatter
{
    private readonly JsonFormatter formatter = new(renderMessage: true);
    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var original = new StringWriter(); formatter.Format(logEvent, original);
        using var document = JsonDocument.Parse(original.ToString());
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer)) Write(document.RootElement, json);
        output.Write(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)));
        output.WriteLine();
    }
    private void Write(JsonElement value, Utf8JsonWriter json)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                json.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                { json.WritePropertyName(secrets.Redact(property.Name)); Write(property.Value, json); }
                json.WriteEndObject(); break;
            case JsonValueKind.Array:
                json.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(item, json);
                json.WriteEndArray(); break;
            case JsonValueKind.String: json.WriteStringValue(secrets.Redact(value.GetString()!)); break;
            default: value.WriteTo(json); break;
        }
    }
}
