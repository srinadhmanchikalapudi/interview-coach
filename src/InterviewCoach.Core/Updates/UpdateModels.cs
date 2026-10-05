using System.Text.RegularExpressions;

namespace InterviewCoach.Core.Updates;

/// <summary>A newer version that is published on GitHub.</summary>
/// <param name="Version">The version, from the release tag.</param>
/// <param name="Tag">The tag as written, for example "v1.1.0".</param>
/// <param name="PageUrl">The release's web page, where the files can be downloaded by hand.</param>
/// <param name="InstallerUrl">The setup program attached to the release, or null if there is none.</param>
/// <param name="InstallerSha256">The SHA-256 GitHub reports for the setup program (lower case hex), or null if it reports none.</param>
public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? InstallerUrl, string? InstallerSha256, string? Notes);

public enum UpdateStatus { UpToDate, Available, Failed }

/// <param name="Update">Set when <paramref name="Status"/> is <see cref="UpdateStatus.Available"/>.</param>
/// <param name="Error">A sentence for the user when <paramref name="Status"/> is <see cref="UpdateStatus.Failed"/>.</param>
public sealed record UpdateCheckResult(UpdateStatus Status, UpdateInfo? Update = null, string? Error = null)
{
    public static UpdateCheckResult UpToDate { get; } = new(UpdateStatus.UpToDate);
    public static UpdateCheckResult Failed(string error) => new(UpdateStatus.Failed, null, error);
    public static UpdateCheckResult Available(UpdateInfo update) => new(UpdateStatus.Available, update);
}

/// <summary>Asks where the newest release is published and whether it is newer than the running version.</summary>
public interface IUpdateChecker
{
    /// <summary>Never throws for a network or server problem: that is a <see cref="UpdateStatus.Failed"/> result with a message.</summary>
    Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken ct = default);
}

/// <summary>Downloads a release's setup program, checks it and starts it.</summary>
public interface IUpdateInstaller
{
    /// <summary>Downloads the setup program to a temporary file and verifies it. Throws <see cref="UpdateException"/> with a message for the user if it cannot.</summary>
    Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Starts the downloaded setup program, which replaces the installed version and starts the program again. The caller must then exit.</summary>
    void Launch(string setupPath);
}

/// <summary>Whether the running program was put in place by the installer (and so can be updated by it) or is a copy that was unzipped or built.</summary>
public interface IInstallationInfo
{
    bool InstalledBySetup { get; }
}

/// <summary>An update could not be downloaded or started. The message is written to be shown to the user.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

public static partial class UpdateVersions
{
    [GeneratedRegex(@"^\s*v?(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:[-+].*)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    /// <summary>Reads "v1.2.3", "1.2" or "1.2.3-beta+abc" as a version (the part after a dash or plus is dropped). Null if it is not one.</summary>
    public static Version? Parse(string? text)
    {
        if (Pattern().Match(text ?? "") is not { Success: true } m) return null;
        int major = int.Parse(m.Groups[1].Value), minor = int.Parse(m.Groups[2].Value), build = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        return m.Groups[4].Success ? new Version(major, minor, build, int.Parse(m.Groups[4].Value)) : new Version(major, minor, build);
    }

    /// <summary>True when <paramref name="candidate"/> is newer than <paramref name="current"/>, comparing the first three parts.</summary>
    public static bool IsNewer(Version candidate, Version current)
        => new Version(candidate.Major, candidate.Minor, Math.Max(0, candidate.Build)) > new Version(current.Major, current.Minor, Math.Max(0, current.Build));

    /// <summary>"1.2.3" (three parts, as users see it).</summary>
    public static string Display(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
}
