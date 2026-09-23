using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SliderSorter.Core;
using Pfim;

namespace SliderSorter.Wpf.Services;

/// <summary>把 Data 相对路径的 DDS 变成能贴到 WPF 3D 上的位图，并按路径缓存。
/// <para>
/// 为什么不能直接交给 <see cref="BitmapImage"/>：WIC 只认老式 fourCC 的 DXT1/DXT3/DXT5，
/// 天际 SE/AE 的纹理大量是 DX10 头 + BC7，喂进去抛 COMException 0x88982F61（"图像标题无法识别"）。
/// 所以用纯托管的 Pfim 解成 32 位，再自己建 BitmapSource——这也是不引入原生 DLL 的前提下的唯一路子。
/// </para>
/// 可以在后台线程调用：产出的位图一律 Freeze，因此不归属某个 Dispatcher。</summary>
public sealed class PreviewTextureCache
{
    /// <summary>解码后保留的最长边。预览面板只有几百像素宽，而 2048² 解出来一张就是 16 MB；
    /// 一件衣服四五张贴图会把工作集推高几十 MB，缩到 1024 在这个尺寸上看不出差别。</summary>
    private const int MaxEdge = 1024;

    /// <summary>缓存条数上限。纹理在服装之间高度复用（同一套 CBBE 衣服共用身体贴图），
    /// 超过这个数说明用户在快速翻列表，旧的就该让位。</summary>
    private const int MaxCached = 32;

    private readonly GameDataResolver _resolver;
    private readonly object _gate = new();
    private readonly Dictionary<string, ImageSource?> _cached = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();

    public PreviewTextureCache(GameDataResolver resolver) => _resolver = resolver;

    /// <summary>取一张贴图；路径为空、文件找不到或解不开都返回 null（调用方退回纯色）。</summary>
    public ImageSource? Get(string? dataRelativePath)
    {
        if (GameDataResolver.Normalize(dataRelativePath) is not { } path)
            return null;

        lock (_gate)
        {
            if (_cached.TryGetValue(path, out var hit))
            {
                Touch(path);
                return hit;
            }
        }

        var decoded = Decode(path);

        lock (_gate)
        {
            if (_cached.Count >= MaxCached)
            {
                // 淘汰最久没用的那条：预览是"沿着列表往下翻"，最近用过的下一件多半还要用
                var oldest = _order.Count > 0 ? _order[0] : null;
                if (oldest is not null)
                {
                    _order.RemoveAt(0);
                    _cached.Remove(oldest);
                }
            }
            _cached[path] = decoded;
            _order.Add(path);
        }
        return decoded;
    }

    ImageSource? Decode(string path)
    {
        try
        {
            var bytes = _resolver.TryRead(path);
            if (bytes is not { Length: > 128 })
                return null;

            using var ms = new MemoryStream(bytes, writable: false);
            var image = Pfimage.FromStream(ms);
            var format = PixelFormatOf(image.Format);
            if (format is null || image.Stride <= 0)
                return null;

            var source = BitmapSource.Create(image.Width, image.Height, 96, 96, format.Value,
                null, image.Data, image.Stride);

            if (Math.Max(image.Width, image.Height) <= MaxEdge)
            {
                source.Freeze();
                return source;
            }

            var scale = MaxEdge / (double)Math.Max(image.Width, image.Height);
            var shrunk = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale));
            shrunk.Freeze();
            return shrunk;
        }
        catch (Exception)
        {
            // 归档损坏、Pfim 不支持的 DXGI 格式、位图尺寸溢出——都当成"这张贴图没有"，
            // 让那一个形状退回纯色，而不是把整件预览赔掉
            return null;
        }
    }

    /// <summary>Pfim 的格式枚举按"内存里的字节序"命名：它的 Rgba32 实际是 B,G,R,A，
    /// Rgb24 实际是 B,G,R——所以这里映射到 WPF 的 Bgra32/Bgr24，反过来就满屏红蓝互换。</summary>
    static PixelFormat? PixelFormatOf(ImageFormat format) => format switch
    {
        ImageFormat.Rgba32 => PixelFormats.Bgra32,
        ImageFormat.Rgb24 => PixelFormats.Bgr24,
        ImageFormat.R5g6b5 => PixelFormats.Bgr565,
        // 16 位与浮点格式（BC6H 环境贴图、R5g5b5a1）在 WPF 的 3D 纹理格式表里没有对应项，
        // 而它们本来就只出现在环境贴图上——diffuse 一律是 32/24 位，所以退回纯色就是正确行为
        _ => null,
    };

    private void Touch(string path)
    {
        var i = _order.IndexOf(path);
        if (i >= 0 && i != _order.Count - 1)
        {
            _order.RemoveAt(i);
            _order.Add(path);
        }
    }
}
