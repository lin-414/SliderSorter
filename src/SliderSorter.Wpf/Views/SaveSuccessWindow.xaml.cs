using System.Windows;
using SliderSorter.Wpf.ViewModels;

using SliderSorter.Wpf.Services;

namespace SliderSorter.Wpf.Views;

/// <summary>保存成功提示：带「打开输出目录」「打开 BodySlide 目录」快捷按钮。</summary>
public partial class SaveSuccessWindow : Window
{
    private readonly string _dir;
    private readonly string? _bsAppDir;

    /// <summary>targetDescription 来自 ResolveTargetDescription()（可空：有效项目路径或 BodySlide 目录
    /// 未能解析时返回 null）。它只作为 {3} 的格式化实参，null 会被渲染成空串，故此处声明为可空。</summary>
    public SaveSuccessWindow(int groupCount, string dir, string examples, int memberCount,
        string? targetDescription, string? bsAppDir, bool customNote)
    {
        InitializeComponent();
        _dir = dir;
        _bsAppDir = bsAppDir;
        OpenOut.Content = L10n.Tr("L.SaveOk_OpenOut");
        OpenBs.Content = L10n.Tr("L.SaveOk_OpenBs");
        if (bsAppDir is null)
            OpenBs.Visibility = Visibility.Collapsed;
        MessageText.Text =
            L10n.TrF("L.SaveOk_Msg", dir, examples, groupCount > 3 ? " …" : "", targetDescription, memberCount) +
            customNoteText(customNote) + "\n\n" + L10n.Tr("L.SaveOk_Restart");
    }

    private static string customNoteText(bool custom) =>
        custom ? "\n\n" + L10n.Tr("L.SaveOk_CustomNote") : "";

    private void OpenOut_Click(object sender, RoutedEventArgs e)
    {
        MainViewModel.OpenDirectory(_dir);
        DialogResult = true;
    }

    private void OpenBs_Click(object sender, RoutedEventArgs e)
    {
        if (_bsAppDir is not null)
            MainViewModel.OpenDirectory(_bsAppDir);
        DialogResult = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
