using System.Net;
using System.Security.Cryptography;
using System.Text;
using InterviewCoach.Core.Updates;
using InterviewCoach.Infrastructure.Updates;
using Microsoft.Win32;

namespace InterviewCoach.Infrastructure.Tests;

/// <summary>Looking for a newer release on GitHub, downloading its setup program safely, and telling an installed copy from an unzipped one.</summary>
public class UpdateTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Release(string tag, string? sha = "abc123", bool withInstaller = true, string installerUrl = "https://github.com/me/repo/releases/download/v9.9.9/InterviewCoach-Setup-9.9.9.exe")
    {
        var assets = new List<string>
        {
            "{ \"name\": \"InterviewCoach-9.9.9-win-x64.zip\", \"browser_download_url\": \"https://github.com/me/repo/releases/download/v9.9.9/zip.zip\", \"digest\": \"sha256:zzzz\" }",
        };
        if (withInstaller)
            assets.Add("{ \"name\": \"InterviewCoach-Setup-9.9.9.exe\", \"browser_download_url\": \"" + installerUrl + "\""
                       + (sha is null ? "" : ", \"digest\": \"sha256:" + sha + "\"") + " }");
        return "{ \"tag_name\": \"" + tag + "\", \"html_url\": \"https://github.com/me/repo/releases/tag/" + tag + "\", \"body\": \"What changed\", \"assets\": [" + string.Join(", ", assets) + "] }";
    }

    private static GitHubUpdateChecker Checker(Func<HttpRequestMessage, HttpResponseMessage> respond, out StubHandler handler)
    {
        handler = new StubHandler(respond);
        return new GitHubUpdateChecker(new HttpClient(handler), "me", "repo");
    }

    // ---- versions

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2", 1, 2, 0)]
    [InlineData("1.2.3-beta.1", 1, 2, 3)]
    [InlineData("1.2.3+abc123", 1, 2, 3)]
    [InlineData("  V10.20.30  ", 10, 20, 30)]
    public void A_tag_is_read_as_a_version(string tag, int major, int minor, int build)
        => Assert.Equal(new Version(major, minor, build), UpdateVersions.Parse(tag));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("release-3")]
    [InlineData("1")]
    public void Something_that_is_not_a_version_is_not_one(string? tag) => Assert.Null(UpdateVersions.Parse(tag));

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("1.0", "1.0.0", false)]
    [InlineData("1.10.0", "1.9.0", true)]                                // numbers, not text: 10 is more than 9
    public void Only_a_higher_version_is_newer(string candidate, string current, bool newer)
        => Assert.Equal(newer, UpdateVersions.IsNewer(UpdateVersions.Parse(candidate)!, UpdateVersions.Parse(current)!));

    [Fact]
    public void A_version_is_shown_with_three_parts() => Assert.Equal("1.2.0", UpdateVersions.Display(new Version(1, 2)));

    // ---- the check

    [Fact]
    public async Task A_newer_release_is_reported_with_its_installer_checksum_and_page()
    {
        var checker = Checker(_ => Json(Release("v9.9.9", sha: "ABC123")), out var handler);

        var result = await checker.CheckAsync(new Version(1, 0, 0));

        Assert.Equal(UpdateStatus.Available, result.Status);
        var update = result.Update!;
        Assert.Equal(new Version(9, 9, 9), update.Version);
        Assert.Equal("v9.9.9", update.Tag);
        Assert.Equal("https://github.com/me/repo/releases/download/v9.9.9/InterviewCoach-Setup-9.9.9.exe", update.InstallerUrl);
        Assert.Equal("abc123", update.InstallerSha256);                      // lower case, and the zip's checksum is not mixed up with it
        Assert.Equal("https://github.com/me/repo/releases/tag/v9.9.9", update.PageUrl);
        Assert.Equal("What changed", update.Notes);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/me/repo/releases/latest", request.RequestUri!.ToString());
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
    }

    [Fact]
    public async Task The_same_or_an_older_release_is_up_to_date()
    {
        var checker = Checker(_ => Json(Release("v1.0.0")), out _);

        Assert.Equal(UpdateStatus.UpToDate, (await checker.CheckAsync(new Version(1, 0, 0))).Status);
        Assert.Equal(UpdateStatus.UpToDate, (await checker.CheckAsync(new Version(2, 0, 0))).Status);
    }

    [Fact]
    public async Task A_release_without_a_setup_program_is_still_reported_with_its_page()
    {
        var checker = Checker(_ => Json(Release("v9.9.9", withInstaller: false)), out _);

        var update = (await checker.CheckAsync(new Version(1, 0, 0))).Update!;

        Assert.Null(update.InstallerUrl);
        Assert.Null(update.InstallerSha256);
        Assert.NotEmpty(update.PageUrl);
    }

    [Fact]
    public async Task A_setup_program_without_a_published_checksum_has_none()
    {
        var checker = Checker(_ => Json(Release("v9.9.9", sha: null)), out _);

        var update = (await checker.CheckAsync(new Version(1, 0, 0))).Update!;

        Assert.NotNull(update.InstallerUrl);
        Assert.Null(update.InstallerSha256);
    }

    [Fact]
    public async Task No_release_yet_is_up_to_date_not_an_error()
        => Assert.Equal(UpdateStatus.UpToDate, (await Checker(_ => Json("{}", HttpStatusCode.NotFound), out _).CheckAsync(new Version(1, 0, 0))).Status);

    [Fact]
    public async Task A_tag_that_is_not_a_version_is_not_an_update()
        => Assert.Equal(UpdateStatus.UpToDate, (await Checker(_ => Json(Release("nightly")), out _).CheckAsync(new Version(1, 0, 0))).Status);

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "limiting requests")]
    [InlineData(HttpStatusCode.TooManyRequests, "limiting requests")]
    [InlineData(HttpStatusCode.InternalServerError, "(500)")]
    public async Task A_server_problem_is_a_failed_result_with_words_not_an_exception(HttpStatusCode status, string expected)
    {
        var result = await Checker(_ => Json("{}", status), out _).CheckAsync(new Version(1, 0, 0));

        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Contains(expected, result.Error);
    }

    [Fact]
    public async Task No_network_and_unreadable_answers_are_failed_results_too()
    {
        var offline = await Checker(_ => throw new HttpRequestException("no such host"), out _).CheckAsync(new Version(1, 0, 0));
        var garbage = await Checker(_ => Json("<html>"), out _).CheckAsync(new Version(1, 0, 0));

        Assert.Equal(UpdateStatus.Failed, offline.Status);
        Assert.Contains("no such host", offline.Error);
        Assert.Equal(UpdateStatus.Failed, garbage.Status);
        Assert.Contains("could not be read", garbage.Error);
    }

    [Fact]
    public async Task A_timeout_is_a_failed_result_but_a_cancellation_by_the_caller_is_not_swallowed()
    {
        var timeout = await Checker(_ => throw new TaskCanceledException("timed out"), out _).CheckAsync(new Version(1, 0, 0));
        Assert.Equal(UpdateStatus.Failed, timeout.Status);
        Assert.Contains("did not answer in time", timeout.Error);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Checker(_ => Json(Release("v9.9.9")), out _).CheckAsync(new Version(1, 0, 0), cts.Token));
    }

    // ---- the download

    private sealed class FakeLauncher : IProcessLauncher
    {
        public List<(string File, string Args)> Started { get; } = [];
        public Exception? Throw { get; set; }

        public void Start(string fileName, string arguments)
        {
            if (Throw is not null) throw Throw;
            Started.Add((fileName, arguments));
        }
    }

    private static readonly byte[] SetupBytes = Encoding.ASCII.GetBytes("MZ pretend this is a setup program " + new string('x', 200_000));

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static UpdateInfo Update(string? sha, string url = "https://github.com/me/repo/releases/download/v9.9.9/InterviewCoach-Setup-9.9.9.exe")
        => new(new Version(9, 9, 9), "v9.9.9", "https://github.com/me/repo/releases/tag/v9.9.9", url, sha, null);

    private static (UpdateInstaller Installer, FakeLauncher Launcher, string Temp) NewInstaller(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var temp = Directory.CreateTempSubdirectory("ic-update-test").FullName;
        var launcher = new FakeLauncher();
        return (new UpdateInstaller(new HttpClient(new StubHandler(respond)), launcher, () => temp), launcher, temp);
    }

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    [Fact]
    public async Task The_setup_program_is_downloaded_checked_and_its_progress_reported()
    {
        var (installer, _, temp) = NewInstaller(_ => Bytes(SetupBytes));
        var reported = new List<double>();

        var path = await installer.DownloadAsync(Update(Sha(SetupBytes)), new Progress<double>(reported.Add));
        await Task.Delay(50);                                                 // Progress<T> posts its reports

        Assert.Equal(Path.Combine(temp, "InterviewCoach-update", "InterviewCoach-Setup-9.9.9.exe"), path);
        Assert.Equal(SetupBytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(1.0, reported.Max());
        Assert.True(reported.Count >= 2);
        Directory.Delete(temp, recursive: true);
    }

    [Fact]
    public async Task A_download_with_no_published_checksum_is_still_accepted_over_https()
    {
        var (installer, _, temp) = NewInstaller(_ => Bytes(SetupBytes));

        var path = await installer.DownloadAsync(Update(null));

        Assert.True(File.Exists(path));
        Directory.Delete(temp, recursive: true);
    }

    [Fact]
    public async Task A_file_that_does_not_match_the_published_checksum_is_deleted_and_never_run()
    {
        var (installer, launcher, temp) = NewInstaller(_ => Bytes(SetupBytes));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => installer.DownloadAsync(Update(new string('0', 64))));

        Assert.Contains("does not match the checksum", ex.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(temp, "InterviewCoach-update")));
        Assert.Empty(launcher.Started);
        Directory.Delete(temp, recursive: true);
    }

    [Fact]
    public async Task The_checksum_comparison_ignores_case()
    {
        var (installer, _, temp) = NewInstaller(_ => Bytes(SetupBytes));

        var path = await installer.DownloadAsync(Update(Sha(SetupBytes).ToUpperInvariant()));

        Assert.True(File.Exists(path));
        Directory.Delete(temp, recursive: true);
    }

    [Theory]
    [InlineData("http://github.com/me/repo/releases/download/v9/setup.exe")]       // not HTTPS
    [InlineData("https://evil.example.com/InterviewCoach-Setup-9.9.9.exe")]        // not GitHub
    [InlineData("https://github.com.evil.example.com/setup.exe")]                  // a look-alike
    [InlineData("https://notgithub.com/setup.exe")]
    [InlineData("file:///C:/setup.exe")]
    [InlineData("not a url")]
    public async Task Only_https_addresses_on_GitHub_are_downloaded(string url)
    {
        var (installer, _, temp) = NewInstaller(_ => Bytes(SetupBytes));

        await Assert.ThrowsAsync<UpdateException>(() => installer.DownloadAsync(Update(null, url)));

        Directory.Delete(temp, recursive: true);
    }

    [Theory]
    [InlineData("https://github.com/me/repo/releases/download/v9/setup.exe", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/x", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("https://api.github.com/x", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://github.com.evil.com/x", false)]
    [InlineData("https://evilgithub.com/x", false)]
    public void The_trusted_hosts_are_github_and_the_servers_it_serves_files_from(string url, bool trusted)
        => Assert.Equal(trusted, UpdateInstaller.IsTrustedUrl(url));

    [Fact]
    public async Task A_release_without_a_setup_program_cannot_be_downloaded()
    {
        var (installer, _, temp) = NewInstaller(_ => Bytes(SetupBytes));
        var update = new UpdateInfo(new Version(9, 9, 9), "v9.9.9", "https://github.com/me/repo", null, null, null);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => installer.DownloadAsync(update));

        Assert.Contains("no setup program", ex.Message);
        Directory.Delete(temp, recursive: true);
    }

    [Fact]
    public async Task A_server_error_or_a_dropped_connection_is_reported_and_leaves_no_file()
    {
        var (installer, _, temp) = NewInstaller(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<UpdateException>(() => installer.DownloadAsync(Update(null)));
        Assert.Contains("(404)", ex.Message);

        var (broken, _, temp2) = NewInstaller(_ => throw new HttpRequestException("connection reset"));
        var ex2 = await Assert.ThrowsAsync<UpdateException>(() => broken.DownloadAsync(Update(null)));
        Assert.Contains("connection reset", ex2.Message);

        Assert.Empty(Directory.GetFiles(Path.Combine(temp, "InterviewCoach-update")));
        Directory.Delete(temp, recursive: true);
        Directory.Delete(temp2, recursive: true);
    }

    [Fact]
    public async Task A_download_cancelled_by_the_user_leaves_no_file()
    {
        var (installer, _, temp) = NewInstaller(_ => Bytes(SetupBytes));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.DownloadAsync(Update(null), null, cts.Token));

        Assert.Empty(Directory.GetFiles(Path.Combine(temp, "InterviewCoach-update")));
        Directory.Delete(temp, recursive: true);
    }

    [Fact]
    public void Launching_starts_the_setup_program_with_a_visible_progress_window_and_a_restart_of_the_program()
    {
        var (installer, launcher, temp) = NewInstaller(_ => Bytes(SetupBytes));

        installer.Launch(@"C:\temp\setup.exe");

        var started = Assert.Single(launcher.Started);
        Assert.Equal(@"C:\temp\setup.exe", started.File);
        Assert.Equal("/SILENT /NORESTART /CLOSEAPPLICATIONS /RESTARTAPP=1", started.Args);
        Assert.DoesNotContain("VERYSILENT", started.Args);                    // the user should see that something is happening
        Directory.Delete(temp, recursive: true);
    }

    [Fact]
    public void A_setup_program_that_cannot_be_started_is_reported_in_words()
    {
        var (installer, launcher, temp) = NewInstaller(_ => Bytes(SetupBytes));
        launcher.Throw = new System.ComponentModel.Win32Exception("The operation was canceled by the user");

        var ex = Assert.Throws<UpdateException>(() => installer.Launch(@"C:\temp\setup.exe"));

        Assert.Contains("could not be started", ex.Message);
        Directory.Delete(temp, recursive: true);
    }

    // ---- an installed copy and an unzipped one

    [Fact]
    public void A_copy_in_the_folder_the_installer_recorded_is_an_installed_copy_and_any_other_folder_is_not()
    {
        var keyPath = @"Software\InterviewCoachTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
                key.SetValue("InstallLocation", @"C:\Users\Someone\AppData\Local\Programs\Interview Coach\");

            Assert.True(new InstallationInfo(@"C:\Users\Someone\AppData\Local\Programs\Interview Coach", keyPath).InstalledBySetup);
            Assert.True(new InstallationInfo(@"c:\users\someone\appdata\local\programs\interview coach\", keyPath).InstalledBySetup);     // case and the trailing slash do not matter
            Assert.False(new InstallationInfo(@"C:\Downloads\InterviewCoach", keyPath).InstalledBySetup);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\InterviewCoachTests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Without_an_installer_entry_a_copy_is_not_an_installed_one()
        => Assert.False(new InstallationInfo(@"C:\Anywhere", @"Software\InterviewCoachTests\does-not-exist").InstalledBySetup);

    [Fact]
    public void The_registry_key_the_app_looks_for_is_the_one_the_installer_script_writes()
    {
        // The installer's AppId and the key below must stay equal, or an installed copy would not know it was installed.
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "installer", "InterviewCoach.iss"));

        Assert.Contains("AppId={{6F2B8E54-3D1A-4C7B-9A0E-5B7C1D2E4F83}", script);
        Assert.EndsWith(@"\{6F2B8E54-3D1A-4C7B-9A0E-5B7C1D2E4F83}_is1", InstallationInfo.UninstallKey);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InterviewCoach.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
