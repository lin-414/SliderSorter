using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace BSGroupGenerator.Wpf.Services;

/// <summary>使用说明正文：先给「常见任务速查」（我要做什么 → 怎么做），再给界面参考手册。
/// 正文取自语言资源（L.Help_*），FlowDocument 排版，可滚动、可复制。
///
/// 开头放目录而不是长篇正文，是因为原来的帮助是一整块密排文字：用户带着一个具体问题
/// （"某个服装归错组了怎么办"）进来，得从头读到尾。任务导向的入口 + 可跳转目录能把
/// 这一步压到一眼。
///
/// 原先这套构建长在 HelpWindow 的代码后台里；说明改成设置页内嵌后，宿主换了而正文没变，
/// 所以抽到这里——FlowDocument 只能被一个宿主持有，故每次调用现造一份。</summary>
public static class HelpDocument
{
    /// <summary>目录项 → 章节锚点。键与标题 Paragraph 的 Name 一一对应。
    /// 顺序即正文顺序：第八章讲"一次扫描的两份成果"，排在常见问题之后——
    /// 它是概念说明而非操作步骤，放前面会打断"先学会做一件事"的主线。</summary>
    private static readonly (string Key, string Anchor)[] Toc =
    [
        ("L.Help_H1", "sec1"),
        ("L.Help_H2", "sec2"),
        ("L.Help_H3", "sec3"),
        ("L.Help_H4", "sec4"),
        ("L.Help_H5", "sec5"),
        ("L.Help_H6", "sec6"),
        ("L.Help_H7", "sec7"),
        ("L.Help_H8", "sec8"),
    ];

    public static FlowDocument Build()
    {
        var doc = new FlowDocument { PagePadding = new Thickness(20), FontSize = 13 };
        // 颜色走色板键而不是写死：暗色主题下正文必须是浅色，而 DynamicResource 在
        // ThemeManager 换色板时会跟着热切换。
        doc.SetResourceReference(TextElement.ForegroundProperty, "B.Text");
        var anchors = new Dictionary<string, Paragraph>(StringComparer.Ordinal);

        Paragraph P(string text, bool bold = false, bool heading = false)
        {
            var para = new Paragraph(new Run(text)
            {
                FontWeight = heading || bold ? FontWeights.Bold : FontWeights.Normal,
            });
            if (heading)
            {
                para.FontSize = 15;
                para.Margin = new Thickness(0, 14, 0, 4);
            }
            else
            {
                para.Margin = new Thickness(0, 2, 0, 2);
            }
            return para;
        }

        Paragraph B(string text) => P("  · " + text);

        /// 章节标题，同时登记为锚点。
        Paragraph H(string key, string anchor)
        {
            var para = P(L10n.Tr(key), heading: true);
            para.Name = anchor;
            anchors[anchor] = para;
            return para;
        }

        doc.Blocks.Add(P(L10n.Tr("L.Help_01")));

        // 目录。FlowDocumentScrollViewer 不会自己处理 Hyperlink 的片段导航，
        // 所以拦下 RequestNavigate 直接对目标段落 BringIntoView —— 比给每段挂
        // <c>&lt;a name&gt;</c> 等价物可靠，也不依赖滚动查看器的内部实现。
        doc.Blocks.Add(P(L10n.Tr("L.Help_Toc"), heading: true));
        doc.Blocks.Add(P(L10n.Tr("L.Help_TocHint")));
        foreach (var (key, anchor) in Toc)
        {
            var link = new Hyperlink(new Run(L10n.Tr(key)))
            {
                // NavigateUri 只为「可点 + 手型光标」而设，真正的跳转由下面的 RequestNavigate 做。
                NavigateUri = new Uri("#" + anchor, UriKind.Relative),
                TextDecorations = null,
            };
            link.SetResourceReference(TextElement.ForegroundProperty, "B.AccentText");
            link.RequestNavigate += (_, e) =>
            {
                e.Handled = true;
                // ⚠️ 别读 e.Uri.Fragment：NavigateUri 是**相对** URI，而 Uri.Fragment 对相对 URI 会抛
                // InvalidOperationException（"This operation is not supported for a relative URI"），
                // 结果目录里每一条链接点下去都弹一次错误框。锚点直接取循环变量即可
                //（foreach 的迭代变量每次迭代独立，闭包捕获安全）。
                if (anchors.TryGetValue(anchor, out var target))
                    target.BringIntoView();
            };
            doc.Blocks.Add(new Paragraph(link) { Margin = new Thickness(0, 1, 0, 1) });
        }

        // 一、常见任务速查
        doc.Blocks.Add(H("L.Help_H1", "sec1"));
        doc.Blocks.Add(B(L10n.Tr("L.Help_Task1")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_Task2")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_Task3")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_Task4")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_Task5")));

        // 二、快速上手
        doc.Blocks.Add(H("L.Help_H2", "sec2"));
        doc.Blocks.Add(P(L10n.Tr("L.Help_02")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_03")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_04")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_05")));

        // 三、界面各区域说明
        doc.Blocks.Add(H("L.Help_H3", "sec3"));
        doc.Blocks.Add(P(L10n.Tr("L.Help_S2_Top")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_06")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_07")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_08")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_09")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_S2_Tree")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_10")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_11")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_12")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_13")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_S2_Groups")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_14")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_15")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_16")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_17")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_18")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_S2_Status")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_19")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_20")));

        // 四、保存与文件布局
        doc.Blocks.Add(H("L.Help_H4", "sec4"));
        doc.Blocks.Add(P(L10n.Tr("L.Help_21")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_22")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_23")));

        // 五、规则归组
        doc.Blocks.Add(H("L.Help_H5", "sec5"));
        doc.Blocks.Add(P(L10n.Tr("L.Help_24")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_25")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_26")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_27")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_28")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_29")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_30")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_31")));

        // 六、撤销与输出位置
        doc.Blocks.Add(H("L.Help_H6", "sec6"));
        doc.Blocks.Add(P(L10n.Tr("L.Help_32")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_33")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_34")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_35")));
        doc.Blocks.Add(B(L10n.Tr("L.Help_36")));

        // 七、常见问题
        doc.Blocks.Add(H("L.Help_H7", "sec7"));
        doc.Blocks.Add(P(L10n.Tr("L.Help_37")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_38")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_39")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_40")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_41")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_42")));

        // 八、两份独立成果
        doc.Blocks.Add(H("L.Help_H8", "sec8"));
        doc.Blocks.Add(P(L10n.Tr("L.Help_Conflict_Intro")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_Conflict_A")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_Conflict_B")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_Conflict_C")));
        doc.Blocks.Add(P(L10n.Tr("L.Help_Conflict_D")));

        return doc;
    }
}
