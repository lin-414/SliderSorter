using System.Windows;
using SliderSorter.Core;
using SliderSorter.Wpf.Services;

namespace SliderSorter.Wpf.Views;

/// <summary>
/// 提示/确认框的统一出口。
/// <para>
/// 为什么不直接用系统 MessageBox：暗色主题下它会弹出一个纯白矩形，图标是 Vista 时代的位图，
/// 按钮文字还跟随**操作系统**语言——英文界面里冒出中文「确定」，直接破坏了"五种界面语言可切换"的承诺。
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

    /// <summary>复制一段文本到剪贴板，并把结果说一句。
    /// <para>
    /// 为什么要接异常：WPF 在剪贴板被别的进程占着时抛 <c>ExternalException</c>（远程桌面、
    /// 剪贴板历史、输入法都可能正占着它），而它不在 App 的可恢复白名单里 —— 不接住就是
    /// 点一下「复制」整个程序按致命崩溃退出，还会在崩溃处理里再弹一个框。
    /// </para></summary>
    public static void CopyText(Window? owner, string title, string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Warn(owner, title, L10n.Tr("L.Msg_ClipboardFailed"));
            return;
        }
        Info(owner, title, L10n.Tr("L.Msg_CopiedToClipboard"));
    }

    /// <summary>确认（确定 / 取消）。</summary>
    public static bool Confirm(Window? owner, string title, string message, bool destructive = false) =>
        Show(owner, title, message, NotifyKind.Question, NotifyButtons.OkCancel, destructive)
            == NotifyResult.Primary;
}
