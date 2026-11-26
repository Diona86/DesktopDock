using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
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
    Uri? ReleasePage = null,
    DockUpdatePackage? Package = null);

internal sealed record DockUpdatePackage(Version Version, string Tag, long Size, string Sha256);

internal sealed class DockUpdateService(HttpClient httpClient)
{
    private const string ApiEndpoint = "https://api.github.com/repos/Diona86/DesktopDock/releases/latest";
    private const long MaxPackageBytes = 512L * 1024 * 1024;

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
        if (ReadTagVersion(tag) is not Version version)
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

        var packageName = PackageName(version);
        var asset = assets.EnumerateArray().FirstOrDefault(asset =>
            asset.ValueKind == JsonValueKind.Object &&
            asset.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.ValueEquals(packageName) &&
            asset.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.ValueEquals("uploaded"));

        var releasePage = new Uri($"{ReleasesPage}/tag/{tag}");
        if (asset.ValueKind == JsonValueKind.Undefined)
            return new(DockUpdateStatus.PackageMissing, version, releasePage);

        DockUpdatePackage? package = null;
        if (asset.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number &&
            size.TryGetInt64(out long bytes) && bytes is > 0 and <= MaxPackageBytes &&
            asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String &&
            digest.GetString() is string value && value.StartsWith("sha256:", StringComparison.Ordinal) &&
            IsSha256(value[7..]))
        {
            package = new(version, tag, bytes, value[7..]);
        }
        return new(DockUpdateStatus.UpdateAvailable, version, releasePage, package);
    }

    internal async Task<DockUpdateDownload> DownloadAsync(DockUpdatePackage package, IProgress<int>? progress,
        CancellationToken cancellationToken = default)
    {
        if (ReadTagVersion(package.Tag) != package.Version || package.Size is <= 0 or > MaxPackageBytes || !IsSha256(package.Sha256))
            throw new InvalidDataException("更新包校验信息无效。");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        var token = deadline.Token;
        token.ThrowIfCancellationRequested();
        var name = PackageName(package.Version);
        var url = new Uri($"{ReleasesPage}/download/{package.Tag}/{name}");
        var path = Path.Combine(Directory.CreateTempSubdirectory("DesktopDock-update-").FullName, name);
        var partial = path + ".partial";
        FileStream? lease = null;
        DockUpdateDownload? verified = null;
        try
        {
            using (var response = await GetInstallerAsync(url, token).ConfigureAwait(false))
            {
                if (response.Content.Headers.ContentLength is long contentLength && contentLength != package.Size)
                    throw new InvalidDataException("更新包大小与发行记录不符。");

                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                int lastProgress = 0;
                progress?.Report(lastProgress);
                var buffer = new byte[65536];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    received += read;
                    if (received > package.Size)
                        throw new InvalidDataException("下载的数据超出发行记录中的更新包大小。");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    var percent = (int)(received * 99 / package.Size);
                    if (percent != lastProgress)
                    {
                        progress?.Report(percent);
                        lastProgress = percent;
                    }
                }
                if (received != package.Size)
                    throw new InvalidDataException("更新包下载不完整。");
            }

            File.Move(partial, path);
            // Keep the verified bytes protected from replacement or edits through the launch.
            lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (lease.Length != package.Size ||
                !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(lease, token).ConfigureAwait(false),
                    Convert.FromHexString(package.Sha256)))
            {
                throw new InvalidDataException("更新包的 SHA-256 校验失败。");
            }
            token.ThrowIfCancellationRequested();
            progress?.Report(100);
            return verified = new DockUpdateDownload(path, lease);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("更新包下载超时，请稍后重试。");
        }
        finally
        {
            if (verified == null)
            {
                lease?.Dispose();
                DockUpdateDownload.DeleteTemporaryFiles(path);
            }
        }
    }

    private async Task<HttpResponseMessage> GetInstallerAsync(Uri url, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("DesktopDock");
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK)
                return response;

            using (response)
            {
                if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
                    throw new HttpRequestException($"GitHub 无法提供更新包（HTTP {(int)response.StatusCode}）。");
                if (response.Headers.Location is not { IsAbsoluteUri: true } next ||
                    next.Scheme != Uri.UriSchemeHttps || !next.IsDefaultPort || next.UserInfo.Length != 0 ||
                    next.Fragment.Length != 0 || next.IdnHost is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                {
                    throw new InvalidDataException("更新下载被重定向到非预期地址。");
                }
                url = next;
            }
        }
        throw new HttpRequestException("GitHub 更新下载重定向次数过多。");
    }

    private static Version? ReadTagVersion(string tag)
    {
        var versionText = tag.StartsWith('v') ? tag[1..] : tag;
        return Version.TryParse(versionText, out var version) && version.Build >= 0 && version.Revision == -1 &&
            versionText == version.ToString(3) ? version : null;
    }

    private static string PackageName(Version version) => $"DesktopDock-{version}-win-x64-setup.exe";
    private static bool IsSha256(string hash) => hash.Length == 64 && hash.All(char.IsAsciiHexDigit);
}

internal sealed class DockUpdateDownload(string path, FileStream lease) : IDisposable
{
    private bool keepForInstaller;

    internal string Path { get; } = path;

    internal void KeepForInstaller() => keepForInstaller = true;

    public void Dispose()
    {
        lease.Dispose();
        if (!keepForInstaller)
            DeleteTemporaryFiles(Path);
    }

    internal static void DeleteTemporaryFiles(string path)
    {
        try
        {
            File.Delete(path + ".partial");
            File.Delete(path);
            Directory.Delete(System.IO.Path.GetDirectoryName(path)!);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
