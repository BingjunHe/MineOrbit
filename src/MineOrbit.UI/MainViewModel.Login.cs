using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using MineOrbit.Core;

namespace MineOrbit.UI;
public sealed record LoginModeChoice(MicrosoftLoginMode Mode, string Label) { public override string ToString() => Label; }
public sealed partial class MainViewModel
{
    private string deviceInstructions = "";
    public string DeviceInstructions { get => deviceInstructions; private set { deviceInstructions = value; Notify(); Notify(nameof(HasDevicePrompt)); CommandManager.InvalidateRequerySuggested(); } }
    public bool HasDevicePrompt => DeviceInstructions.Length > 0;
    public LoginModeChoice[] LoginModes { get; } = [new(MicrosoftLoginMode.SystemBrowser, "浏览器登录（推荐）"), new(MicrosoftLoginMode.DeviceCode, "设备码登录")];
    public ICommand OpenVerificationCommand { get; private set; } = null!;
    public ICommand LoginHelpCommand { get; private set; } = null!;
    private void SetupLogin()
    {
        auth.StageChanged += message => Application.Current.Dispatcher.InvokeAsync(() => SetStatus(message, false));
        auth.DevicePromptChanged += prompt => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            DeviceInstructions = prompt == null ? "" : $"Microsoft 正版登录设备码：{prompt.UserCode}\n在 microsoft.com/devicelogin 输入此代码，使用拥有 Java Edition 的账号登录。有效期至 {prompt.Expires.LocalDateTime:t}。\n设备码只显示在当前窗口，不会保存或写入日志。";
        });
        OpenVerificationCommand = new ActionCommand(() => { Process.Start(new ProcessStartInfo("https://microsoft.com/devicelogin") { UseShellExecute = true }); return Task.CompletedTask; }, () => HasDevicePrompt);
        LoginHelpCommand = Command(_ => { new LoginHelpWindow { Owner = Application.Current.MainWindow }.Show(); return Task.CompletedTask; });
    }
    private bool EnsureLoginConfiguration()
    {
        if (Guid.TryParse(settings.Value.MicrosoftClientId, out _)) return true;
        Page = 6;
        new LoginHelpWindow { Owner = Application.Current.MainWindow }.Show();
        SetStatus("请先完成 MineOrbit 应用注册，将 Client ID 填入设置并保存。账号邮箱不是 Client ID。");
        return false;
    }
}
