using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
namespace MineOrbit.UI;
public partial class MainWindow : Window
{
    private bool allowClose;
    private bool savingOnClose;
    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => SidebarColumn.Width = new GridLength(ActualWidth < 1000 ? 155 : 185);
        PreviewMouseWheel += ForwardWheelAtBoundary;
    }
    public bool HasInvalidInput => Descendants(Content as DependencyObject ?? this).Any(Validation.GetHasError);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || allowClose) return;
        e.Cancel = true;
        if (savingOnClose) return;
        if (DataContext is not MainViewModel vm) { allowClose = true; _ = Dispatcher.BeginInvoke(Close); return; }
        if (vm.IsBusy) { MessageBox.Show(this, "请先取消或等待当前任务完成；游戏运行中请先正常退出游戏。", "任务进行中"); return; }
        Keyboard.ClearFocus();
        if (HasInvalidInput) { MessageBox.Show(this, "有输入框的内容无效，请先修正红色标记。设置未保存，窗口将保持打开。", "检查设置"); return; }
        savingOnClose = true;
        try
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            vm.Settings.LauncherWindowWidth = bounds.Width;
            vm.Settings.LauncherWindowHeight = bounds.Height;
            await vm.SaveEditedSettings();
            allowClose = true; Close();
        }
        catch (Exception ex) { MessageBox.Show(this, "设置保存失败，窗口将保持打开：" + ex.Message, "未能保存设置"); }
        finally { savingOnClose = false; }
    }
    private void SaveVisibleSettings(object sender, RoutedEventArgs e)
    {
        Keyboard.ClearFocus();
        if (HasInvalidInput) { MessageBox.Show(this, "请先修正红色标记的输入内容，再保存设置。", "检查设置"); return; }
        if (DataContext is MainViewModel vm && vm.SaveSettingsCommand.CanExecute(null)) vm.SaveSettingsCommand.Execute(null);
    }
    private void PageUp(object sender, RoutedEventArgs e) { if (MainPages.SelectedContent is ScrollViewer viewer) viewer.PageUp(); }
    private void PageDown(object sender, RoutedEventArgs e) { if (MainPages.SelectedContent is ScrollViewer viewer) viewer.PageDown(); }
    private void ForwardWheelAtBoundary(object sender, MouseWheelEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        if (current is FrameworkContentElement content) current = content.Parent;
        ScrollViewer? nearest = null;
        while (current is Visual)
        {
            if (current is ScrollViewer viewer)
            {
                if (nearest == null)
                {
                    nearest = viewer;
                    if (e.Delta > 0 && viewer.VerticalOffset > 0 || e.Delta < 0 && viewer.VerticalOffset < viewer.ScrollableHeight) return;
                }
                else if (viewer.ScrollableHeight > 0)
                { viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta); e.Handled = true; return; }
            }
            current = VisualTreeHelper.GetParent(current);
        }
    }
    private void OnCurseKeyChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is System.Windows.Controls.PasswordBox box)
            vm.Settings.CurseForgeApiKey = box.Password;
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.IsBusy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths)
            await vm.Inspect(paths[0]);
    }
}
