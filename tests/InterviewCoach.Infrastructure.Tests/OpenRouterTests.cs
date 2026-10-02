using System.Net;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Llm;
using InterviewCoach.Infrastructure.Settings;
using Microsoft.Extensions.AI;

namespace InterviewCoach.Infrastructure.Tests;

public class OpenRouterTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory();
    public void Dispose() => _dir.Delete(true);

    // A trimmed copy of the shape OpenRouter returns from GET /models.
    private const string Catalog = """
    {
      "data": [
        { "id": "anthropic/claude-sonnet-5.5", "name": "Anthropic: Claude Sonnet 5.5", "context_length": 1000000,
          "architecture": { "input_modalities": ["text", "image"], "output_modalities": ["text"] },
          "pricing": { "prompt": "0.000002", "completion": "0.00001" } },
        { "id": "anthropic/claude-sonnet-5.5:batch", "name": "Anthropic: Claude Sonnet 5.5 (batch)", "context_length": 1000000,
          "architecture": { "input_modalities": ["text"], "output_modalities": ["text"] },
          "pricing": { "prompt": "0.000001", "completion": "0.000005" } },
        { "id": "google/image-maker", "name": "Google: Image Maker", "context_length": 65536,
          "architecture": { "input_modalities": ["text"], "output_modalities": ["image", "text"] },
          "pricing": { "prompt": "0.0000005", "completion": "0.000003" } },
        { "id": "deepseek/deepseek-v4-flash", "name": "DeepSeek: V4 Flash", "context_length": 1048576,
          "architecture": { "input_modalities": ["text"], "output_modalities": ["text"] },
          "pricing": { "prompt": "0.000000028", "completion": "0.000000056" } },
        { "id": "vendor/free-one", "name": "Vendor: Free One", "context_length": 32768,
          "pricing": { "prompt": "0", "completion": "0" } },
        { "id": "vendor/varies", "name": "Vendor: Varies", "context_length": 8192,
          "pricing": { "prompt": "-1", "completion": "-1" } },
        { "id": "vendor/no-pricing", "context_length": 4096 }
      ]
    }
    """;

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("no network");
    }

    // ---- Parsing the catalog

    [Fact]
    public void The_catalog_keeps_text_models_and_leaves_out_batch_copies_and_image_generators()
    {
        var models = OpenRouterCatalog.Parse(Catalog);

        Assert.DoesNotContain(models, m => m.Id.EndsWith(":batch"));
        Assert.DoesNotContain(models, m => m.Id == "google/image-maker");
        Assert.Contains(models, m => m.Id == "anthropic/claude-sonnet-5.5");
        Assert.Contains(models, m => m.Id == "deepseek/deepseek-v4-flash");
    }

    [Fact]
    public void Prices_are_converted_from_dollars_per_token_to_dollars_per_million()
    {
        var models = OpenRouterCatalog.Parse(Catalog).ToDictionary(m => m.Id);

        Assert.Equal(2m, models["anthropic/claude-sonnet-5.5"].PromptPerMillion);
        Assert.Equal(10m, models["anthropic/claude-sonnet-5.5"].CompletionPerMillion);
        Assert.Equal(0.028m, models["deepseek/deepseek-v4-flash"].PromptPerMillion);
        Assert.Equal(0.056m, models["deepseek/deepseek-v4-flash"].CompletionPerMillion);
        Assert.True(models["vendor/free-one"].IsFree);
    }

    [Fact]
    public void A_model_with_a_variable_or_missing_price_is_listed_with_no_price()
    {
        var models = OpenRouterCatalog.Parse(Catalog).ToDictionary(m => m.Id);

        Assert.Null(models["vendor/varies"].PromptPerMillion);
        Assert.Null(models["vendor/no-pricing"].PromptPerMillion);
        Assert.Equal("vendor/no-pricing", models["vendor/no-pricing"].Name); // no name given: the id is shown
    }

    [Fact]
    public void The_catalog_is_sorted_by_name()
    {
        var names = OpenRouterCatalog.Parse(Catalog).Select(m => m.Name).ToList();

        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), names);
    }

    [Fact]
    public void A_reply_that_is_not_a_model_list_is_rejected()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => OpenRouterCatalog.Parse("{ \"oops\": 1 }"));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => OpenRouterCatalog.Parse("not json"));
    }

    // ---- Fetching the catalog

    [Fact]
    public async Task The_list_is_fetched_from_the_public_models_address_and_kept_for_the_rest_of_the_run()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Catalog);
        var catalog = new OpenRouterCatalog(new HttpClient(handler));

        var first = await catalog.GetModelsAsync(refresh: false);
        var second = await catalog.GetModelsAsync(refresh: false);

        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://openrouter.ai/api/v1/models", handler.LastUri!.ToString());
        Assert.Same(first, second);
    }

    [Fact]
    public async Task Refresh_asks_again()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Catalog);
        var catalog = new OpenRouterCatalog(new HttpClient(handler));

        await catalog.GetModelsAsync(refresh: false);
        await catalog.GetModelsAsync(refresh: true);

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task A_server_error_becomes_a_plain_message()
    {
        var catalog = new OpenRouterCatalog(new HttpClient(new StubHandler(HttpStatusCode.ServiceUnavailable, "down")));

        var ex = await Assert.ThrowsAsync<LlmException>(() => catalog.GetModelsAsync(refresh: false));

        Assert.Contains("503", ex.Message);
    }

    [Fact]
    public async Task No_network_becomes_a_plain_message()
    {
        var catalog = new OpenRouterCatalog(new HttpClient(new FailingHandler()));

        var ex = await Assert.ThrowsAsync<LlmException>(() => catalog.GetModelsAsync(refresh: false));

        Assert.Contains("Could not reach OpenRouter", ex.Message);
    }

    [Fact]
    public async Task Garbled_json_becomes_a_plain_message_and_is_not_kept()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "<html>captive portal</html>");
        var catalog = new OpenRouterCatalog(new HttpClient(handler));

        await Assert.ThrowsAsync<LlmException>(() => catalog.GetModelsAsync(refresh: false));
        await Assert.ThrowsAsync<LlmException>(() => catalog.GetModelsAsync(refresh: false));

        Assert.Equal(2, handler.Calls); // a failure is not cached
    }

    // ---- The provider itself

    [Fact]
    public void Using_OpenRouter_without_a_key_says_what_to_do()
    {
        const string name = "OPENROUTER_API_KEY";
        var before = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, null);
            var ex = Assert.Throws<LlmException>(() =>
                new ChatClientFactory().Create(new AppSettings { Provider = LlmProvider.OpenRouter }, "anthropic/claude-sonnet-5.5"));

            Assert.Contains("OpenRouter", ex.Message);
            Assert.Contains("OPENROUTER_API_KEY", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, before);
        }
    }

    [Fact]
    public void With_a_key_a_client_is_built_and_reused_per_model()
    {
        var settings = new AppSettings { Provider = LlmProvider.OpenRouter, OpenRouterApiKey = "sk-or-test" };
        var factory = new ChatClientFactory();

        var a = factory.Create(settings, "deepseek/deepseek-v4-flash");

        Assert.NotNull(a);
        Assert.Same(a, factory.Create(settings, "deepseek/deepseek-v4-flash"));
        Assert.NotSame(a, factory.Create(settings, "anthropic/claude-haiku-4.5"));
    }

    [Fact]
    public void Anthropic_only_features_are_not_sent_through_OpenRouter()
    {
        var settings = new AppSettings { Provider = LlmProvider.OpenRouter, ThinkingEffort = ThinkingEffort.Low };
        const string prompt = "Guidance\n=== THE CANDIDATE AND THE QUESTION ===\n<candidate_resume>\nme\n</candidate_resume>\nPer-call text";

        var message = LlmService.BuildSystemMessage(settings, prompt);

        Assert.Single(message.Contents);
        Assert.Null(LlmService.OptionsFor(settings, "anthropic/claude-sonnet-5.5").Reasoning);
    }

    [Fact]
    public void The_OpenRouter_key_is_stored_encrypted_and_comes_back()
    {
        var path = Path.Combine(_dir.FullName, "settings.json");
        var store = new SettingsStore(path);
        var settings = store.Current.Clone();
        settings.Provider = LlmProvider.OpenRouter;
        settings.OpenRouterApiKey = "sk-or-v1-secret-789";
        settings.SetModel(LlmProvider.OpenRouter, LlmRole.Coach, "google/gemini-3.5-flash-lite");
        store.Save(settings);

        var onDisk = File.ReadAllText(path);
        Assert.DoesNotContain("sk-or-v1-secret-789", onDisk);
        Assert.Contains("\"OpenRouter\"", onDisk);

        var reloaded = new SettingsStore(path).Current;
        Assert.Equal("sk-or-v1-secret-789", reloaded.OpenRouterApiKey);
        Assert.Equal(LlmProvider.OpenRouter, reloaded.Provider);
        Assert.Equal("google/gemini-3.5-flash-lite", reloaded.ModelFor(LlmRole.Coach));
        Assert.Equal("claude-sonnet-5-5", reloaded.ModelFor(LlmProvider.Anthropic, LlmRole.Coach));
    }

    [Fact]
    public void A_settings_file_from_before_OpenRouter_existed_opens_with_the_default_OpenRouter_models()
    {
        var path = Path.Combine(_dir.FullName, "settings.json");
        File.WriteAllText(path, "{ \"Provider\": \"Anthropic\", \"CoachModel\": \"claude-opus-5-5\" }");

        var s = new SettingsStore(path).Current;

        Assert.Equal("claude-opus-5-5", s.ModelFor(LlmRole.Coach));
        Assert.Equal(AppSettings.DefaultOpenRouterModel, s.ModelFor(LlmProvider.OpenRouter, LlmRole.Coach));
        Assert.Null(s.OpenRouterApiKey);
    }
}
