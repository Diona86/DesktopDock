using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using DesktopDock;

const string ReleaseUrl = "https://github.com/Diona86/DesktopDock/releases/tag/";
var installed = new Version(1, 2, 9);
var newer = new Version(1, 10, 0);
var package = """[{"name":"DesktopDock-1.10.0-win-x64-setup.exe","state":"uploaded","browser_download_url":"https://example.org/other.exe"}]""";
var wrongAssets = """[false,{"name":42},{"name":"DesktopDock-1.10.0-win-x64-setup.exe","state":"starter"},{"name":"DesktopDock-1.10.0-win-arm64-setup.exe","state":"uploaded"}]""";
var payload = OperatingSystem.IsWindows()
    ? File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "whoami.exe"))
    : new byte[] { 1, 2, 3, 4 };
var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
var verifiedPackage = new DockUpdatePackage(newer, "v1.10.0", payload.Length, digest);
var releasePage = new Uri(ReleaseUrl + verifiedPackage.Tag);
var verifiedAssets = $$"""[{"name":"DesktopDock-1.10.0-win-x64-setup.exe","state":"uploaded","size":{{payload.Length}},"digest":"sha256:{{digest}}","browser_download_url":"https://example.org/other.exe"}]""";

await Check("metadata enables one-click only with expected size and SHA-256", Reply(Release(verifiedPackage.Tag, verifiedAssets)),
    new(DockUpdateStatus.UpdateAvailable, newer, releasePage, verifiedPackage));
await Check("invalid digest leaves browser download available", Reply(Release(verifiedPackage.Tag, verifiedAssets.Replace(digest, "xyz"))),
    new(DockUpdateStatus.UpdateAvailable, newer, releasePage));
await Check("oversize metadata leaves browser download available", Reply(Release(verifiedPackage.Tag, verifiedAssets.Replace($"\"size\":{payload.Length}", "\"size\":536870913"))),
    new(DockUpdateStatus.UpdateAvailable, newer, releasePage));

await Check("newer stable release with x64 installer", Reply(Release("v1.10.0", package)),
    new(DockUpdateStatus.UpdateAvailable, newer, new Uri(ReleaseUrl + "v1.10.0")));
await Check("unpadded version without a v prefix", Reply(Release("1.10.0", package)),
    new(DockUpdateStatus.UpdateAvailable, newer, new Uri(ReleaseUrl + "1.10.0")));
await Check("matching version", Reply(Release("v1.2.9", "[]")), new(DockUpdateStatus.UpToDate, installed));
await Check("older release", Reply(Release("v1.2.8", "[]")), new(DockUpdateStatus.UpToDate, new Version(1, 2, 8)));
await Check("installer absent", Reply(Release("v1.10.0", "[]")),
    new(DockUpdateStatus.PackageMissing, newer, new Uri(ReleaseUrl + "v1.10.0")));
await Check("installer name and upload state must both match", Reply(Release("v1.10.0", wrongAssets)),
    new(DockUpdateStatus.PackageMissing, newer, new Uri(ReleaseUrl + "v1.10.0")));

foreach (var tag in new[] { "v1.10", "v1.10.0.0", "v01.10.0", "v1.10.0-beta", "V1.10.0", "../../different/repo", " 1.10.0" })
{
    await Check($"invalid version {tag}", Reply(Release(tag, package)), new(DockUpdateStatus.InvalidRelease));
}

await Check("prerelease", Reply(Release("v1.10.0", package, prerelease: true)), new(DockUpdateStatus.InvalidRelease));
await Check("draft", Reply(Release("v1.10.0", package, draft: true)), new(DockUpdateStatus.InvalidRelease));
await Check("malformed JSON", Reply("{"), new(DockUpdateStatus.InvalidRelease));
await Check("non-object JSON", Reply("[]"), new(DockUpdateStatus.InvalidRelease));
await Check("wrong asset list format", Reply(Release("v1.10.0", "null")), new(DockUpdateStatus.InvalidRelease));

await Check("no public release or inaccessible repository", Reply("{}", HttpStatusCode.NotFound), new(DockUpdateStatus.NoRelease));
await Check("rate limiting with HTTP 429", Reply("{}", HttpStatusCode.TooManyRequests), new(DockUpdateStatus.RateLimited));
var rateLimited = Reply("{}", HttpStatusCode.Forbidden);
rateLimited.Headers.Add("X-RateLimit-Remaining", "0");
await Check("rate limiting with HTTP 403", rateLimited, new(DockUpdateStatus.RateLimited));
await Check("unclassified forbidden request", Reply("{}", HttpStatusCode.Forbidden), new(DockUpdateStatus.Unavailable));
await Check("ignored redirect", Reply("{}", HttpStatusCode.Redirect), new(DockUpdateStatus.Unavailable));
await Check("GitHub unavailable", Reply("{}", HttpStatusCode.ServiceUnavailable), new(DockUpdateStatus.Unavailable));

var attempts = 0;
using var retryClient = new HttpClient(new ProbeHandler((_, _) =>
{
    attempts++;
    return attempts == 1
        ? Task.FromException<HttpResponseMessage>(new HttpRequestException("Simulated transport failure"))
        : Task.FromResult(Reply(Release("v1.2.9", "[]")));
}));
var retryService = new DockUpdateService(retryClient);
Expect("network failure", new(DockUpdateStatus.Unavailable), await retryService.CheckAsync(installed));
if (attempts != 1)
{
    throw new InvalidOperationException("A failed check should not retry automatically.");
}
Expect("user retries a previous network failure", new(DockUpdateStatus.UpToDate, installed), await retryService.CheckAsync(installed));

using var cancelClient = new HttpClient(new ProbeHandler(async (_, token) =>
{
    await Task.Delay(Timeout.Infinite, token);
    throw new InvalidOperationException("The request should have been canceled.");
}));
using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
try
{
    await new DockUpdateService(cancelClient).CheckAsync(installed, cancellation.Token);
    throw new InvalidOperationException("The caller's cancellation should propagate.");
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.WriteLine("PASS caller cancellation");
}

const string DownloadUri = "https://github.com/Diona86/DesktopDock/releases/download/v1.10.0/DesktopDock-1.10.0-win-x64-setup.exe";
const string CdnUri = "https://release-assets.githubusercontent.com/release-data?token=example";
var requests = new List<string>();
var reports = new List<int>();
using var downloadClient = new HttpClient(new ProbeHandler((request, _) =>
{
    var uri = request.RequestUri!.AbsoluteUri;
    requests.Add(uri);
    return Task.FromResult(uri == DownloadUri ? Redirect(CdnUri) : Binary(payload));
}, releaseCheck: false));
string stagingPath;
using (var download = await new DockUpdateService(downloadClient).DownloadAsync(verifiedPackage, new InlineProgress(reports.Add)))
{
    stagingPath = download.Path;
    Verify(requests.SequenceEqual(new[] { DownloadUri, CdnUri }), "download uses only pinned repository and approved CDN");
    Verify(File.ReadAllBytes(stagingPath).SequenceEqual(payload) && reports is [0, .., 100], "stage contains the verified bytes and reaches full progress");
    if (OperatingSystem.IsWindows())
    {
        try
        {
            using var unexpected = File.Open(stagingPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            throw new InvalidOperationException("A write handle bypassed the verified-file lease.");
        }
        catch (IOException)
        {
            Console.WriteLine("PASS verified installer lease rejects writes");
        }

        using var probe = Process.Start(new ProcessStartInfo(stagingPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        })!;
        await probe.WaitForExitAsync();
        Verify(probe.ExitCode == 0, "Windows can launch the executable while its verified lease is held");
    }
}
Verify(!File.Exists(stagingPath) && !Directory.Exists(Path.GetDirectoryName(stagingPath)), "discarding a verified download cleans only its staging folder");

foreach (var unsafeRedirect in new[]
{
    "https://example.org/other.exe", "http://release-assets.githubusercontent.com/file",
    "https://release-assets.githubusercontent.com:8443/file", "https://release-assets.githubusercontent.com.example.org/file"
})
{
    int requestCount = 0;
    await RejectDownload<InvalidDataException>("blocks untrusted download redirect", (_, _) =>
    {
        requestCount++;
        return Task.FromResult(Redirect(unsafeRedirect));
    });
    Verify(requestCount == 1, "blocked redirect never reaches its destination");
}

await RejectDownload<InvalidDataException>("rejects SHA-256 mismatch", (_, _) =>
{
    var altered = (byte[])payload.Clone();
    altered[^1] ^= 0xff;
    return Task.FromResult(Binary(altered));
});
await RejectDownload<InvalidDataException>("rejects inconsistent package size", (_, _) => Task.FromResult(Binary(payload[..^1])));
using var stopDownload = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
await RejectDownload<OperationCanceledException>("caller can cancel an installer download", async (_, token) =>
{
    await Task.Delay(Timeout.Infinite, token);
    throw new InvalidOperationException("The download should have been canceled.");
}, stopDownload.Token);

async Task RejectDownload<T>(string label, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
    CancellationToken token = default) where T : Exception
{
    var temporary = Path.GetTempPath();
    var original = Directory.GetDirectories(temporary, "DesktopDock-update-*");
    using var client = new HttpClient(new ProbeHandler(respond, releaseCheck: false));
    try
    {
        using var unexpected = await new DockUpdateService(client).DownloadAsync(verifiedPackage, progress: null, token);
    }
    catch (T)
    {
        Verify(!Directory.GetDirectories(temporary, "DesktopDock-update-*").Except(original).Any(), $"{label} and cleans staging");
        return;
    }
    throw new InvalidOperationException($"Expected failure for {label}.");
}

async Task Check(string label, HttpResponseMessage response, DockUpdateResult expected)
{
    using var client = new HttpClient(new ProbeHandler((_, _) => Task.FromResult(response)));
    Expect(label, expected, await new DockUpdateService(client).CheckAsync(installed));
}

static string Release(string tag, string assets, bool draft = false, bool prerelease = false) =>
    $$"""{"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"assets":{{assets}},"html_url":"https://example.org/not-a-release"}""";

static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) =>
    new(status) { Content = new StringContent(json) };

static HttpResponseMessage Binary(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

static HttpResponseMessage Redirect(string url) => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(url) } };

static void Verify(bool condition, string label)
{
    if (!condition)
        throw new InvalidOperationException(label);
    Console.WriteLine($"PASS {label}");
}

static void Expect(string label, DockUpdateResult expected, DockUpdateResult actual)
{
    if (expected != actual)
    {
        throw new InvalidOperationException($"{label}. Expected {expected}, received {actual}.");
    }
    Console.WriteLine($"PASS {label}");
}

internal sealed class ProbeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
    bool releaseCheck = true) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get ||
            request.Headers.UserAgent.ToString() != "DesktopDock" ||
            request.Headers.Authorization is not null ||
            (releaseCheck && (request.RequestUri?.AbsoluteUri != "https://api.github.com/repos/Diona86/DesktopDock/releases/latest" ||
                request.Headers.Accept.ToString() != "application/vnd.github+json")))
        {
            throw new InvalidOperationException("The request must use the expected public GitHub endpoint and headers.");
        }
        return respond(request, cancellationToken);
    }
}

internal sealed class InlineProgress(Action<int> report) : IProgress<int>
{
    public void Report(int value) => report(value);
}
