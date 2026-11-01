using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Mcdl.App.ViewModels;

namespace Mcdl.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // TextBox 的 TwoWay 绑定在挂载时会先用目标端的空值推给源端，导致界面上显示不出默认目录，
        // 这里显式对齐一次，保证「看到的」和「实际用的」是同一个路径。
        if (DataContext is MainViewModel vm)
        {
            if (string.IsNullOrWhiteSpace(PathBox.Text)) PathBox.Text = vm.TargetDir;
            vm.TargetDir = PathBox.Text;

            // 页面切换时做个 180ms 的淡入 + 上移，和 PCL 的过渡观感一致
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(MainViewModel.IsVersionTab)
                    or nameof(MainViewModel.IsResourceTab)
                    or nameof(MainViewModel.IsAboutTab))
                    AnimateCurrentPage(vm);
            };
        }

        // 小屏 / 高缩放比的机器上，别让窗口跑到屏幕外面去
        var screen = Screens.Primary;
        if (screen is null) return;

        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var maxWidth = screen.WorkingArea.Width / scale * 0.94;
        var maxHeight = screen.WorkingArea.Height / scale * 0.94;

        Width = Math.Min(Width, Math.Max(820, maxWidth));
        Height = Math.Min(Height, Math.Max(560, maxHeight));
        Position = new PixelPoint(
            screen.WorkingArea.X + (int)((screen.WorkingArea.Width - Width * scale) / 2),
            screen.WorkingArea.Y + (int)((screen.WorkingArea.Height - Height * scale) / 2));
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 .minecraft 文件夹",
            AllowMultiple = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(vm.TargetDir),
        });

        var folder = folders.FirstOrDefault();
        if (folder is null) return;

        var path = folder.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) vm.TargetDir = path;
    }

    /// <summary>资源页搜索框里按回车直接搜，不用去点按钮。</summary>
    private void OnResourceSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        if (DataContext is not MainViewModel vm) return;

        vm.SearchResourcesCommand.Execute(null);
        e.Handled = true;
    }

    // ---------- 自绘标题栏 ----------

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // ---------- 关于页链接 ----------

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 没有默认浏览器就算了 */ }
    }

    private void OnOpenPclRepoClick(object? sender, RoutedEventArgs e)
        => OpenUrl("https://github.com/Meloong-Git/PCL/");

    private void OnOpenSponsorClick(object? sender, RoutedEventArgs e)
        => OpenUrl("https://meloong.com/afd/a/LTCat");

    private void OnOpenLicenceClick(object? sender, RoutedEventArgs e)
        => OpenUrl("https://shimo.im/docs/rGrd8pY8xWkt6ryW#anchor-X0bo");

    // ---------- 页面切换动画（PCL 观感：180ms 淡入 + 上移） ----------

    private void AnimateCurrentPage(MainViewModel vm)
    {
        Control? page = vm.IsVersionTab ? PageVersion : vm.IsResourceTab ? PageResource : PageAbout;
        if (page is null) return;

        page.RenderTransform = new TranslateTransform(0, 10);
        page.Opacity = 0;

        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(180),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0d),
                        new Setter(TranslateTransform.YProperty, 10d),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1d),
                        new Setter(TranslateTransform.YProperty, 0d),
                    },
                },
            },
        };

        _ = animation.RunAsync(page);
    }
}
