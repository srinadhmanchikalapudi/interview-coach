using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using InterviewCoach.App.Services;
using InterviewCoach.App.ViewModels;
using InterviewCoach.App.Views;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Core.Updates;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.App.Tests;

/// <summary>Updating: when the check runs, what the user is told, and that nothing is installed without their say-so.</summary>
public class AutoUpdateTests
{
    private sealed class MemorySettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current { get; private set; } = s;
        public int Saves { get; private set; }
        public void Save(AppSettings settings) { Current = settings; Saves++; Changed?.Invoke(this, EventArgs.Empty); }
        public event EventHandler? Changed;
    }

    private sealed class FakeChecker : IUpdateChecker
    {
        public UpdateCheckResult Result { get; set; } = UpdateCheckResult.UpToDate;
        public int Calls { get; private set; }
        public Version? AskedAbout { get; private set; }

        public Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken ct = default)
        {
            Calls++;
            AskedAbout = current;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeInstaller : IUpdateInstaller
    {
        public List<string> Launched { get; } = [];
        public int Downloads { get; private set; }
        public Exception? DownloadThrows { get; set; }
        public Exception? LaunchThrows { get; set; }
        public double[] Progress { get; set; } = [0.25, 0.5, 1.0];

        public Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Downloads++;
            if (DownloadThrows is not null) throw DownloadThrows;
            foreach (var p in Progress) progress?.Report(p);
            return Task.FromResult(@"C:\temp\setup.exe");
        }

        public void Launch(string setupPath)
        {
            if (LaunchThrows is not null) throw LaunchThrows;
            Launched.Add(setupPath);
        }
    }

    private sealed class FakeInstallation(bool installed) : IInstallationInfo
    {
        public bool InstalledBySetup { get; set; } = installed;
    }

    private sealed class FakeOpener : IUrlOpener
    {
        public List<string> Opened { get; } = [];
        public void Open(string url) => Opened.Add(url);
    }

    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private static UpdateInfo Newer(string? installerUrl = "https://github.com/me/repo/releases/download/v1.1.0/InterviewCoach-Setup-1.1.0.exe")
        => new(new Version(1, 1, 0), "v1.1.0", "https://github.com/me/repo/releases/tag/v1.1.0", installerUrl, "abc", "Notes");

    private sealed class World
    {
        public FakeChecker Checker { get; } = new();
        public FakeInstaller Installer { get; } = new();
        public FakeInstallation Installation { get; }
        public FakeOpener Opener { get; } = new();
        public MemorySettings Settings { get; }
        public int Exits { get; private set; }
        public DateTime Clock { get; set; } = Now;
        public UpdateService Service { get; }

        public World(bool installed = true, AppSettings? settings = null)
        {
            Installation = new FakeInstallation(installed);
            Settings = new MemorySettings(settings ?? new AppSettings { AnthropicApiKey = "k" });
            Service = new UpdateService(Checker, Installer, Settings, Installation, Opener, new Version(1, 0, 0), () => Clock, () => Exits++);
        }
    }

    // ---- when the check runs

    [Fact]
    public async Task The_check_on_start_runs_when_allowed_and_asks_about_the_running_version()
    {
        var w = new World();

        await w.Service.CheckOnStartupAsync();

        Assert.Equal(1, w.Checker.Calls);
        Assert.Equal(new Version(1, 0, 0), w.Checker.AskedAbout);
        Assert.Equal(Now, w.Settings.Current.LastUpdateCheck);              // remembered, so it does not run again today
    }

    [Fact]
    public async Task The_check_on_start_does_nothing_when_Settings_switches_it_off()
    {
        var w = new World(settings: new AppSettings { CheckForUpdates = false });

        await w.Service.CheckOnStartupAsync();

        Assert.Equal(0, w.Checker.Calls);
        Assert.Equal(UpdatePhase.Idle, w.Service.Phase);
    }

    [Fact]
    public async Task The_check_on_start_runs_at_most_once_a_day()
    {
        var w = new World();
        await w.Service.CheckOnStartupAsync();

        w.Clock = Now.AddHours(23);
        await w.Service.CheckOnStartupAsync();
        Assert.Equal(1, w.Checker.Calls);

        w.Clock = Now.AddHours(24).AddMinutes(1);
        await w.Service.CheckOnStartupAsync();
        Assert.Equal(2, w.Checker.Calls);
    }

    [Fact]
    public async Task The_check_on_start_can_be_switched_off_for_development_with_an_environment_variable()
    {
        var w = new World();
        try
        {
            Environment.SetEnvironmentVariable("ICOACH_NO_UPDATE_CHECK", "1");

            await w.Service.CheckOnStartupAsync();

            Assert.Equal(0, w.Checker.Calls);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ICOACH_NO_UPDATE_CHECK", null);
        }
    }

    [Fact]
    public async Task A_check_the_user_asks_for_ignores_the_daily_limit_and_the_setting()
    {
        var w = new World(settings: new AppSettings { CheckForUpdates = false, LastUpdateCheck = Now });

        await w.Service.CheckAsync(manual: true);

        Assert.Equal(1, w.Checker.Calls);
    }

    // ---- what a check finds

    [Fact]
    public async Task A_newer_version_is_remembered_and_described()
    {
        var w = new World();
        w.Checker.Result = UpdateCheckResult.Available(Newer());
        var changes = 0;
        w.Service.Changed += () => changes++;

        await w.Service.CheckAsync(manual: false);

        Assert.Equal(UpdatePhase.Available, w.Service.Phase);
        Assert.Equal(new Version(1, 1, 0), w.Service.Available!.Version);
        Assert.Equal("Version 1.1.0 is available.", w.Service.StatusText);
        Assert.True(w.Service.CanInstallInPlace);
        Assert.True(changes >= 2);                                         // checking, then the result
    }

    [Fact]
    public async Task Being_up_to_date_is_said_in_words_with_the_running_version()
    {
        var w = new World();

        await w.Service.CheckAsync(manual: true);

        Assert.Equal(UpdatePhase.Idle, w.Service.Phase);
        Assert.Null(w.Service.Available);
        Assert.Equal("You have the latest version (1.0.0).", w.Service.StatusText);
    }

    [Fact]
    public async Task An_automatic_check_that_fails_stays_quiet_and_one_the_user_asked_for_says_why()
    {
        var w = new World();
        w.Checker.Result = UpdateCheckResult.Failed("Could not reach GitHub to look for updates: no network");

        await w.Service.CheckAsync(manual: false);
        Assert.Equal(UpdatePhase.Idle, w.Service.Phase);
        Assert.Null(w.Service.Error);
        Assert.Equal("", w.Service.StatusText);
        Assert.Null(w.Settings.Current.LastUpdateCheck);                   // a failed check is not remembered: the next start tries again

        await w.Service.CheckAsync(manual: true);
        Assert.Equal(UpdatePhase.Failed, w.Service.Phase);
        Assert.Contains("no network", w.Service.StatusText);
    }

    // ---- installing

    [Fact]
    public async Task Installing_downloads_the_setup_program_starts_it_and_closes_the_program()
    {
        var w = new World();
        w.Checker.Result = UpdateCheckResult.Available(Newer());
        await w.Service.CheckAsync(manual: false);
        var phases = new List<UpdatePhase>();
        w.Service.Changed += () => phases.Add(w.Service.Phase);

        await w.Service.InstallAsync();

        Assert.Equal(1, w.Installer.Downloads);
        Assert.Equal([@"C:\temp\setup.exe"], w.Installer.Launched);
        Assert.Equal(1, w.Exits);                                          // the setup program replaces the program, so it must not be running
        Assert.Contains(UpdatePhase.Downloading, phases);
        Assert.Empty(w.Opener.Opened);
    }

    [Fact]
    public async Task A_copy_that_was_not_installed_by_the_setup_program_is_pointed_to_the_download_page_instead()
    {
        var w = new World(installed: false);
        w.Checker.Result = UpdateCheckResult.Available(Newer());
        await w.Service.CheckAsync(manual: false);

        await w.Service.InstallAsync();

        Assert.False(w.Service.CanInstallInPlace);
        Assert.Equal(["https://github.com/me/repo/releases/tag/v1.1.0"], w.Opener.Opened);
        Assert.Equal(0, w.Installer.Downloads);
        Assert.Equal(0, w.Exits);
    }

    [Fact]
    public async Task A_release_with_no_setup_program_is_pointed_to_the_page_even_for_an_installed_copy()
    {
        var w = new World();
        w.Checker.Result = UpdateCheckResult.Available(Newer(installerUrl: null));
        await w.Service.CheckAsync(manual: false);

        await w.Service.InstallAsync();

        Assert.False(w.Service.CanInstallInPlace);
        Assert.Single(w.Opener.Opened);
        Assert.Equal(0, w.Installer.Downloads);
    }

    [Fact]
    public async Task A_failed_download_is_reported_and_the_program_keeps_running()
    {
        var w = new World();
        w.Checker.Result = UpdateCheckResult.Available(Newer());
        await w.Service.CheckAsync(manual: false);
        w.Installer.DownloadThrows = new UpdateException("The downloaded file does not match the checksum GitHub published for it, so it was not run.");

        await w.Service.InstallAsync();

        Assert.Equal(UpdatePhase.Failed, w.Service.Phase);
        Assert.Contains("does not match the checksum", w.Service.Error);
        Assert.Equal(0, w.Exits);                                          // nothing was run, so nothing is closed
        Assert.Empty(w.Installer.Launched);
        Assert.NotNull(w.Service.Available);                               // the update is still there to try again or fetch by hand
    }

    [Fact]
    public async Task A_setup_program_that_will_not_start_is_reported_and_the_program_keeps_running()
    {
        var w = new World();
        w.Checker.Result = UpdateCheckResult.Available(Newer());
        await w.Service.CheckAsync(manual: false);
        w.Installer.LaunchThrows = new UpdateException("The update could not be started: access denied");

        await w.Service.InstallAsync();

        Assert.Equal(UpdatePhase.Failed, w.Service.Phase);
        Assert.Equal(0, w.Exits);
    }

    [Fact]
    public async Task Installing_with_nothing_available_does_nothing()
    {
        var w = new World();

        await w.Service.InstallAsync();

        Assert.Equal(0, w.Installer.Downloads);
        Assert.Empty(w.Opener.Opened);
        Assert.Equal(0, w.Exits);
    }

    [Fact]
    public async Task The_release_page_can_be_opened_with_or_without_a_known_release()
    {
        var w = new World();
        w.Service.OpenReleasePage();
        Assert.Equal([AppInfo.ReleasesUrl], w.Opener.Opened);

        w.Checker.Result = UpdateCheckResult.Available(Newer());
        await w.Service.CheckAsync(manual: false);
        w.Service.OpenReleasePage();
        Assert.Equal("https://github.com/me/repo/releases/tag/v1.1.0", w.Opener.Opened[1]);
    }

    // ---- the bar above the page

    private static async Task<(MainViewModel Main, World World, ScriptedDialogs Dialogs)> NewMainAsync(bool installed = true)
    {
        var w = new World(installed);
        var llm = new FakeLlmService();
        var prompts = new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir"));
        var bank = new TechBank(new InMemoryTechBankRepository(), llm, prompts, () => 0.0);
        var repo = new InMemoryProfileRepository();
        await repo.SaveAsync(Samples.CompleteProfile());
        var dialogs = new ScriptedDialogs();
        var home = new HomeViewModel(repo, new StubExtractor(), dialogs, bank, w.Settings);
        await home.InitializeAsync();
        var learn = new LearnViewModel(llm, prompts, w.Settings, bank, () => 0.0);
        var settings = new SettingsViewModel(w.Settings, llm, bank, dialogs, null, null, w.Service);
        var main = new MainViewModel(home, settings, learn, w.Settings, null, null, null, null, null, w.Service, dialogs);
        return (main, w, dialogs);
    }

    [Fact]
    public async Task A_newer_version_shows_a_bar_with_Update_now()
    {
        var (main, w, _) = await NewMainAsync();
        Assert.Null(main.Notice);
        w.Checker.Result = UpdateCheckResult.Available(Newer());

        await w.Service.CheckAsync(manual: false);

        Assert.True(main.HasNotice);
        Assert.Equal(NoticeLevel.Info, main.Notice!.Level);
        Assert.Equal("Version 1.1.0 is available (you have 1.0.0).", main.Notice.Text);
        Assert.Equal("Update now", main.Notice.ActionLabel);
    }

    [Fact]
    public async Task The_button_of_a_copy_that_was_not_installed_says_Download()
    {
        var (main, w, _) = await NewMainAsync(installed: false);
        w.Checker.Result = UpdateCheckResult.Available(Newer());

        await w.Service.CheckAsync(manual: false);

        Assert.Equal("Download", main.Notice!.ActionLabel);
        main.RunNoticeActionCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal(["https://github.com/me/repo/releases/tag/v1.1.0"], w.Opener.Opened);
    }

    [Fact]
    public async Task Update_now_asks_first_and_declining_changes_nothing()
    {
        var (main, w, dialogs) = await NewMainAsync();
        w.Checker.Result = UpdateCheckResult.Available(Newer());
        await w.Service.CheckAsync(manual: false);
        dialogs.ConfirmAnswer = false;

        main.RunNoticeActionCommand.Execute(null);
        await Task.Delay(50);

        Assert.Equal(["Update Interview Coach"], dialogs.Confirmations);
        Assert.Equal(0, w.Installer.Downloads);
        Assert.Equal(0, w.Exits);
        Assert.Equal(UpdatePhase.Available, w.Service.Phase);
    }

    [Fact]
    public async Task Confirming_installs_and_closes_the_program()
    {
        var (main, w, dialogs) = await NewMainAsync();
        w.Checker.Result = UpdateCheckResult.Available(Newer());
        await w.Service.CheckAsync(manual: false);

        main.RunNoticeActionCommand.Execute(null);
        await Task.Delay(100);

        Assert.Equal(["Update Interview Coach"], dialogs.Confirmations);
        Assert.Equal(1, w.Installer.Downloads);
        Assert.Equal([@"C:\temp\setup.exe"], w.Installer.Launched);
        Assert.Equal(1, w.Exits);
    }

    [Fact]
    public async Task The_confirmation_says_what_will_happen_to_work_in_progress()
    {
        var (main, w, dialogs) = await NewMainAsync();
        w.Checker.Result = UpdateCheckResult.Available(Newer());
        await w.Service.CheckAsync(manual: false);
        string? message = null;
        var asking = new CapturingDialogs(dialogs, m => message = m);
        var main2 = new MainViewModel(
            new HomeViewModel(new InMemoryProfileRepository(), new StubExtractor(), asking), new SettingsViewModel(w.Settings, new FakeLlmService()),
            new LearnViewModel(new FakeLlmService(), new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")), w.Settings), w.Settings,
            null, null, null, null, null, w.Service, asking);

        main2.RunNoticeActionCommand.Execute(null);
        await Task.Delay(100);

        Assert.Contains("Interview Coach will close, update and start again", message);
        Assert.Contains("mock interview in progress is kept in History", message);
        Assert.Contains("1.1.0", message);
        _ = main;
    }

    private sealed class CapturingDialogs(ScriptedDialogs inner, Action<string> captured) : IDialogService
    {
        public string? PickFile(string title, string filter) => inner.PickFile(title, filter);
        public string? PickSaveFile(string title, string filter, string defaultName) => inner.PickSaveFile(title, filter, defaultName);
        public bool Confirm(string title, string message) { captured(message); return inner.Confirm(title, message); }
    }

    [Fact]
    public async Task While_downloading_the_bar_shows_the_progress_and_has_no_button()
    {
        var (main, w, _) = await NewMainAsync();
        w.Checker.Result = UpdateCheckResult.Available(Newer());
        await w.Service.CheckAsync(manual: false);
        var seen = new List<AppNotice>();
        main.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Notice) && main.Notice is { } n) seen.Add(n); };
        w.Installer.Progress = [0.5];

        main.RunNoticeActionCommand.Execute(null);
        await Task.Delay(150);

        var downloading = Assert.Single(seen, n => n.Text == "Downloading version 1.1.0… 50%");
        Assert.Null(downloading.ActionLabel);                               // nothing to click while it downloads
        Assert.Null(downloading.Action);
    }

    [Fact]
    public async Task A_failed_update_shows_an_error_bar_with_a_button_to_the_download_page()
    {
        var (main, w, _) = await NewMainAsync();
        w.Checker.Result = UpdateCheckResult.Available(Newer());
        await w.Service.CheckAsync(manual: false);
        w.Installer.DownloadThrows = new UpdateException("The download failed: connection reset");

        main.RunNoticeActionCommand.Execute(null);
        await Task.Delay(100);

        Assert.Equal(NoticeLevel.Error, main.Notice!.Level);
        Assert.Equal("The update did not install: The download failed: connection reset", main.Notice.Text);
        Assert.Equal("Open download page", main.Notice.ActionLabel);
        main.RunNoticeActionCommand.Execute(null);
        Assert.Equal(["https://github.com/me/repo/releases/tag/v1.1.0"], w.Opener.Opened);
    }

    [Fact]
    public async Task Dismissing_the_bar_hides_that_version_until_the_program_is_started_again_but_a_newer_one_shows()
    {
        var (main, w, _) = await NewMainAsync();
        w.Checker.Result = UpdateCheckResult.Available(Newer());
        await w.Service.CheckAsync(manual: false);

        main.DismissNoticeCommand.Execute(null);
        Assert.Null(main.Notice);
        await w.Service.CheckAsync(manual: true);                           // asking again does not bring it back
        Assert.Null(main.Notice);

        w.Checker.Result = UpdateCheckResult.Available(new UpdateInfo(new Version(1, 2, 0), "v1.2.0", "https://github.com/me/repo/releases/tag/v1.2.0", null, null, null));
        await w.Service.CheckAsync(manual: true);
        Assert.Contains("1.2.0", main.Notice!.Text);
    }

    [Fact]
    public async Task The_update_bar_is_not_shown_on_Settings_which_has_its_own_controls()
    {
        var (main, w, _) = await NewMainAsync();
        w.Checker.Result = UpdateCheckResult.Available(Newer());
        await w.Service.CheckAsync(manual: false);

        main.ShowSettingsCommand.Execute(null);
        Assert.Null(main.Notice);

        main.ShowHomeCommand.Execute(null);
        Assert.NotNull(main.Notice);
    }

    [Fact]
    public async Task A_problem_that_was_reported_comes_before_an_update_bar()
    {
        var (main, w, _) = await NewMainAsync();
        w.Checker.Result = UpdateCheckResult.Available(Newer());
        await w.Service.CheckAsync(manual: false);

        main.ReportError("Disk full");

        Assert.Equal("Disk full", main.Notice!.Text);
        main.DismissNoticeCommand.Execute(null);
        Assert.Contains("Version 1.1.0 is available", main.Notice!.Text);
    }

    // ---- Settings

    [Fact]
    public async Task Settings_shows_the_version_and_checks_on_request_with_the_answer_in_words()
    {
        var (_, w, _) = await NewMainAsync();
        var settings = new SettingsViewModel(w.Settings, new FakeLlmService(), updates: w.Service);
        Assert.True(settings.HasUpdates);
        Assert.Equal("Interview Coach 1.0.0", settings.VersionText);
        Assert.False(settings.HasUpdateStatus);

        await settings.CheckForUpdatesNowCommand.ExecuteAsync(null);
        Assert.Equal("You have the latest version (1.0.0).", settings.UpdateStatus);
        Assert.True(settings.HasUpdateStatus);

        w.Checker.Result = UpdateCheckResult.Available(Newer());
        await settings.CheckForUpdatesNowCommand.ExecuteAsync(null);
        Assert.Equal("Version 1.1.0 is available.", settings.UpdateStatus);

        w.Checker.Result = UpdateCheckResult.Failed("GitHub did not answer in time. Check your internet connection.");
        await settings.CheckForUpdatesNowCommand.ExecuteAsync(null);
        Assert.Equal("GitHub did not answer in time. Check your internet connection.", settings.UpdateStatus);
    }

    [Fact]
    public async Task The_choice_to_look_for_updates_is_saved_with_the_other_settings()
    {
        var (_, w, _) = await NewMainAsync();
        var settings = new SettingsViewModel(w.Settings, new FakeLlmService(), updates: w.Service);
        Assert.True(settings.CheckForUpdates);                              // on by default

        settings.CheckForUpdates = false;
        settings.SaveCommand.Execute(null);

        Assert.False(w.Settings.Current.CheckForUpdates);
    }

    [Fact]
    public void Settings_without_the_update_service_has_no_update_controls()
    {
        var settings = new SettingsViewModel(new MemorySettings(new AppSettings()), new FakeLlmService());

        Assert.False(settings.HasUpdates);
        Assert.Equal("", settings.VersionText);
        Assert.False(settings.CanCheckNow);
    }

    [Fact]
    public async Task Open_the_releases_page_opens_the_page()
    {
        var (_, w, _) = await NewMainAsync();
        var settings = new SettingsViewModel(w.Settings, new FakeLlmService(), updates: w.Service);

        settings.OpenReleasePageCommand.Execute(null);

        Assert.Equal([AppInfo.ReleasesUrl], w.Opener.Opened);
    }

    // ---- the screen

    private static void Layout(FrameworkElement view)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            view.Measure(new Size(1000, 3000));
            view.Arrange(new Rect(0, 0, 1000, 3000));
            view.UpdateLayout();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static bool Shown(UIElement e)
    {
        for (DependencyObject? node = e; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private static List<string> Texts(DependencyObject root)
        => Descendants<TextBlock>(root).Where(Shown).Select(t => string.Concat(t.Inlines.OfType<Run>().Select(r => r.Text)) is { Length: > 0 } s ? s : t.Text).ToList();

    [Fact]
    public async Task The_Settings_screen_has_an_About_and_updates_card_with_the_version_and_buttons_and_binds_cleanly()
    {
        var (_, w, _) = await NewMainAsync();
        var settings = new SettingsViewModel(w.Settings, new FakeLlmService(), updates: w.Service);
        await settings.CheckForUpdatesNowCommand.ExecuteAsync(null);
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new SettingsView { DataContext = settings };
            Layout(view);

            var texts = Texts(view);
            Assert.Contains("About and updates", texts);
            Assert.Contains("Interview Coach 1.0.0", texts);
            Assert.Contains("You have the latest version (1.0.0).", texts);
            var buttons = Descendants<Button>(view).Where(Shown).Select(b => b.Content as string).ToList();
            Assert.Contains("Check for updates now", buttons);
            Assert.Contains("Open the releases page", buttons);
            Assert.Contains(Descendants<CheckBox>(view), c => Shown(c) && c.Content is string t && t.StartsWith("Look for a new version"));
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }
}
