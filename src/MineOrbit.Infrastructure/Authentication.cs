using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MineOrbit.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using XboxAuthNet.Game.Accounts;
using XboxAuthNet.Game.Accounts.JsonStorage;
using XboxAuthNet.Game.OAuth;
using Microsoft.Identity.Client;
using XboxAuthNet.Game.Msal;
using XboxAuthNet.Game;

namespace MineOrbit.Infrastructure;

public sealed class AuthenticationFailureException(string message, bool requiresInteraction = false) : Exception(message)
{ public bool RequiresInteraction { get; } = requiresInteraction; }
public sealed record DeviceLoginPrompt(string UserCode, string VerificationUrl, DateTimeOffset Expires);

public sealed class DpapiJsonStorage(string path) : IJsonStorage
{
    public JsonNode? ReadAsJsonNode()
    {
        if (!File.Exists(path)) return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return JsonNode.Parse(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Write(JsonNode node, JsonSerializerOptions? serializerOptions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plain = Encoding.UTF8.GetBytes(node.ToJsonString(serializerOptions));
        try
        {
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temp, encrypted); File.Move(temp, path, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
public sealed class MicrosoftAuthenticationService : IDisposable
{
    private JELoginHandler? handler;
    private IPublicClientApplication? application;
    private HttpClient? authenticationHttp;
    private CancellationToken activeLoginToken;
    private (string Phase, int Status)? lastServiceFailure;
    private readonly LauncherSettingsAccessor settings;
    private readonly string profileDirectory;
    private string AccountDirectory => Path.Combine(profileDirectory, "accounts", settings.Value.MicrosoftClientId.Trim());
    public bool HasSavedAccount => Guid.TryParse(settings.Value.MicrosoftClientId, out _) && File.Exists(Path.Combine(AccountDirectory, "accounts.dpapi"));
    public string? Username { get; private set; }
    public event Action<DeviceLoginPrompt?>? DevicePromptChanged;
    public event Action<string>? StageChanged;
    public MicrosoftAuthenticationService(LauncherSettingsAccessor settings, string? profileDirectory = null)
    {
        this.settings = settings;
        this.profileDirectory = profileDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MineOrbit");
    }
    private JELoginHandler Handler => handler ??= Create();
    private JELoginHandler Create()
    {
        var id = settings.Value.MicrosoftClientId.Trim();
        if (!Guid.TryParse(id, out _)) throw new AuthenticationFailureException("请先在设置中填写并保存你自己的 Microsoft 应用 Client ID，回调配置为 http://localhost，并获准访问 Minecraft API。");
        var storage = new DpapiJsonStorage(Path.Combine(AccountDirectory, "accounts.dpapi"));
        var accounts = new JsonXboxGameAccountManager(storage, JEGameAccount.FromSessionStorage, JsonXboxGameAccountManager.DefaultSerializerOption);
        application = PublicClientApplicationBuilder.Create(id).WithAuthority(AzureCloudInstance.AzurePublic, "consumers")
            .WithRedirectUri("http://localhost").Build();
        var cachePath = Path.Combine(AccountDirectory, "msal.dpapi");
        application.UserTokenCache.SetBeforeAccess(args =>
        {
            if (!File.Exists(cachePath)) return;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(cachePath), null, DataProtectionScope.CurrentUser);
            try { args.TokenCache.DeserializeMsalV3(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
        });
        application.UserTokenCache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged) return;
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var plain = args.TokenCache.SerializeMsalV3();
            var temp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temp, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); File.Move(temp, cachePath, true); }
            finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temp)) File.Delete(temp); }
        });
        authenticationHttp = new HttpClient(new AuthTransport(() => activeLoginToken, (phase, status) =>
        {
            if (status >= 400) { lastServiceFailure = (phase, status); StageChanged?.Invoke($"{phase}返回 HTTP {status}"); }
        })) { Timeout = TimeSpan.FromSeconds(45) };
        return new JELoginHandlerBuilder().WithHttpClient(authenticationHttp).WithAccountManager(accounts)
            .WithOAuthProvider(new XboxAuthNet.Game.Msal.OAuth.MsalCodeFlowProvider(application)).Build();
    }
    public async Task<MSession> Login(bool interactive, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(interactive ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(45));
        var loginToken = timeout.Token;
        activeLoginToken = loginToken; lastServiceFailure = null;
        Username = null;
        // A forced JE authenticator checks ownership/profile in the interactive flow.
        var h = Handler;
        var authenticator = h.CreateAuthenticator(h.AccountManager.GetDefaultAccount(), loginToken);
        StageChanged?.Invoke(interactive ? "正在请求 Microsoft 正版授权，请完成网页登录…" : "正在刷新 Microsoft 账号会话…");
        authenticator.AddMsalOAuth(application!, oauth => !interactive ? oauth.Silent()
            : settings.Value.LoginMode == MicrosoftLoginMode.DeviceCode ? oauth.DeviceCode(code =>
            {
                DevicePromptChanged?.Invoke(new(code.UserCode, code.VerificationUrl, code.ExpiresOn));
                return Task.CompletedTask;
            }) : oauth.SystemBrowser());
        authenticator.AddAuthenticatorWithoutValidator(new AuthStage(() => StageChanged?.Invoke("正在验证 Xbox Live 账号…")));
        if (interactive) authenticator.AddForceXboxAuthForJE(xbox => xbox.Basic());
        else authenticator.AddXboxAuthForJE(xbox => xbox.Basic());
        authenticator.AddAuthenticatorWithoutValidator(new AuthStage(() => StageChanged?.Invoke("正在验证 Minecraft Java 使用权限和角色档案…")));
        authenticator.AddForceJEAuthenticator(builder => builder.WithGameOwnershipChecker().Build());
        MSession session;
        try { session = await authenticator.ExecuteForLauncherAsync(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AuthenticationFailureException(interactive && timeout.IsCancellationRequested ? "登录等待超时，请重试并在五分钟内完成 Microsoft 授权。" : "认证服务响应超时，请检查网络后重试。"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var failure = ClassifyFailure(ex);
            throw lastServiceFailure is { } service ? new AuthenticationFailureException($"{service.Phase}（HTTP {service.Status}）：{failure.Message}", failure.RequiresInteraction) : failure;
        }
        finally { activeLoginToken = default; DevicePromptChanged?.Invoke(null); }
        if (!session.CheckIsValid() || string.IsNullOrWhiteSpace(session.AccessToken) || session.AccessToken == "0")
            throw new AuthenticationFailureException("未取得有效的 Minecraft Java Edition 正版会话，请重新登录。", true);
        Username = session.Username;
        return session;
    }
    public async Task<MSession> LoginForLaunch(CancellationToken ct)
    {
        if (!Handler.AccountManager.GetAccounts().Any(account => !string.IsNullOrEmpty(account.Identifier))) return await Login(true, ct);
        try { return await Login(false, ct); }
        catch (AuthenticationFailureException ex) when (ex.RequiresInteraction)
        { StageChanged?.Invoke("需要重新授权，将按选择的方式打开 Microsoft 正版登录…"); return await Login(true, ct); }
    }
    public static AuthenticationFailureException ClassifyFailure(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
        {
            if (current is MsalUiRequiredException) return new("账号需要重新授权，请完成 Microsoft 登录。", true);
            if (current is MsalException missing && missing.ErrorCode.StartsWith("loginHint was empty", StringComparison.Ordinal)) return new("尚未登录 Microsoft 账号，请完成首次授权。", true);
            if (current is MsalServiceException msal && msal.ErrorCode is "invalid_client" or "unauthorized_client")
                return new("Microsoft 应用配置无效：请检查 Client ID、支持个人账号、公共客户端流及 localhost 回调。邮箱不能代替 Client ID。");
            if (current is JEAuthException je)
            {
                var status = je.StatusCode;
                // Upstream sometimes wraps non-JSON HTTP errors as "403: Forbidden" with StatusCode=0.
                if (status == 0 && je.Message.Length >= 4 && je.Message[3] == ':' && int.TryParse(je.Message.AsSpan(0, 3), out var parsed) && parsed is >= 400 and <= 599) status = parsed;
                if (status == 403) return new("Minecraft 服务拒绝此应用或账号。请确认应用已获准访问 Minecraft API，并检查账号限制。");
                if (status == 404) return new("未找到 Minecraft Java 角色档案。请确认账号拥有 Java Edition，并已在官方启动器完成首次角色设置。");
                if (status == 401) return new("Minecraft 账号会话已失效，请重新完成 Microsoft 授权。", true);
                if (je.Message == "The user doesn't own the game.") return new("未能验证 Minecraft Java Edition 使用权限。请确认登录的是拥有该游戏的账号，并检查服务网络。");
                if (status > 0) return new($"Minecraft 认证服务返回 HTTP {status}。请检查应用准入、账号权限和网络后重试。");
                if (je.Message is "UserHash was empty. Xbox authentication is required." or "XstsToken was empty. Xbox authentication is required.") return new("Xbox 会话不完整，请重新完成 Microsoft 授权。", true);
                if (je.Message is "The Mojang server returned empty UUID." or "The Mojang server returned empty username.") return new("Minecraft 返回了不完整的角色档案，请先在官方启动器完成角色设置。");
            }
            if (current is HttpRequestException) return new("无法连接 Microsoft / Xbox / Minecraft 认证服务。请检查网络后重试。");
        }
        return new("正版认证未完成。请检查应用配置、Minecraft API 准入和 Java Edition 权限。错误类型：" + error.GetType().Name);
    }
    private sealed class AuthStage(Action action) : XboxAuthNet.Game.Authenticators.IAuthenticator
    { public ValueTask ExecuteAsync(XboxAuthNet.Game.Authenticators.AuthenticateContext context) { context.CancellationToken.ThrowIfCancellationRequested(); action(); return ValueTask.CompletedTask; } }
    public void Logout()
    {
        if (handler != null) handler.AccountManager.ClearAccounts();
        var path = Guid.TryParse(settings.Value.MicrosoftClientId, out _) ? AccountDirectory : profileDirectory;
        foreach (var file in new[] { "accounts.dpapi", "msal.dpapi" }) if (File.Exists(Path.Combine(path, file))) File.Delete(Path.Combine(path, file));
        Reset();
    }
    public void Reset() { handler = null; application = null; Username = null; authenticationHttp?.Dispose(); authenticationHttp = null; }
    public void Dispose() => authenticationHttp?.Dispose();
    private sealed class AuthTransport(Func<CancellationToken> activeToken, Action<string, int> responseStatus) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, activeToken());
            var response = await base.SendAsync(request, linked.Token);
            var phase = request.RequestUri?.AbsolutePath switch
            {
                "/authentication/login_with_xbox" => "Minecraft 令牌交换",
                "/minecraft/profile" => "Minecraft 角色档案验证",
                "/entitlements/mcstore" => "Minecraft 游戏权限验证",
                _ => "Xbox 账号验证"
            };
            responseStatus(phase, (int)response.StatusCode);
            return response;
        }
    }
}
public sealed class LauncherSettingsAccessor { public LauncherSettingsAccessor(MineOrbit.Core.LauncherSettings value) => Value = value; public MineOrbit.Core.LauncherSettings Value { get; set; } }
