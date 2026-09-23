using System.Windows;
using SliderSorter.Wpf.Services;

namespace SliderSorter.Wpf.Views;

/// <summary>提示框的语气。只影响图标与主按钮配色。</summary>
public enum NotifyKind
{
    Info,
    Warning,
    Error,
    Question,
}

/// <summary>提示框的按钮组合。</summary>
public enum NotifyButtons
{
    Ok,
    OkCancel,
    YesNoCancel,
}

/// <summary>提示框结果。用三值枚举而不是 bool：退出时的三分支（保存 / 不保存 / 留下）
/// 无法用"确定还是取消"表达。</summary>
public enum NotifyResult
{
    Primary,
    Secondary,
    Cancel,
}

/// <summary>自绘提示框（替代系统 MessageBox 的统一样式）。
/// 不用 MessageBox 的原因见 Notify 的说明。</summary>
public partial class NotifyDialog : Window
{
    public string DialogTitle { get; }
    public string Message { get; }
    public NotifyKind Kind { get; }
    public bool ShowDontAsk { get; }
    public bool DontAsk { get; set; }
    public bool ShowSecondary { get; }
    public bool ShowCancel { get; }
    public string PrimaryText { get; }
    public string SecondaryText { get; }

    /// <summary>关闭方式。点标题栏的 × 不会走任何 Click，保持默认的 Cancel。</summary>
    public NotifyResult Result { get; private set; } = NotifyResult.Cancel;

    internal NotifyDialog(string title, string message, NotifyKind kind, NotifyButtons buttons,
        bool showDontAsk, bool destructive)
    {
        InitializeComponent();
        DialogTitle = title;
        Message = message;
        Kind = kind;
        ShowDontAsk = showDontAsk;
        ShowSecondary = buttons == NotifyButtons.YesNoCancel;
        ShowCancel = buttons != NotifyButtons.Ok;
        PrimaryText = buttons == NotifyButtons.YesNoCancel ? L10n.Tr("L.Btn_Yes") : L10n.Tr("L.Btn_Ok");
        SecondaryText = L10n.Tr("L.Btn_No");
        DataContext = this;
        // 破坏性操作的主按钮用实心危险色，别让"删除"看起来和"确定"一样
        PrimaryButton.Style = (Style)FindResource(destructive ? "DangerSolidButton" : "PrimaryButton");
        Loaded += (_, _) => PrimaryButton.Focus();
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        Result = NotifyResult.Primary;
        DialogResult = true;
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        Result = NotifyResult.Secondary;
        DialogResult = false;
    }
}
