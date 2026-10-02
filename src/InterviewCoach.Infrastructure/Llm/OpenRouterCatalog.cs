using System.Globalization;
using System.Text.Json;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;

namespace InterviewCoach.Infrastructure.Llm;

/// <summary>Reads OpenRouter's public model list (GET /models). No key is needed, and nothing about the user is sent.</summary>
public sealed class OpenRouterCatalog(HttpClient http) : IOpenRouterCatalog
{
    public static readonly Uri ModelsUrl = new(AppSettings.OpenRouterBaseUrl + "/models");

    private IReadOnlyList<OpenRouterModel>? _cached;

    public async Task<IReadOnlyList<OpenRouterModel>> GetModelsAsync(bool refresh, CancellationToken ct = default)
    {
        if (!refresh && _cached is not null) return _cached;

        try
        {
            using var response = await http.GetAsync(ModelsUrl, ct);
            if (!response.IsSuccessStatusCode)
                throw new LlmException($"OpenRouter did not return its model list (HTTP {(int)response.StatusCode}). Try again in a moment.");
            var models = Parse(await response.Content.ReadAsStringAsync(ct));
            if (models.Count == 0)
                throw new LlmException("OpenRouter returned a model list with no usable models.");
            return _cached = models;
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"Could not reach OpenRouter to load its model list: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new LlmException($"OpenRouter's model list was not in the expected format: {ex.Message}", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException("Loading the OpenRouter model list timed out.");
        }
    }

    /// <summary>
    /// Keeps models that take text and answer in text (no image or audio generators). Entries ending ":batch" are the
    /// slower discounted batch versions of a model, which are not suited to a live session, so they are left out.
    /// </summary>
    public static IReadOnlyList<OpenRouterModel> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new JsonException("no \"data\" list");

        var models = new List<OpenRouterModel>();
        foreach (var item in data.EnumerateArray())
        {
            var id = Text(item, "id");
            if (string.IsNullOrWhiteSpace(id) || id.EndsWith(":batch", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TextOnly(item)) continue;

            var name = Text(item, "name");
            var context = item.TryGetProperty("context_length", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : 0;
            item.TryGetProperty("pricing", out var pricing);
            models.Add(new OpenRouterModel(id, string.IsNullOrWhiteSpace(name) ? id : name, context,
                PerMillion(pricing, "prompt"), PerMillion(pricing, "completion")));
        }
        return models.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? Text(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // A model with no architecture information is kept: better to show it than hide a usable one.
    private static bool TextOnly(JsonElement item)
    {
        if (!item.TryGetProperty("architecture", out var arch) || arch.ValueKind != JsonValueKind.Object) return true;
        return Lists(arch, "input_modalities", "text") && OnlyText(arch, "output_modalities");
    }

    private static bool Lists(JsonElement arch, string name, string wanted)
        => !arch.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array
           || list.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == wanted);

    private static bool OnlyText(JsonElement arch, string name)
        => !arch.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array
           || list.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && x.GetString() == "text");

    // OpenRouter publishes dollars per token as a string; the screen shows dollars per million tokens.
    // A negative value means "varies by request" in OpenRouter's catalog, which is shown as not listed.
    private static decimal? PerMillion(JsonElement pricing, string name)
    {
        if (pricing.ValueKind != JsonValueKind.Object || !pricing.TryGetProperty(name, out var v)) return null;
        var text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null;
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var perToken) || perToken < 0) return null;
        return Math.Round(perToken * 1_000_000m, 4);
    }
}
