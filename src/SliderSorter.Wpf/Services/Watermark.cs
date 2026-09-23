using System.Windows;

namespace SliderSorter.Wpf.Services;

/// <summary>TextBox 占位提示文字（配合 Modern.xaml 的 TextBox 模板使用）。</summary>
public static class Watermark
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Watermark), new PropertyMetadata(null));

    public static string? GetText(DependencyObject obj) => obj.GetValue(TextProperty) as string;

    public static void SetText(DependencyObject obj, string? value) => obj.SetValue(TextProperty, value);
}
