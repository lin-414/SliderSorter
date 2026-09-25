using System.ComponentModel;
using SliderSorter.Core;

namespace SliderSorter.Wpf.ViewModels;

/// <summary>
/// 实例下拉的条目：给 Mo2Instance 包一层 INotifyPropertyChanged。
/// <para>
/// 为什么不把 Mo2Instance 直接塞给 ComboBox 的 DisplayMemberPath：DisplayName 是 CoreStrings
/// 现拼的普通 CLR 属性，Core 不知道语言这回事、自身无从发通知，WPF 只在容器生成时求值一次，
/// 之后换语言这格文字就定死在旧语言里（2026-09-25 的「英文界面冒出全局」）。包一层之后，
/// OnLanguageChanged 逐项发 DisplayName 的通知，绑定直接重读属性——不依赖容器重建
/// （实测换 ItemsSource 集合外壳再回填同一引用，WPF 认为"内容没变"，选中框的文本不会重建）。
/// </para>
/// </summary>
public sealed class InstanceItem : INotifyPropertyChanged
{
    public InstanceItem(Mo2Instance instance) => Instance = instance;

    /// <summary>被包装的实例：下拉经 SelectedValuePath 取它，回填 MainViewModel.SelectedInstance。</summary>
    public Mo2Instance Instance { get; }

    public string DisplayName => Instance.DisplayName;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>语言切换后由 OnLanguageChanged 调：DisplayName 是现拼串，让绑定重读一次。</summary>
    public void RefreshDisplay() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
}
