using System.Security.Cryptography;
using MineOrbit.Core;

namespace MineOrbit.Infrastructure;
public sealed class DownloadManager(HttpClient http, LauncherSettingsAccessor? settings = null)
{
    private static readonly HttpClient CredentialTransport = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
    public static bool IsCurseForgeCdn(Uri uri) => uri.Scheme == "https" && (uri.Host == "forgecdn.net" || uri.Host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase));
    public static HttpRequestMessage CreateRequest(Uri uri, string? curseForgeKey)
    {
        if (uri.Scheme != "https") throw new InvalidDataException("下载仅支持 HTTPS。");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("MineOrbit/" + LauncherIdentity.Version);
        if (IsCurseForgeCdn(uri) && !string.IsNullOrWhiteSpace(curseForgeKey)) request.Headers.Add("x-api-key", curseForgeKey);
        return request;
    }
    private async Task<HttpResponseMessage> SendDownload(Uri url, CancellationToken ct)
    {
        // Credentialed CDN requests never use an automatically redirecting transport.
        var credentialed = IsCurseForgeCdn(url) && !string.IsNullOrWhiteSpace(settings?.Value.CurseForgeApiKey);
        for (var redirect = 0; redirect < 6; redirect++)
        {
            using var request = CreateRequest(url, settings?.Value.CurseForgeApiKey);
            var response = await (credentialed ? CredentialTransport : http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location == null) throw new InvalidDataException("下载重定向缺少目标。");
            url = location.IsAbsoluteUri ? location : new Uri(url, location);
            if (url.Scheme != "https") throw new InvalidDataException("拒绝不安全的下载重定向。");
            if (IsCurseForgeCdn(url) && !string.IsNullOrWhiteSpace(settings?.Value.CurseForgeApiKey)) credentialed = true;
        }
        throw new HttpRequestException("下载重定向次数过多。");
    }
    public static async Task Verify(string file, string sha512, CancellationToken ct)
        => await VerifyHash(file, sha512, "sha512", ct);
    public static async Task VerifyHash(string file, string hash, string algorithm, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        var bytes = algorithm == "sha512" ? await SHA512.HashDataAsync(stream, ct) : algorithm == "sha1" ? await SHA1.HashDataAsync(stream, ct) : throw new ArgumentException("不支持的校验算法。");
        if (!Convert.ToHexString(bytes).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(algorithm + " 校验失败。");
    }
    public async Task Download(Uri url, string target, string sha512, long maxBytes, IProgress<WorkProgress>? progress, CancellationToken ct)
        => await DownloadHash(url, target, sha512, "sha512", maxBytes, progress, ct);
    public async Task DownloadHash(Uri url, string target, string hash, string algorithm, long maxBytes, IProgress<WorkProgress>? progress, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // Validate input before it can be used to construct any cache file paths.
                var hashLength = algorithm == "sha512" ? 128 : algorithm == "sha1" ? 40 : 0;
                if (url.Scheme != "https" || maxBytes <= 0 || hashLength == 0 || hash.Length != hashLength || !hash.All(Uri.IsHexDigit)) throw new ArgumentException("无效的下载参数。");
                string? cache = settings == null ? null : Path.Combine(CachePaths.Resolve(settings.Value, settings.Value.DownloadCachePath, "cache"), algorithm, hash.ToLowerInvariant());
                if (cache != null)
                {
                    GameDirectoryService.EnsureNoLinks(Path.GetDirectoryName(cache)!);
                    if (File.Exists(cache))
                    {
                        try { if (new FileInfo(cache).Length > maxBytes) throw new InvalidDataException("缓存文件过大。"); await VerifyHash(cache, hash, algorithm, ct); await CopyVerified(cache, target, ct); progress?.Report(new("已复用校验缓存", 1)); return; }
                        catch (InvalidDataException) { /* Re-download a corrupt cache entry. */ }
                    }
                }
                await DownloadAttempt(url, target, hash, algorithm, maxBytes, progress, ct);
                if (cache != null) await CopyVerified(target, cache, ct);
                return;
            }
            catch (HttpRequestException ex) when (attempt < 2 && (ex.StatusCode == null || (int)ex.StatusCode >= 500 || (int)ex.StatusCode == 429))
            { progress?.Report(new("网络下载失败，正在重试 " + (attempt + 1) + "/2")); await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct); }
        }
    }
    private static async Task CopyVerified(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        GameDirectoryService.EnsureNoLinks(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var input = File.OpenRead(source)) await using (var output = File.Create(temporary)) await input.CopyToAsync(output, ct);
            ct.ThrowIfCancellationRequested(); File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task DownloadAttempt(Uri url, string target, string hash, string algorithm, long maxBytes, IProgress<WorkProgress>? progress, CancellationToken ct)
    {
        var hashLength = algorithm == "sha512" ? 128 : algorithm == "sha1" ? 40 : 0;
        if (url.Scheme != "https" || maxBytes <= 0 || hashLength == 0 || hash.Length != hashLength || !hash.All(Uri.IsHexDigit)) throw new ArgumentException("无效的下载地址、Hash 或大小限制。");
        if (File.Exists(target))
        {
            try { await VerifyHash(target, hash, algorithm, ct); return; }
            catch (InvalidDataException) { /* Existing file is preserved until a valid replacement is ready. */ }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + "." + Guid.NewGuid().ToString("N") + ".part";
        using var activity = CancellationTokenSource.CreateLinkedTokenSource(ct);
        activity.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            using var response = await SendDownload(url, activity.Token);
            if (IsCurseForgeCdn(url) && response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new HttpRequestException("CurseForge 下载需要有效的授权 API Key。", null, response.StatusCode);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new InvalidDataException("拒绝不安全的重定向。");
            if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("下载文件过大。");
            await using (var input = await response.Content.ReadAsStreamAsync(activity.Token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long length = 0; int count;
                while ((count = await input.ReadAsync(buffer, activity.Token)) > 0)
                {
                    activity.CancelAfter(TimeSpan.FromSeconds(40));
                    length += count; if (length > maxBytes) throw new InvalidDataException("下载超出大小限制。");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    progress?.Report(new($"下载 {length:N0} bytes", response.Content.Headers.ContentLength is > 0 ? (double)length / response.Content.Headers.ContentLength.Value : null));
                }
            }
            await VerifyHash(partial, hash, algorithm, ct); ct.ThrowIfCancellationRequested(); File.Move(partial, target, true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new HttpRequestException("下载连接或传输长时间无响应，请检查网络后重试。"); }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
