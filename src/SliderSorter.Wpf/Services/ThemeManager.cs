using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SliderSorter.Wpf.Services;

/// <summary>
/// 界面主题管理：向 Application 资源装载对应色板字典（Palette.Light / Palette.Boutique）。
/// 控件样式与视图一律用 DynamicResource 引用 B.* 颜色键，替换字典即对全部已打开窗口即时生效。
/// 刻意不用 Application.ThemeMode（Fluent）：它注入的主题样式在隐式样式层压过 Controls.xaml
/// （下拉框会变成点不开的纯文本），且属预览 API；窗口明暗标题栏改由 DWM API 实现。
/// </summary>
public static class ThemeManager
{
    public const string Boutique = "boutique";
    public const string Light = "light";

    /// <summary>当前主题（设置文件里的值；默认 Boutique 暗色）。</summary>
    public static string Current { get; private set; } = Boutique;

    private static bool _chromeHooked;

    public static void Apply(string? theme)
    {
        Current = theme == Light ? Light : Boutique;
        var isLight = Current == Light;

        var dicts = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/Palette.{(isLight ? "Light" : "Boutique")}.xaml"),
        };
        // 合并字典查找"后加的优先"：清掉旧色板，把新色板追加到末尾（最优先）
        for (var i = dicts.Count - 1; i >= 0; i--)
        {
            if (dicts[i].Source?.OriginalString.Contains("Palette.") == true)
                dicts.RemoveAt(i);
        }
        dicts.Add(palette);

        if (!_chromeHooked)
        {
            // 之后创建的每个窗口（含各对话框）按当前主题设置标题栏明暗
            EventManager.RegisterClassHandler(typeof(Window), Window.LoadedEvent,
                new RoutedEventHandler((s, _) =>
                {
                    if (s is Window w)
                        ApplyTitleBar(w);
                }));
            _chromeHooked = true;
        }

        // 已打开的窗口立即更新标题栏
        foreach (Window w in Application.Current.Windows)
            ApplyTitleBar(w);
    }

    /// <summary>暗色主题 → DWM 深色标题栏；亮色 → 系统默认（浅色）。失败静默（旧系统无此属性）。</summary>
    public static void ApplyTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            var dark = Current == Boutique ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch
        {
            // DWM 不可用时保持系统默认标题栏即可
        }
    }

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
