using SliderSorter.Core;

namespace SliderSorter.Tests;

public class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bsgg-tests", System.IO.Path.GetRandomFileName());
        Directory.CreateDirectory(Path);
    }

    public string Sub(params string[] parts)
    {
        var dir = System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string File(params string[] partsAndContentSplit)
    {
        // 最后一个参数是内容，前面是相对路径
        var parts = partsAndContentSplit[..^1];
        var content = partsAndContentSplit[^1];
        var full = System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
