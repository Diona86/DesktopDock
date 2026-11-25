using System.Net;
using DesktopDock;

const string ReleaseUrl = "https://github.com/Diona86/DesktopDock/releases/tag/";
var installed = new Version(1, 2, 9);
var newer = new Version(1, 10, 0);
var package = """[{"name":"DesktopDock-1.10.0-win-x64-setup.exe","state":"uploaded","browser_download_url":"https://example.org/other.exe"}]""";
var wrongAssets = """[false,{"name":42},{"name":"DesktopDock-1.10.0-win-x64-setup.exe","state":"starter"},{"name":"DesktopDock-1.10.0-win-arm64-setup.exe","state":"uploaded"}]""";

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

async Task Check(string label, HttpResponseMessage response, DockUpdateResult expected)
{
    using var client = new HttpClient(new ProbeHandler((_, _) => Task.FromResult(response)));
    Expect(label, expected, await new DockUpdateService(client).CheckAsync(installed));
}

static string Release(string tag, string assets, bool draft = false, bool prerelease = false) =>
    $$"""{"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"assets":{{assets}},"html_url":"https://example.org/not-a-release"}""";

static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) =>
    new(status) { Content = new StringContent(json) };

static void Expect(string label, DockUpdateResult expected, DockUpdateResult actual)
{
    if (expected != actual)
    {
        throw new InvalidOperationException($"{label}. Expected {expected}, received {actual}.");
    }
    Console.WriteLine($"PASS {label}");
}

internal sealed class ProbeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get ||
            request.RequestUri?.AbsoluteUri != "https://api.github.com/repos/Diona86/DesktopDock/releases/latest" ||
            request.Headers.UserAgent.ToString() != "DesktopDock" ||
            request.Headers.Accept.ToString() != "application/vnd.github+json" ||
            request.Headers.Authorization is not null)
        {
            throw new InvalidOperationException("The request must use the expected public GitHub endpoint and headers.");
        }
        return respond(request, cancellationToken);
    }
}
