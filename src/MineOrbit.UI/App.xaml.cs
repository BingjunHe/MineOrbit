using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using MineOrbit.Infrastructure;
using Serilog;

namespace MineOrbit.UI;
public partial class App : Application
{
    private ServiceProvider? services;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var settingsService = new SettingsService();
            var settings = await settingsService.Load();
            var logs = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MineOrbit", "logs");
            Log.Logger = new LoggerConfiguration().WriteTo.File(System.IO.Path.Combine(logs, "launcher-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7).CreateLogger();
            var collection = new ServiceCollection();
            collection.AddSingleton(settingsService).AddSingleton(new LauncherSettingsAccessor(settings));
            collection.AddSingleton(new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) });
            collection.AddSingleton<MicrosoftAuthenticationService>().AddSingleton<MinecraftVersionService>()
                .AddSingleton<ModLoaderInstallationService>().AddSingleton<JavaRuntimeService>()
                .AddSingleton<MinecraftInstallationService>().AddSingleton<MinecraftLaunchService>()
                .AddSingleton<ModpackImportService>().AddSingleton<PackInstallationService>()
                .AddSingleton<GameDirectoryService>().AddSingleton<BackupService>().AddSingleton<DownloadManager>()
                .AddSingleton<ModrinthService>().AddSingleton<CurseForgeService>().AddSingleton<ModCatalogService>().AddSingleton<IModCatalog>(p => p.GetRequiredService<ModCatalogService>())
                .AddSingleton<ModDependencyResolver>().AddSingleton<ModManagementService>().AddSingleton<MainViewModel>();
            services = collection.BuildServiceProvider();
            var vm = services.GetRequiredService<MainViewModel>();
            var window = new MainWindow { DataContext = vm }; MainWindow = window;
            var width = double.IsFinite(settings.LauncherWindowWidth) ? settings.LauncherWindowWidth : 1180;
            var height = double.IsFinite(settings.LauncherWindowHeight) ? settings.LauncherWindowHeight : 800;
            window.Width = Math.Clamp(width, window.MinWidth, Math.Max(window.MinWidth, SystemParameters.WorkArea.Width));
            window.Height = Math.Clamp(height, window.MinHeight, Math.Max(window.MinHeight, SystemParameters.WorkArea.Height));
            window.Show();
            await vm.Initialize();
        }
        catch (Exception ex)
        {
            // Startup diagnostics cannot contain a login session: authentication has not run yet.
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "startup-error.txt"), ex.ToString());
            MessageBox.Show("启动失败：" + ex.Message, "MineOrbit"); Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { services?.Dispose(); Log.CloseAndFlush(); base.OnExit(e); }
}
