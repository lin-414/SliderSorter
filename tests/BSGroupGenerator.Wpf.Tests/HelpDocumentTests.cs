using System.Threading;
using System.Windows.Documents;
using System.Windows.Navigation;
using BSGroupGenerator.Wpf.Services;
using Xunit;

namespace BSGroupGenerator.Wpf.Tests;

/// <summary>使用说明的目录超链接必须真的能跳。
///
/// 起因是一次真实崩溃：<c>Hyperlink.NavigateUri</c> 是**相对** URI（<c>"#sec1"</c>），而处理函数读了
/// <c>e.Uri.Fragment</c> —— <c>Uri.Fragment</c> 对相对 URI 会抛 InvalidOperationException
/// （"This operation is not supported for a relative URI"），于是目录里每一条链接点下去都弹一次错误框。
///
/// 这里把每条链接的 RequestNavigate 真发一遍。**同时断言 Handled 已置位**——否则万一处理函数压根没被
/// 调用（例如事件名写错），测试会「因为什么都没发生」而假通过。
///
/// 正文原先长在 HelpWindow 的代码后台，现在内嵌进设置页，构建逻辑抽到 Services/HelpDocument，
/// 所以这里测的就是那份 FlowDocument 本身。
///
/// 与 MainWindowCommandTests 同属 WpfSta 集合：WPF 的 Application 是进程级单例、只能构造一次，
/// 两个 STA 用例并行会撞在 InvalidOperationException 上。这里也**不 Show 任何窗口**——
/// 最后一个窗口一关，共享的 Application 就 Shutdown 了，后面每个碰 Application 的用例一起遭殃。
/// 目录链接要验的是"处理函数被调到、Handled 置位、不抛异常"，这三件事都不需要排版。</summary>
[Collection(WpfStaCollection.Name)]
public class HelpDocumentTests
{
    [Fact]
    public void TocLinksNavigateWithoutThrowing()
    {
        Exception? captured = null;
        var handled = new List<bool>();

        // WPF 对象只能在 STA 线程上构造，而 xUnit 默认跑在 MTA 线程池线程上。
        var thread = new Thread(() =>
        {
            try
            {
                var doc = HelpDocument.Build();

                var links = doc.Blocks.OfType<Paragraph>()
                    .SelectMany(p => p.Inlines.OfType<Hyperlink>())
                    .ToList();
                Assert.NotEmpty(links);

                foreach (var link in links)
                {
                    var args = new RequestNavigateEventArgs(link.NavigateUri!, "help")
                    {
                        RoutedEvent = Hyperlink.RequestNavigateEvent,
                    };
                    link.RaiseEvent(args);
                    handled.Add(args.Handled);
                }
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(captured);
        Assert.NotEmpty(handled);
        Assert.All(handled, h => Assert.True(h, "目录链接的 RequestNavigate 没有被处理"));
    }
}
