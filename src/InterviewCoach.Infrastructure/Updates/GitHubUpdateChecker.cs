using System.Net;
using System.Text.Json;
using InterviewCoach.Core.Updates;

namespace InterviewCoach.Infrastructure.Updates;

/// <summary>
/// Looks at the repository's latest GitHub release (public, no key needed; the API's "latest" is never a draft or a pre-release) and says
/// whether it is newer than the running version. One small request, sent only when the user allows update checks in Settings or asks for one.
/// </summary>
public sealed class GitHubUpdateChecker(HttpClient http, string owner, string repo) : IUpdateChecker
{
    public const string InstallerPrefix = "InterviewCoach-Setup-";

    public async Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound) return UpdateCheckResult.UpToDate; // nothing has been published yet
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return UpdateCheckResult.Failed("GitHub is limiting requests from this connection right now. Try again later.");
            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Failed($"GitHub answered with an error ({(int)response.StatusCode}).");

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            return Read(doc.RootElement, current);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return UpdateCheckResult.Failed("GitHub did not answer in time. Check your internet connection.");
        }
        catch (HttpRequestException ex)
        {
            return UpdateCheckResult.Failed("Could not reach GitHub to look for updates: " + ex.Message);
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Failed("GitHub's answer could not be read.");
        }
    }

    private UpdateCheckResult Read(JsonElement release, Version current)
    {
        var tag = Text(release, "tag_name");
        if (UpdateVersions.Parse(tag) is not { } version) return UpdateCheckResult.UpToDate; // a tag that is not a version is not an update
        if (!UpdateVersions.IsNewer(version, current)) return UpdateCheckResult.UpToDate;

        string? url = null, sha = null;
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name") ?? "";
                if (!name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                url = Text(asset, "browser_download_url");
                var digest = Text(asset, "digest");
                if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = digest["sha256:".Length..].Trim().ToLowerInvariant();
                break;
            }
        }

        var page = Text(release, "html_url") ?? $"https://github.com/{owner}/{repo}/releases/latest";
        return UpdateCheckResult.Available(new UpdateInfo(version, tag!, page, url, sha, Text(release, "body")));
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
