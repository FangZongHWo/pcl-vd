using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mcdl.App.ViewModels;

namespace Mcdl.App;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
        Loaded += (_, _) => HookViewModel();
    }

    private void HookViewModel()
    {
        if (DataContext is not MainViewModel vm || ReferenceEquals(vm, _vm)) return;
        _vm = vm;

        // TextBox 的 TwoWay 绑定在挂载时会先推空串，这里对齐一次
        if (string.IsNullOrWhiteSpace(PathBox.Text)) PathBox.Text = vm.TargetDir;
        vm.TargetDir = PathBox.Text;

        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.IsVersionTab)
                or nameof(MainViewModel.IsResourceTab)
                or nameof(MainViewModel.IsAboutTab))
                AnimateCurrentPage(vm);
        };
    }

    // ---------- 标题栏 ----------

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ---------- 目录选择 ----------

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 .minecraft 文件夹",
            InitialDirectory = System.IO.Directory.Exists(vm.TargetDir) ? vm.TargetDir : null,
        };

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            PathBox.Text = dialog.FolderName;
            vm.TargetDir = dialog.FolderName;
        }
    }

    // ---------- 搜索框回车 ----------

    private void OnResourceSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        if (DataContext is not MainViewModel vm) return;

        vm.SearchResourcesCommand.Execute(null);
        e.Handled = true;
    }

    // ---------- 关于页链接 ----------

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 没有默认浏览器就算了 */ }
    }

    private void OnOpenRepoClick(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/FangZongHWo/pcl-vd");

    private void OnOpenPclRepoClick(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/Meloong-Git/PCL/");

    private void OnOpenSponsorClick(object sender, RoutedEventArgs e) => OpenUrl("https://meloong.com/afd/a/LTCat");

    private void OnOpenLicenceClick(object sender, RoutedEventArgs e)
        => OpenUrl("https://shimo.im/docs/rGrd8pY8xWkt6ryW#anchor-X0bo");

    // ---------- 页面切换动画（180ms 淡入 + 上移） ----------

    private void AnimateCurrentPage(MainViewModel vm)
    {
        FrameworkElement? page = vm.IsVersionTab ? PageVersion
            : vm.IsResourceTab ? PageResource
            : vm.IsSettingsTab ? PageSettings
            : PageAbout;
        if (page is null) return;

        var translate = new TranslateTransform(0, 10);
        page.RenderTransform = translate;
        page.Opacity = 0;

        var duration = TimeSpan.FromMilliseconds(180);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        page.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, duration) { EasingFunction = ease });
    }
    // ---------- 整合包导入 ----------

    private void OnImportModpackClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择整合包",
            Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
            _ = ImportAsync(dialog.FileName);
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedPack(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!TryGetDroppedPack(e, out var path) || path is null) return;

        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this,
                "暂时只支持 Modrinth 的 .mrpack 整合包。\nCurseForge 的 .zip 整合包还没做（见路线图阶段 8.3）。",
                "还不支持这个格式", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _ = ImportAsync(path);
    }

    private static bool TryGetDroppedPack(DragEventArgs e, out string? path)
    {
        path = null;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return false;

        var first = files[0];
        if (first.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)
            || first.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            path = first;
            return true;
        }

        return false;
    }

    private async Task ImportAsync(string path)
    {
        if (DataContext is not MainViewModel vm) return;
        try { await vm.ImportModpackAsync(path); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "导入失败"); }
    }
}
