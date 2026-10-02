using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Settings;

namespace InterviewCoach.Infrastructure.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory();
    private string Path_ => Path.Combine(_dir.FullName, "settings.json");

    public void Dispose() => _dir.Delete(true);

    [Fact]
    public void Defaults_use_the_default_model_for_every_role()
    {
        var s = new SettingsStore(Path_).Current;

        Assert.All(Enum.GetValues<LlmRole>(), r => Assert.Equal("claude-sonnet-5-5", s.ModelFor(r)));
        Assert.True(s.AutoListen);
        Assert.False(s.SilenceAutoSubmit);
        Assert.Equal(6, s.SilenceSeconds);
    }

    [Fact]
    public void Keys_round_trip_but_are_not_stored_as_plain_text()
    {
        var store = new SettingsStore(Path_);
        var settings = store.Current.Clone();
        settings.AnthropicApiKey = "sk-ant-secret-123";
        settings.AzureSpeechKey = "azure-secret-456";
        settings.CoachModel = "claude-opus-5-5";
        settings.DemoMode = true;
        store.Save(settings);

        var onDisk = File.ReadAllText(Path_);
        Assert.DoesNotContain("sk-ant-secret-123", onDisk);
        Assert.DoesNotContain("azure-secret-456", onDisk);
        Assert.Contains("dpapi:", onDisk);

        var reloaded = new SettingsStore(Path_).Current;
        Assert.Equal("sk-ant-secret-123", reloaded.AnthropicApiKey);
        Assert.Equal("azure-secret-456", reloaded.AzureSpeechKey);
        Assert.Equal("claude-opus-5-5", reloaded.CoachModel);
        Assert.True(reloaded.DemoMode);
    }

    [Fact]
    public void Enums_are_written_as_names()
    {
        var store = new SettingsStore(Path_);
        var settings = store.Current.Clone();
        settings.Provider = LlmProvider.OpenAiCompatible;
        store.Save(settings);

        Assert.Contains("\"OpenAiCompatible\"", File.ReadAllText(Path_));
        Assert.Equal(LlmProvider.OpenAiCompatible, new SettingsStore(Path_).Current.Provider);
    }

    [Fact]
    public void The_kind_of_interview_is_remembered_between_runs_and_stored_as_a_readable_name()
    {
        var store = new SettingsStore(Path_);
        Assert.Equal(EmploymentType.FullTime, store.Current.EmploymentType);

        var settings = store.Current.Clone();
        settings.EmploymentType = EmploymentType.Contract;
        store.Save(settings);

        Assert.Contains("\"Contract\"", File.ReadAllText(Path_));
        Assert.Equal(EmploymentType.Contract, new SettingsStore(Path_).Current.EmploymentType);
    }

    [Fact]
    public void A_settings_file_from_before_the_option_existed_opens_as_full_time()
    {
        File.WriteAllText(Path_, "{ \"DemoMode\": true }");

        var settings = new SettingsStore(Path_).Current;

        Assert.True(settings.DemoMode);
        Assert.Equal(EmploymentType.FullTime, settings.EmploymentType);
    }

    [Fact]
    public void Save_raises_Changed()
    {
        var store = new SettingsStore(Path_);
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.Save(store.Current.Clone());

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults()
    {
        File.WriteAllText(Path_, "{ this is not json");

        Assert.Equal(LlmProvider.Anthropic, new SettingsStore(Path_).Current.Provider);
    }

    [Fact]
    public void Stored_key_wins_over_environment_variable()
    {
        var s = new AppSettings { AnthropicApiKey = "stored" };

        Assert.Equal("stored", s.EffectiveAnthropicKey);
    }
}
