using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopDock;

internal enum DockUpdateStatus
{
    UpToDate,
    UpdateAvailable,
    PackageMissing,
    NoRelease,
    RateLimited,
    Unavailable,
    InvalidRelease
}

internal sealed record DockUpdateResult(
    DockUpdateStatus Status,
    Version? LatestVersion = null,
    Uri? ReleasePage = null);

internal sealed class DockUpdateService(HttpClient httpClient)
{
    private const string ApiEndpoint = "https://api.github.com/repos/Diona86/DesktopDock/releases/latest";

    internal static Uri ReleasesPage { get; } = new("https://github.com/Diona86/DesktopDock/releases");

    internal static DockUpdateService Shared { get; } = new(new HttpClient(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    })
    {
        MaxResponseContentBufferSize = 1024 * 1024
    });

    internal async Task<DockUpdateResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiEndpoint);
        request.Headers.UserAgent.ParseAdd("DesktopDock");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        try
        {
            using var response = await httpClient.SendAsync(request, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new(DockUpdateStatus.NoRelease);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                (response.StatusCode == HttpStatusCode.Forbidden &&
                 (response.Headers.RetryAfter is not null ||
                  (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0")))))
            {
                return new(DockUpdateStatus.RateLimited);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new(DockUpdateStatus.Unavailable);
            }

            using var content = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: deadline.Token).ConfigureAwait(false);
            return ReadRelease(document.RootElement, currentVersion);
        }
        catch (HttpRequestException)
        {
            return new(DockUpdateStatus.Unavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(DockUpdateStatus.Unavailable);
        }
        catch (JsonException)
        {
            return new(DockUpdateStatus.InvalidRelease);
        }
    }

    private static DockUpdateResult ReadRelease(JsonElement release, Version currentVersion)
    {
        if (release.ValueKind != JsonValueKind.Object ||
            !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("tag_name", out var tagProperty) || tagProperty.ValueKind != JsonValueKind.String)
        {
            return new(DockUpdateStatus.InvalidRelease);
        }

        var tag = tagProperty.GetString()!;
        var versionText = tag.StartsWith('v') ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out var version) || version.Build < 0 || version.Revision != -1 ||
            versionText != version.ToString(3))
        {
            return new(DockUpdateStatus.InvalidRelease);
        }

        if (version <= currentVersion)
        {
            return new(DockUpdateStatus.UpToDate, version);
        }

        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return new(DockUpdateStatus.InvalidRelease);
        }

        var packageName = $"DesktopDock-{version}-win-x64-setup.exe";
        var hasPackage = assets.EnumerateArray().Any(asset =>
            asset.ValueKind == JsonValueKind.Object &&
            asset.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.ValueEquals(packageName) &&
            asset.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.ValueEquals("uploaded"));

        return new(hasPackage ? DockUpdateStatus.UpdateAvailable : DockUpdateStatus.PackageMissing,
            version, new Uri($"{ReleasesPage}/tag/{tag}"));
    }
}
