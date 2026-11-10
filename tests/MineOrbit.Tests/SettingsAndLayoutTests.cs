using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using MineOrbit.Core;
using MineOrbit.Infrastructure;
using MineOrbit.UI;
using Xunit;

namespace MineOrbit.Tests;

public sealed class SettingsAndLayoutTests
{
    [Fact]
    public async Task AllPreferencesSurviveFreshStoreAndInvalidSaveKeepsPreviousSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "MineOrbit-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "settings.json");
            var java = Path.Combine(root, "java.exe");
            await File.WriteAllTextAsync(java, "path persistence fixture; never executed");
            var settings = new LauncherSettings
            {
                DataRoot = Path.Combine(root, "games"), JavaPath = java, MicrosoftClientId = Guid.NewGuid().ToString(),
                LoginMode = MicrosoftLoginMode.DeviceCode, MinimumRamMb = 2048, MaximumRamMb = 6144,
                Width = 1600, Height = 900, DefaultIsolation = IsolationMode.ByVersion,
                DownloadConcurrency = 7, ShareRuntimeAndAssets = false, ModDownloadSource = ModDownloadSource.Modrinth,
                LibrariesPath = Path.Combine(root, "libraries"), AssetsPath = Path.Combine(root, "assets"),
                RuntimePath = Path.Combine(root, "runtime"), DownloadCachePath = Path.Combine(root, "cache"),
                LastSelectedInstanceId = Guid.NewGuid(), LastPage = 6,
                LauncherWindowWidth = 820, LauncherWindowHeight = 550, CurseForgeApiKey = "test-key-not-a-real-secret"
            };
            await new SettingsService(file).Save(settings);
            var loaded = await new SettingsService(file).Load();
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(settings), System.Text.Json.JsonSerializer.Serialize(loaded));
            Assert.Equal(settings.CurseForgeApiKey, loaded.CurseForgeApiKey);
            Assert.DoesNotContain(settings.CurseForgeApiKey, await File.ReadAllTextAsync(file));
            settings.MaximumRamMb = 100;
            await Assert.ThrowsAsync<ArgumentException>(() => new SettingsService(file).Save(settings));
            Assert.Equal(6144, (await new SettingsService(file).Load()).MaximumRamMb);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SmallWindowBindingsSaveAndRestoreWithoutLeavingInput()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await CheckWindow(); completed.SetResult(); }
                catch (Exception ex) { completed.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private static async Task CheckWindow()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "MineOrbit-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        try
        {
            var store = new SettingsService(Path.Combine(rootPath, "settings.json"));
            var options = new LauncherSettings { DataRoot = Path.Combine(rootPath, "games") };
            var manager = new InstanceManager(options.DataRoot);
            await manager.Save(manager.New("First", "1.21.1", LoaderKind.Vanilla, ""));
            var selected = manager.New("Second", "1.20.1", LoaderKind.Fabric, "0.16.10");
            await manager.Save(selected);
            options.LastSelectedInstanceId = selected.Id;
            options.LastPage = 6;
            using var services = Services(store, options);
            var vm = services.GetRequiredService<MainViewModel>();
            var window = new MainWindow { DataContext = vm };
            await vm.Initialize();
            Assert.Equal(selected.Id, vm.Selected?.Id);
            Assert.Equal(6, vm.Page);
            var root = (Grid)window.Content;
            root.Background = (Brush)window.Background;
            root.ColumnDefinitions[0].Width = new GridLength(155);
            var pages = (TabControl)window.FindName("MainPages");
            var save = (Button)window.FindName("PinnedSaveButton");
            async Task Layout()
            {
                root.Measure(new Size(704, 400)); root.Arrange(new Rect(0, 0, 704, 400)); root.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            }
            for (var i = 0; i < 7; i++)
            {
                vm.Page = i; await Layout();
                var nav = (ListBox)window.FindName("NavigationList");
                foreach (ListBoxItem item in nav.Items)
                {
                    var itemBounds = item.TransformToAncestor(nav).TransformBounds(new Rect(item.RenderSize));
                    Assert.True(itemBounds.Top >= 0 && itemBounds.Bottom <= nav.ActualHeight, $"Navigation item clipped: {item.Content}");
                }
                var scroll = Assert.IsType<ScrollViewer>(pages.SelectedContent);
                Assert.True(scroll.ViewportHeight > 30, $"Page {i} has no usable viewport");
                scroll.ScrollToEnd(); await Layout();
                Assert.InRange(scroll.ScrollableHeight - scroll.VerticalOffset, 0, 1);
                foreach (var button in Walk(scroll).OfType<Button>())
                {
                    var buttonBounds = button.TransformToAncestor(scroll).TransformBounds(new Rect(button.RenderSize));
                    Assert.True(buttonBounds.Left >= -1 && buttonBounds.Right <= scroll.ActualWidth + 1, $"Page {i} clips button {button.Content}: {buttonBounds}");
                }
                if (i == 1)
                {
                    var nested = Walk(scroll).OfType<ScrollViewer>().First(x => x != scroll);
                    nested.ScrollToTop(); await Layout();
                    var previous = scroll.VerticalOffset;
                    nested.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                    await Layout();
                    Assert.True(scroll.VerticalOffset < previous, "Wheel at list boundary must scroll the enclosing page");
                }
                if (i == 6)
                {
                    Assert.Equal(Visibility.Visible, save.Visibility);
                    var bounds = save.TransformToAncestor(root).TransformBounds(new Rect(save.RenderSize));
                    Assert.True(bounds.Width > 50 && bounds.Height > 20);
                    Assert.True(bounds.Left >= 0 && bounds.Right <= 705 && bounds.Bottom <= 401, $"Save button clipped: {bounds}");
                }
                if (i == 0)
                {
                    var start = (Button)window.FindName("PinnedLaunchButton");
                    var bounds = start.TransformToAncestor(root).TransformBounds(new Rect(start.RenderSize));
                    Assert.Equal(Visibility.Visible, start.Visibility);
                    Assert.True(bounds.Width >= 100 && bounds.Right <= 705 && bounds.Bottom <= 401, "Pinned launch button must remain accessible");
                }
            }
            var clientId = Guid.NewGuid().ToString();
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.DeviceInstructions))!.SetValue(vm, new string('验', 180));
            await Layout();
            Assert.True(((ScrollViewer)pages.SelectedContent).ViewportHeight > 30, "Login instructions must not push page buttons out of the window");
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.DeviceInstructions))!.SetValue(vm, "");
            await Layout();
            TextBox Input(string name) => Walk(root).OfType<TextBox>().Single(x => BindingOperations.GetBinding(x, TextBox.TextProperty)?.Path.Path == "Settings." + name);
            Input("MicrosoftClientId").Text = clientId;
            Input("MinimumRamMb").Text = "2048";
            Input("MaximumRamMb").Text = "6144";
            Walk(root).OfType<CheckBox>().Single().IsChecked = false;
            Walk(root).OfType<ComboBox>().Single(x => BindingOperations.GetBinding(x, ComboBox.SelectedValueProperty)?.Path.Path == "Settings.LoginMode").SelectedValue = MicrosoftLoginMode.DeviceCode;
            Walk(root).OfType<ComboBox>().Single(x => BindingOperations.GetBinding(x, ComboBox.SelectedValueProperty)?.Path.Path == "Settings.DefaultIsolation").SelectedValue = IsolationMode.ByVersion;
            Assert.Equal(clientId, vm.Settings.MicrosoftClientId);
            Assert.Equal(6144, vm.Settings.MaximumRamMb);
            await vm.SaveEditedSettings();
            var loaded = await new SettingsService(store.ConfigFile).Load();
            Assert.Equal(clientId, loaded.MicrosoftClientId);
            Assert.Equal(2048, loaded.MinimumRamMb);
            Assert.Equal(6144, loaded.MaximumRamMb);
            Assert.False(loaded.ShareRuntimeAndAssets);
            Assert.Equal(MicrosoftLoginMode.DeviceCode, loaded.LoginMode);
            Assert.Equal(IsolationMode.ByVersion, loaded.DefaultIsolation);
            Assert.Equal(selected.Id, loaded.LastSelectedInstanceId);
            Assert.Equal(6, loaded.LastPage);
            Input("MaximumRamMb").Text = "not-a-number";
            Assert.True(window.HasInvalidInput);
            Input("MaximumRamMb").Text = "6144";
            Assert.False(window.HasInvalidInput);
            using var restarted = Services(new SettingsService(store.ConfigFile), loaded);
            var restored = restarted.GetRequiredService<MainViewModel>();
            await restored.Initialize();
            Assert.Equal(6, restored.Page);
            Assert.Equal(selected.Id, restored.Selected?.Id);
            // Render the actual WPF controls without opening a window or touching the user's profile.
            await Layout();
            var screenshot = new RenderTargetBitmap(704, 400, 96, 96, PixelFormats.Pbgra32);
            screenshot.Render(root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(screenshot));
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/test-results"));
            Directory.CreateDirectory(output);
            using (var stream = File.Create(Path.Combine(output, "small-window-settings.png"))) png.Save(stream);
            // Exercise the real close handler in an offscreen test window using only the temporary profile.
            window.ShowActivated = false; window.ShowInTaskbar = false;
            window.Left = -20000; window.Top = -20000; window.Width = 1180; window.Height = 800;
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            vm.Page = 0; ((ScrollViewer)pages.SelectedContent).ScrollToTop();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle); root.UpdateLayout();
            var home = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth + 40), (int)Math.Ceiling(root.ActualHeight + 40), 96, 96, PixelFormats.Pbgra32); home.Render(root);
            var homePng = new PngBitmapEncoder(); homePng.Frames.Add(BitmapFrame.Create(home));
            using (var stream = File.Create(Path.Combine(output, "home-0.4.png"))) homePng.Save(stream);
            window.Width = 760; window.Height = 480; vm.Page = 6;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var closingId = Guid.NewGuid().ToString();
            Input("MicrosoftClientId").Text = closingId;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var afterClose = await new SettingsService(store.ConfigFile).Load();
            Assert.Equal(closingId, afterClose.MicrosoftClientId);
            Assert.Equal(760, afterClose.LauncherWindowWidth);
            Assert.Equal(480, afterClose.LauncherWindowHeight);
        }
        finally { app.Shutdown(); Directory.Delete(rootPath, true); }
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject node)
    {
        yield return node;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(node, i))) yield return child;
    }

    private static ServiceProvider Services(SettingsService store, LauncherSettings options)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton(store).AddSingleton(new LauncherSettingsAccessor(options)).AddSingleton(new HttpClient());
        collection.AddSingleton(p => new MicrosoftAuthenticationService(p.GetRequiredService<LauncherSettingsAccessor>(), Path.Combine(Path.GetDirectoryName(store.ConfigFile)!, "test-accounts"))).AddSingleton<MinecraftVersionService>()
            .AddSingleton<ModLoaderInstallationService>().AddSingleton<JavaRuntimeService>()
            .AddSingleton<MinecraftInstallationService>().AddSingleton<MinecraftLaunchService>()
            .AddSingleton<ModpackImportService>().AddSingleton<PackInstallationService>()
            .AddSingleton<GameDirectoryService>().AddSingleton<BackupService>().AddSingleton<DownloadManager>()
            .AddSingleton<ModrinthService>().AddSingleton<CurseForgeService>().AddSingleton<ModCatalogService>().AddSingleton<IModCatalog>(p => p.GetRequiredService<ModCatalogService>())
            .AddSingleton<ModDependencyResolver>().AddSingleton<ModManagementService>().AddSingleton<MainViewModel>();
        return collection.BuildServiceProvider();
    }
}
