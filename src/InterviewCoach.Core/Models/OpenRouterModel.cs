using System.Globalization;

namespace InterviewCoach.Core.Models;

/// <summary>One model from OpenRouter's catalog. Prices are dollars per million tokens; null means OpenRouter did not publish one.</summary>
public sealed record OpenRouterModel(string Id, string Name, int ContextLength, decimal? PromptPerMillion, decimal? CompletionPerMillion)
{
    public bool IsFree => PromptPerMillion == 0 && CompletionPerMillion == 0;

    /// <summary>Input plus output price, used to put the cheapest models first. Models with no published price go last.</summary>
    public decimal CostRank => PromptPerMillion is { } p && CompletionPerMillion is { } c ? p + c : decimal.MaxValue;

    public string PriceLabel => IsFree
        ? "Free"
        : PromptPerMillion is { } p && CompletionPerMillion is { } c
            ? $"{Money(p)} in / {Money(c)} out per 1M tokens"
            : "Price not listed";

    public string ContextLabel => ContextLength >= 1_000_000
        ? $"{ContextLength / 1_000_000.0:0.#}M context"
        : ContextLength > 0 ? $"{ContextLength / 1000}K context" : "";

    /// <summary>The second line of a list row: price, then context size when known.</summary>
    public string Details => ContextLabel.Length == 0 ? PriceLabel : $"{PriceLabel}  ·  {ContextLabel}";

    private static string Money(decimal value) => "$" + value.ToString(value < 0.1m ? "0.000" : "0.00", CultureInfo.InvariantCulture);
}
