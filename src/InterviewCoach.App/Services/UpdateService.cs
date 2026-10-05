using System.IO;
using System.Diagnostics;
using System.Reflection;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Core.Updates;
using InterviewCoach.Infrastructure.Updates;

namespace InterviewCoach.App.Services;

/// <summary>Where the program lives on GitHub and which version is running.</summary>
public static class AppInfo
{
    public const string Owner = "srinadhmanchikalapudi";
    public const string Repo = "interview-coach";

    public static string ReleasesUrl => $"https://github.com/{Owner}/{Repo}/releases/latest";

    /// <summary>The running version, from the build (publish.ps1 and the release workflow set it from the tag). 0.0.0 if it cannot be read.</summary>
    public static Version CurrentVersion
    {
        get
        {
            var informational = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return UpdateVersions.Parse(informational) ?? typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 0, 0);
        }
    }
}

/// <summary>Opens a web page in the user's browser. A seam so tests do not open real pages.</summary>
public interface IUrlOpener
{
    void Open(string url);
}

public sealed class ShellUrlOpener(IProcessLauncher launcher) : IUrlOpener
{
    public void Open(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) launcher.Start(url, "");
    }
}

public enum UpdatePhase { Idle, Checking, Available, Downloading, Failed }

/// <summary>
/// Keeps the program up to date, with the user in charge at every step: it looks for a newer GitHub release (on start, at most once a day, and
/// only if Settings allows it; or when asked), says so, and installs it only when the user clicks. A copy that the installer put in place is
/// updated in place (the setup program is downloaded, checked, started, and the program exits so it can be replaced and starts again). A copy that
/// was unzipped or built is pointed to the download page instead.
/// </summary>
public sealed class UpdateService(
    IUpdateChecker checker, IUpdateInstaller installer, ISettingsStore settings, IInstallationInfo installation, IUrlOpener opener,
    Version current, Func<DateTime>? now = null, Action? exit = null)
{
    /// <summary>The checks on start are at most this far apart.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);

    public UpdatePhase Phase { get; private set; } = UpdatePhase.Idle;
    public UpdateInfo? Available { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }

    /// <summary>The result of the last check in words, for Settings.</summary>
    public string StatusText { get; private set; } = "";

    public Version Current => current;
    public string CurrentText => UpdateVersions.Display(current);

    /// <summary>True when the update can be installed from here (the setup program is attached and this copy was installed by it).</summary>
    public bool CanInstallInPlace => Available?.InstallerUrl is not null && installation.InstalledBySetup;

    public event Action? Changed;

    /// <summary>The automatic check on start: only if allowed, not from a debugger or when switched off for development, and not more than once a day.</summary>
    public async Task CheckOnStartupAsync(CancellationToken ct = default)
    {
        var s = settings.Current;
        if (!s.CheckForUpdates || Debugger.IsAttached) return;
        if (Environment.GetEnvironmentVariable("ICOACH_NO_UPDATE_CHECK") == "1") return;
        if (s.LastUpdateCheck is { } last && _now() - last < CheckInterval) return;
        await CheckAsync(manual: false, ct);
    }

    /// <summary>Looks for a newer release. A check the user asked for says what went wrong; an automatic one stays quiet when it fails.</summary>
    public async Task CheckAsync(bool manual, CancellationToken ct = default)
    {
        if (Phase is UpdatePhase.Checking or UpdatePhase.Downloading) return;
        Set(UpdatePhase.Checking, status: "Looking for updates…");
        var result = await checker.CheckAsync(current, ct);

        switch (result.Status)
        {
            case UpdateStatus.Available:
                Available = result.Update;
                Remember();
                Set(UpdatePhase.Available, status: $"Version {UpdateVersions.Display(result.Update!.Version)} is available.");
                break;
            case UpdateStatus.UpToDate:
                Available = null;
                Remember();
                Set(UpdatePhase.Idle, status: $"You have the latest version ({CurrentText}).");
                break;
            default:
                Error = manual ? result.Error : null;
                Set(manual ? UpdatePhase.Failed : UpdatePhase.Idle, status: manual ? (result.Error ?? "The check failed.") : "");
                break;
        }
    }

    /// <summary>Installs the available update (after the caller asked the user), or opens the download page when it cannot be installed from here.</summary>
    public async Task InstallAsync(CancellationToken ct = default)
    {
        if (Available is not { } update || Phase == UpdatePhase.Downloading) return;
        if (!CanInstallInPlace)
        {
            opener.Open(update.PageUrl);
            return;
        }

        Progress = 0;
        Error = null;
        Set(UpdatePhase.Downloading, status: "Downloading the update…");
        try
        {
            var path = await installer.DownloadAsync(update, new Progress<double>(p => { Progress = p; Changed?.Invoke(); }), ct);
            installer.Launch(path);
            exit?.Invoke(); // the setup program replaces the program, so it must not be running
        }
        catch (UpdateException ex)
        {
            Error = ex.Message;
            Set(UpdatePhase.Failed, status: ex.Message);
        }
        catch (OperationCanceledException)
        {
            Set(UpdatePhase.Available, status: $"Version {UpdateVersions.Display(update.Version)} is available.");
        }
    }

    /// <summary>Opens the release page in the browser.</summary>
    public void OpenReleasePage() => opener.Open(Available?.PageUrl ?? AppInfo.ReleasesUrl);

    private void Remember()
    {
        try
        {
            var updated = settings.Current.Clone();
            updated.LastUpdateCheck = _now();
            settings.Save(updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not remembering the time of a check is not worth an error: the next start simply checks again
        }
    }

    private void Set(UpdatePhase phase, string status)
    {
        Phase = phase;
        StatusText = status;
        Changed?.Invoke();
    }
}
