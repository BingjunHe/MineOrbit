using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using MineOrbit.Infrastructure;

namespace MineOrbit.UI;

public sealed partial class MainViewModel
{
    public string ExportDestination
    {
        get => Settings.PackExportDirectory;
        set { Settings.PackExportDirectory = value; Notify(); CommandManager.InvalidateRequerySuggested(); }
    }
    public string OfficialLauncherDirectory
    {
        get => string.IsNullOrWhiteSpace(Settings.OfficialLauncherDirectory) ? OfficialLauncherRegistrationService.DefaultRoot : Settings.OfficialLauncherDirectory;
        set { Settings.OfficialLauncherDirectory = value; Notify(); }
    }
    private string exportResult = "选择整合包后，可直接导出到独立文件夹；也可以选择以前导出的文件夹，添加到官方启动器。";
    public string ExportResult { get => exportResult; private set { exportResult = value; Notify(); } }
    public ICommand ChooseExportFolderCommand { get; private set; } = null!;
    public ICommand ChooseOfficialFolderCommand { get; private set; } = null!;
    public ICommand ExportPackFolderCommand { get; private set; } = null!;
    public ICommand RegisterOfficialLauncherCommand { get; private set; } = null!;
    public ICommand OpenExportFolderCommand { get; private set; } = null!;
    private void SetupPackExport(PackFolderExportService exporter, OfficialLauncherRegistrationService registration)
    {
        ChooseExportFolderCommand = Command(_ =>
        {
            var dialog = new OpenFolderDialog { Title = "选择新建的空文件夹，或以前导出的整合包文件夹" };
            if (Directory.Exists(ExportDestination)) dialog.InitialDirectory = ExportDestination;
            if (dialog.ShowDialog() == true) ExportDestination = dialog.FolderName;
            return Task.CompletedTask;
        });
        ChooseOfficialFolderCommand = Command(_ =>
        {
            var dialog = new OpenFolderDialog { Title = "选择官方启动器的 .minecraft 数据目录" };
            if (Directory.Exists(OfficialLauncherDirectory)) dialog.InitialDirectory = OfficialLauncherDirectory;
            if (dialog.ShowDialog() == true) OfficialLauncherDirectory = dialog.FolderName;
            return Task.CompletedTask;
        });
        ExportPackFolderCommand = Command(async ct =>
        {
            if (!File.Exists(PackPath)) throw new InvalidOperationException("请先选择并检查 ZIP / MRPACK 整合包。");
            if (string.IsNullOrWhiteSpace(ExportDestination)) throw new InvalidOperationException("请先选择一个新的空文件夹作为导出位置。");
            await ApplySettingsForOperation(ct);
            var options = new PackImportOptions(PackName, PackMinecraft, PackLoader, PackLoaderVersion, IncludeOptionalPackFiles);
            var progress = Progress();
            var result = await Task.Run(() => exporter.Export(PackPath, options, ExportDestination, progress, ct), ct);
            ExportDestination = result.Directory;
            ExportResult = $"导出成功：{result.Manifest.Name}\n文件夹：{result.Directory}\n版本：{result.Manifest.LaunchVersion}\nPCL / HMCL：添加此目录并选择版本，实际游戏运行目录也需设为此文件夹。\n官方启动器：关闭官方启动器后，点击下方“添加到官方启动器”。";
            SetStatus("完整游戏文件夹已导出。导出不需要 MineOrbit 登录，进入游戏由目标启动器进行正版认证。");
            CommandManager.InvalidateRequerySuggested();
        }, () => !string.IsNullOrWhiteSpace(PackPath));
        RegisterOfficialLauncherCommand = Command(async ct =>
        {
            await ApplySettingsForOperation(ct); var progress = Progress();
            var result = await Task.Run(() => registration.Register(ExportDestination, OfficialLauncherDirectory, progress, ct), ct);
            ExportResult = $"已添加到官方启动器。\n游戏内容仍在：{ExportDestination}\n官方版本：{result.VersionId}\n原配置备份：{string.Join("\n", result.Backups)}\n重新打开官方启动器，在 Java 版安装配置中选择整合包；必要时启用“修改版 / Modded”。";
            SetStatus("官方启动项已添加，原有配置已备份。请在官方启动器使用正版账号启动。");
        }, () => File.Exists(Path.Combine(ExportDestination ?? "", PackFolderExportService.ManifestName)));
        OpenExportFolderCommand = Command(_ => { OpenFolder(ExportDestination); return Task.CompletedTask; }, () => Directory.Exists(ExportDestination));
    }
}
