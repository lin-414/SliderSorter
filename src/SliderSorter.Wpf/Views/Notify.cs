using System.Windows;
using SliderSorter.Core;
using SliderSorter.Wpf.Services;

namespace SliderSorter.Wpf.Views;

/// <summary>
/// 提示/确认框的统一出口。
/// <para>
/// 为什么不直接用系统 MessageBox：暗色主题下它会弹出一个纯白矩形，图标是 Vista 时代的位图，
/// 按钮文字还跟随**操作系统**语言——英文界面里冒出中文「确定」，直接破坏了"四种界面语言可切换"的承诺。
/// 全部调用点都收在这里，样式与语言才由程序自己掌握。
/// </para>
/// </summary>
public static class Notify
{
    /// <summary>弹提示框。返回用户选择了哪个按钮。</summary>
    /// <param name="destructive">主按钮是否代表破坏性操作（用实心危险色）。</param>
    /// <param name="settings">传入后启用「不再提示」记忆；须同时给 <paramref name="suppressKey"/>。</param>
    /// <param name="suppressKey">「不再提示」的记忆键。已被记住时不再弹框，直接返回 <see cref="NotifyResult.Cancel"/>
    /// （即"什么都不做"）—— 对更新提示这类"要不要去做某事"的询问，记住"不再提示"必须意味着不动作。</param>
    public static NotifyResult Show(Window? owner, string title, string message,
        NotifyKind kind = NotifyKind.Info,
        NotifyButtons buttons = NotifyButtons.Ok,
        bool destructive = false,
        AppSettings? settings = null,
        string? suppressKey = null)
    {
        if (settings is not null && suppressKey is not null
            && settings.SuppressedPrompts.Contains(suppressKey, StringComparer.Ordinal))
            return NotifyResult.Cancel;

        var dialog = new NotifyDialog(title, message, kind, buttons,
            showDontAsk: settings is not null && suppressKey is not null,
            destructive: destructive);
        if (owner is not null && owner.IsLoaded)
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen; // 无宿主窗口（启动早期/崩溃提示）时居中到屏幕

        dialog.ShowDialog();

        if (dialog.Result == NotifyResult.Primary && dialog.DontAsk
            && settings is not null && suppressKey is not null)
        {
            settings.SuppressedPrompts.Add(suppressKey);
            settings.Save();
        }
        return dialog.Result;
    }

    /// <summary>仅告知（一个「确定」按钮）。</summary>
    public static void Info(Window? owner, string title, string message) =>
        Show(owner, title, message);

    /// <summary>警告告知。</summary>
    public static void Warn(Window? owner, string title, string message) =>
        Show(owner, title, message, NotifyKind.Warning);

    /// <summary>错误告知。</summary>
    public static void Error(Window? owner, string title, string message) =>
        Show(owner, title, message, NotifyKind.Error);

    /// <summary>确认（确定 / 取消）。</summary>
    public static bool Confirm(Window? owner, string title, string message, bool destructive = false) =>
        Show(owner, title, message, NotifyKind.Question, NotifyButtons.OkCancel, destructive)
            == NotifyResult.Primary;
}
