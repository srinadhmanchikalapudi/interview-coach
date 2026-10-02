using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using InterviewCoach.App.ViewModels;
using InterviewCoach.App.Views;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;

namespace InterviewCoach.App.Tests;

public class OpenRouterSettingsUiTests
{
    private sealed class CapturingSettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current { get; private set; } = s;
        public void Save(AppSettings settings) { Current = settings; Changed?.Invoke(this, EventArgs.Empty); }
        public event EventHandler? Changed;
    }

    private static readonly OpenRouterModel Sonnet = new("anthropic/claude-sonnet-5.5", "Anthropic: Claude Sonnet 5.5", 1_000_000, 2m, 10m);
    private static readonly OpenRouterModel Haiku = new("anthropic/claude-haiku-4.5", "Anthropic: Claude Haiku 4.5", 200_000, 1m, 5m);
    private static readonly OpenRouterModel Flash = new("deepseek/deepseek-v4-flash", "DeepSeek: V4 Flash", 1_048_576, 0.028m, 0.056m);
    private static readonly OpenRouterModel Unlisted = new("vendor/unpriced", "Vendor: Unpriced", 0, null, null);

    private static SettingsViewModel New(out CapturingSettings store, FakeCatalog? catalog = null, AppSettings? settings = null)
    {
        store = new CapturingSettings(settings ?? new AppSettings());
        return new SettingsViewModel(store, new FakeLlmService(), catalog: catalog ?? new FakeCatalog(Sonnet, Haiku, Flash, Unlisted));
    }

    // ---- Provider switching keeps each provider's models

    [Fact]
    public void OpenRouter_is_one_of_the_providers_offered()
    {
        var vm = New(out _);

        Assert.Contains(vm.Providers, p => p.Value == LlmProvider.OpenRouter);
    }

    [Fact]
    public void Choosing_OpenRouter_shows_its_models_and_going_back_restores_the_others()
    {
        var vm = New(out _);
        vm.CoachModel = "claude-opus-5-5";

        vm.Provider = LlmProvider.OpenRouter;
        Assert.Equal("anthropic/claude-sonnet-5.5", vm.CoachModel);
        Assert.True(vm.IsOpenRouter);
        Assert.False(vm.IsAnthropic);
        Assert.Contains("anthropic/claude-haiku-4.5", vm.ModelSuggestions);

        vm.CoachModel = "deepseek/deepseek-v4-flash";
        vm.Provider = LlmProvider.Anthropic;
        Assert.Equal("claude-opus-5-5", vm.CoachModel);
        Assert.Contains("claude-sonnet-5-5", vm.ModelSuggestions);

        vm.Provider = LlmProvider.OpenRouter;
        Assert.Equal("deepseek/deepseek-v4-flash", vm.CoachModel);
    }

    [Fact]
    public void Save_keeps_both_sets_of_models_and_the_key()
    {
        var vm = New(out var store);
        vm.CoachModel = "claude-opus-5-5";
        vm.Provider = LlmProvider.OpenRouter;
        vm.OpenRouterApiKey = "  sk-or-typed  ";
        vm.CoachModel = "google/gemini-3.5-flash-lite";
        vm.QuestionGeneratorModel = "deepseek/deepseek-v4-flash";

        vm.SaveCommand.Execute(null);

        var saved = store.Current;
        Assert.Equal(LlmProvider.OpenRouter, saved.Provider);
        Assert.Equal("sk-or-typed", saved.OpenRouterApiKey);
        Assert.Equal("google/gemini-3.5-flash-lite", saved.ModelFor(LlmRole.Coach));
        Assert.Equal("deepseek/deepseek-v4-flash", saved.ModelFor(LlmRole.QuestionGenerator));
        Assert.Equal("claude-opus-5-5", saved.ModelFor(LlmProvider.Anthropic, LlmRole.Coach));
    }

    [Fact]
    public void Opening_settings_on_OpenRouter_shows_the_saved_OpenRouter_models()
    {
        var saved = new AppSettings { Provider = LlmProvider.OpenRouter, CoachModel = "claude-opus-5-5" };
        saved.SetModel(LlmProvider.OpenRouter, LlmRole.Coach, "google/gemini-3.5-flash-lite");

        var vm = New(out _, settings: saved);

        Assert.Equal("google/gemini-3.5-flash-lite", vm.CoachModel);
    }

    [Fact]
    public void Reloading_discards_edits_made_on_screen_for_either_provider()
    {
        var vm = New(out _);
        vm.Provider = LlmProvider.OpenRouter;
        vm.CoachModel = "something/typed";
        vm.Provider = LlmProvider.Anthropic;
        vm.CoachModel = "also-typed";

        vm.Load();

        Assert.Equal(LlmProvider.Anthropic, vm.Provider);
        Assert.Equal("claude-sonnet-5-5", vm.CoachModel);
        vm.Provider = LlmProvider.OpenRouter;
        Assert.Equal("anthropic/claude-sonnet-5.5", vm.CoachModel);
    }

    [Fact]
    public void The_key_hint_says_where_else_the_key_can_come_from()
    {
        var vm = New(out _);

        Assert.Contains("OPENROUTER_API_KEY", vm.OpenRouterKeyHint);
    }

    // ---- The model browser

    [Fact]
    public async Task Nothing_is_fetched_until_the_user_asks()
    {
        var catalog = new FakeCatalog(Sonnet);
        var vm = New(out _, catalog);
        vm.Provider = LlmProvider.OpenRouter;

        Assert.Equal(0, catalog.Calls);
        Assert.False(vm.HasCatalog);
        Assert.Empty(vm.CatalogModels);

        await vm.LoadModelsCommand.ExecuteAsync(null);

        Assert.Equal(1, catalog.Calls);
        Assert.True(vm.HasCatalog);
        Assert.Single(vm.CatalogModels);
    }

    [Fact]
    public async Task Loading_fills_the_list_and_says_how_many()
    {
        var vm = New(out _);

        await vm.LoadModelsCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.CatalogModels.Count);
        Assert.StartsWith("4 models", vm.CatalogStatus);
        Assert.False(vm.IsLoadingCatalog);
    }

    [Fact]
    public async Task A_failed_load_shows_the_reason_and_leaves_the_list_empty()
    {
        var vm = New(out _, new FakeCatalog { Fail = true });

        await vm.LoadModelsCommand.ExecuteAsync(null);

        Assert.Contains("Could not reach OpenRouter", vm.CatalogStatus);
        Assert.False(vm.HasCatalog);
        Assert.False(vm.IsLoadingCatalog);
    }

    [Fact]
    public async Task Without_a_catalog_loading_explains_instead_of_failing()
    {
        var vm = new SettingsViewModel(new CapturingSettings(new AppSettings()), new FakeLlmService());

        await vm.LoadModelsCommand.ExecuteAsync(null);

        Assert.Contains("not available", vm.CatalogStatus);
    }

    [Fact]
    public async Task Search_matches_every_word_in_the_name_or_the_id_ignoring_case()
    {
        var vm = New(out _);
        await vm.LoadModelsCommand.ExecuteAsync(null);

        vm.ModelFilter = "CLAUDE";
        Assert.Equal([Haiku.Id, Sonnet.Id], vm.CatalogModels.Select(m => m.Id).Order().ToArray());
        Assert.Equal("2 of 4 models match.", vm.CatalogStatus);

        vm.ModelFilter = "claude haiku";
        Assert.Equal([Haiku.Id], vm.CatalogModels.Select(m => m.Id).ToArray());

        vm.ModelFilter = "deepseek";
        Assert.Equal([Flash.Id], vm.CatalogModels.Select(m => m.Id).ToArray());

        vm.ModelFilter = "no such model";
        Assert.Empty(vm.CatalogModels);

        vm.ModelFilter = "";
        Assert.Equal(4, vm.CatalogModels.Count);
    }

    [Fact]
    public async Task Cheapest_first_puts_low_prices_first_and_unpriced_models_last()
    {
        var vm = New(out _);
        await vm.LoadModelsCommand.ExecuteAsync(null);

        vm.CheapestFirst = true;

        Assert.Equal([Flash.Id, Haiku.Id, Sonnet.Id, Unlisted.Id], vm.CatalogModels.Select(m => m.Id).ToArray());

        vm.CheapestFirst = false;
        Assert.Equal(4, vm.CatalogModels.Count);
        Assert.Equal(vm.CatalogModels.Select(m => m.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(), vm.CatalogModels.Select(m => m.Name).ToArray());
    }

    [Fact]
    public async Task The_selection_survives_searching_while_it_still_matches()
    {
        var vm = New(out _);
        await vm.LoadModelsCommand.ExecuteAsync(null);
        vm.SelectedCatalogModel = vm.CatalogModels.Single(m => m.Id == Haiku.Id);

        vm.ModelFilter = "claude";
        Assert.Equal(Haiku.Id, vm.SelectedCatalogModel?.Id);

        vm.ModelFilter = "deepseek";
        Assert.Null(vm.SelectedCatalogModel);
    }

    [Fact]
    public async Task The_use_buttons_wait_for_a_selection()
    {
        var vm = New(out _);
        await vm.LoadModelsCommand.ExecuteAsync(null);

        Assert.False(vm.UseForCoachCommand.CanExecute(null));
        Assert.False(vm.UseForQuestionGeneratorCommand.CanExecute(null));
        Assert.False(vm.UseForAllRolesCommand.CanExecute(null));

        vm.SelectedCatalogModel = vm.CatalogModels.First();

        Assert.True(vm.UseForCoachCommand.CanExecute(null));
        Assert.True(vm.UseForQuestionGeneratorCommand.CanExecute(null));
        Assert.True(vm.UseForAllRolesCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_selected_model_can_be_given_to_one_role_or_to_all_of_them()
    {
        var vm = New(out _);
        vm.Provider = LlmProvider.OpenRouter;
        await vm.LoadModelsCommand.ExecuteAsync(null);

        vm.SelectedCatalogModel = vm.CatalogModels.Single(m => m.Id == Flash.Id);
        vm.UseForQuestionGeneratorCommand.Execute(null);
        Assert.Equal(Flash.Id, vm.QuestionGeneratorModel);
        Assert.Equal("anthropic/claude-sonnet-5.5", vm.CoachModel);
        Assert.Contains("Click Save", vm.StatusMessage);

        vm.SelectedCatalogModel = vm.CatalogModels.Single(m => m.Id == Haiku.Id);
        vm.UseForCoachCommand.Execute(null);
        Assert.Equal(Haiku.Id, vm.CoachModel);
        Assert.Equal(Flash.Id, vm.QuestionGeneratorModel);

        vm.SelectedCatalogModel = vm.CatalogModels.Single(m => m.Id == Sonnet.Id);
        vm.UseForAllRolesCommand.Execute(null);
        Assert.All(new[] { vm.PlannerModel, vm.InterviewerModel, vm.QuestionGeneratorModel, vm.CoachModel, vm.DebriefModel }, m => Assert.Equal(Sonnet.Id, m));
    }

    [Fact]
    public async Task A_chosen_model_is_saved_for_OpenRouter_without_touching_the_Anthropic_models()
    {
        var vm = New(out var store);
        vm.Provider = LlmProvider.OpenRouter;
        await vm.LoadModelsCommand.ExecuteAsync(null);
        vm.SelectedCatalogModel = vm.CatalogModels.Single(m => m.Id == Flash.Id);
        vm.UseForCoachCommand.Execute(null);

        vm.SaveCommand.Execute(null);

        Assert.Equal(Flash.Id, store.Current.ModelFor(LlmProvider.OpenRouter, LlmRole.Coach));
        Assert.Equal("claude-sonnet-5-5", store.Current.ModelFor(LlmProvider.Anthropic, LlmRole.Coach));
    }

    // ---- The real view

    [Fact]
    public void The_settings_view_with_OpenRouter_selected_loads_without_binding_errors_and_shows_the_browser()
    {
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var vm = New(out _);
            vm.Provider = LlmProvider.OpenRouter;
            vm.LoadModelsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            var view = new SettingsView { DataContext = vm };
            view.Measure(new Size(1000, 4000));
            view.Arrange(new Rect(0, 0, 1000, 4000));
            view.UpdateLayout();

            Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "Choose a model" && IsShown(t));
            Assert.Contains(Descendants<Button>(view), b => Text(b) == "Load models" && IsShown(b));
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "OpenRouter API key" && IsShown(t));
            var list = Descendants<ListBox>(view).Single(l => IsShown(l));
            Assert.Equal(4, list.Items.Count);
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public void The_model_browser_and_key_box_are_hidden_for_the_other_providers()
    {
        WpfHost.Run(() =>
        {
            var vm = New(out _);
            var view = new SettingsView { DataContext = vm };
            view.Measure(new Size(1000, 4000));
            view.Arrange(new Rect(0, 0, 1000, 4000));
            view.UpdateLayout();

            Assert.DoesNotContain(Descendants<TextBlock>(view), t => t.Text == "Choose a model" && IsShown(t));
            Assert.DoesNotContain(Descendants<TextBlock>(view), t => t.Text == "OpenRouter API key" && IsShown(t));
        });
    }

    // IsVisible is false for any view that is not inside a window, so walk up and check each Visibility instead.
    private static bool IsShown(UIElement e)
    {
        for (DependencyObject? node = e; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private static string Text(Button b) => Descendants<TextBlock>(b).FirstOrDefault(t => !string.IsNullOrEmpty(t.Text) && t.Text.Length > 2)?.Text ?? "";

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
