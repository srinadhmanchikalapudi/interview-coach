using System.Text.Json;

namespace InterviewCoach.Infrastructure.Llm;

public static class JsonResponseParser
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Strips ```json fences and anything around the first complete JSON value, then deserializes.</summary>
    public static T Parse<T>(string raw)
    {
        var json = ExtractJson(raw);
        var value = JsonSerializer.Deserialize<T>(json, Options);
        return value ?? throw new JsonException("The model returned JSON null.");
    }

    /// <summary>
    /// Returns the first complete JSON object or array in the text. Walks the braces (ignoring any inside strings)
    /// instead of cutting at the last brace, so a stray extra "}" or commentary after the JSON does not break parsing,
    /// which would otherwise cost a whole repair call.
    /// </summary>
    public static string ExtractJson(string raw)
    {
        var text = raw.Trim();

        var fence = text.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var bodyStart = text.IndexOf('\n', fence);
            var fenceEnd = bodyStart < 0 ? -1 : text.IndexOf("```", bodyStart, StringComparison.Ordinal);
            if (bodyStart >= 0 && fenceEnd > bodyStart)
                text = text[(bodyStart + 1)..fenceEnd].Trim();
        }

        var start = text.IndexOfAny(['{', '[']);
        if (start < 0) return text;

        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;                 // skip the escaped character, including \"
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{' or '[': depth++; break;
                case '}' or ']':
                    if (--depth == 0) return text[start..(i + 1)];
                    break;
            }
        }

        // Never balanced (truncated or malformed): hand the rest to the deserializer so its error message is useful.
        return text[start..];
    }
}
