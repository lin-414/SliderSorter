namespace SliderSorter.Wpf.ViewModels;

/// <summary>日志级别。只用于呈现（展开后着色 + 折叠摘要计数），不参与任何逻辑判断。</summary>
public enum LogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>一行日志。保留级别而不是拼成纯文本，是为了让「扫描完成 · 2 个警告」
/// 这类摘要和展开后的着色都能直接从数据得出，不必再去正则匹配文本。</summary>
public sealed record LogLine(string Text, LogLevel Level);
