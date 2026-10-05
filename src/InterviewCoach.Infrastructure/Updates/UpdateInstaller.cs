using System.Diagnostics;
using System.Security.Cryptography;
using InterviewCoach.Core.Updates;
using Microsoft.Win32;

namespace InterviewCoach.Infrastructure.Updates;

/// <summary>Starts a program or opens a web page. A seam so tests do not start real processes.</summary>
public interface IProcessLauncher
{
    void Start(string fileName, string arguments);
}

public sealed class ShellProcessLauncher : IProcessLauncher
{
    public void Start(string fileName, string arguments)
        => Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
}

/// <summary>
/// Downloads the setup program of a release over HTTPS and runs it. It only ever runs a file that came from GitHub, was downloaded in
/// full, and (when GitHub reports its checksum) matches that checksum; anything else is deleted and reported.
/// </summary>
public sealed class UpdateInstaller(HttpClient http, IProcessLauncher launcher, Func<string>? tempDirectory = null) : IUpdateInstaller
{
    /// <summary>What the setup program is started with: a visible progress window, no restart of Windows, and the program started again at the end.</summary>
    public const string SetupArguments = "/SILENT /NORESTART /CLOSEAPPLICATIONS /RESTARTAPP=1";

    private readonly Func<string> _temp = tempDirectory ?? Path.GetTempPath;

    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (update.InstallerUrl is not { } url) throw new UpdateException("This release has no setup program attached.");
        if (!IsTrustedUrl(url)) throw new UpdateException("The update's download address is not on GitHub, so it was not used.");

        var folder = Path.Combine(_temp(), "InterviewCoach-update");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"InterviewCoach-Setup-{UpdateVersions.Display(update.Version)}.exe");
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new UpdateException($"The download failed: GitHub answered with an error ({(int)response.StatusCode}).");

            var total = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total is > 0) progress?.Report(Math.Min(1.0, (double)read / total.Value));
                }
                if (total is { } expected && read != expected) throw new UpdateException("The download was cut short. Try again.");
            }

            if (update.InstallerSha256 is { Length: > 0 } expectedHash && !await HashMatchesAsync(path, expectedHash, ct))
                throw new UpdateException("The downloaded file does not match the checksum GitHub published for it, so it was not run.");

            progress?.Report(1.0);
            return path;
        }
        catch (UpdateException)
        {
            TryDelete(path);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryDelete(path);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryDelete(path);
            throw new UpdateException("The download failed: " + ex.Message, ex);
        }
    }

    public void Launch(string setupPath)
    {
        try
        {
            launcher.Start(setupPath, SetupArguments);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new UpdateException("The update could not be started: " + ex.Message, ex);
        }
    }

    /// <summary>The address must be HTTPS on github.com (release files) or githubusercontent.com (where GitHub serves them from).</summary>
    public static bool IsTrustedUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
           && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return actual.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* a leftover temporary file is not worth an error */ }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Whether this copy of the program was installed by the setup program: the installer leaves an entry in Windows' list of apps (under its
/// fixed id) that points at the folder it installed to. A copy that was unzipped, or run from a build folder, is not updated in place.
/// </summary>
public sealed class InstallationInfo(string? baseDirectory = null, string? uninstallKey = null) : IInstallationInfo
{
    // The AppId in installer\InterviewCoach.iss, with Inno Setup's "_is1" suffix. It must stay the same.
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F2B8E54-3D1A-4C7B-9A0E-5B7C1D2E4F83}_is1";

    public bool InstalledBySetup
    {
        get
        {
            var here = Normalise(baseDirectory ?? AppContext.BaseDirectory);
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var key = hive.OpenSubKey(uninstallKey ?? UninstallKey);
                if (key?.GetValue("InstallLocation") is string location && Normalise(location) == here) return true;
            }
            return false;
        }
    }

    private static string Normalise(string path) => Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();
}
