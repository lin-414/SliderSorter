using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using NiflySharp;
using NiflySharp.Blocks;
using SliderSorter.Core;
using SliderSorter.Wpf.Services;
using SliderSorter.Wpf.ViewModels;
using SliderSorter.Wpf.Views.Pages;

namespace PreviewProbe;

/// <summary>
/// 3D 预览渲染探针。几种模式（在仓库根目录执行）：
///
///   dotnet run --project tools/preview-probe -c Release -- --calib out/
///       合成网格走生产渲染路径（OutputConflictPage.AddShape）：轴向映射、UV 的 V 轴、背面剔除，
///       全部用像素回读判定，不需要任何游戏数据。
///
///   dotnet run --project tools/preview-probe -c Release -- --stats 120
///       对真实 .nif 做几何体检：核对 NifPreviewModel.cs 注释里那些"实测 N 个形状里有 M 个……"的断言。
///
///   dotnet run --project tools/preview-probe -c Release -- --shots out/ 6
///       真实服装走生产 OutputConflictPage 出多视角 PNG + 每帧读数（覆盖率/轮廓宽高/色数/暗面/锯齿比）。
///
///   dotnet run --project tools/preview-probe -c Release -- --pose out/ 4
///       同一条冲突换模组来源时，把相机摆位逐行打出来（该逐行相同）+ 逐行 PNG，叠着看就知道动没动。
///
/// 只读：不改仓库任何源文件，PNG 与报告写到 out/。会读 %APPDATA%\SliderSorter\settings.json
/// 并由 MainViewModel 按既有行为回写它（跑之前自己备份一份）。
/// </summary>
internal static class Program
{
    private const int RenderWidth = 620;
    private const int RenderHeight = 760;

    /// <summary>把"应用资源程序集"指到 SliderSorter，好让 ThemeManager / L10n 那种不带程序集名的
    /// pack URI 解析得到——不指的话 L10n 的快照建不起来，界面文案全成了裸键。</summary>
    private static void UseAppAssemblyAsResourceAssembly()
    {
        var asm = typeof(OutputConflictPage).Assembly;
        try
        {
            Application.ResourceAssembly = asm;
        }
        catch (InvalidOperationException)
        {
            // WPF 在进程启动时就把这个属性钉住了（探针没有 App.xaml，没人替我们设过它），只能走字段
            var t = typeof(Application);
            t.GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, asm);
            t.GetField("_resourceAssemblyExplicitlySet", BindingFlags.NonPublic | BindingFlags.Static)
                ?.SetValue(null, true);
        }
        if (Application.ResourceAssembly != asm)
            Console.WriteLine("警告：资源程序集没指过去，界面文案会显示成裸键（不影响渲染判定）");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch (Exception) { /* 管道环境下不允许改编码 */ }

        var mode = args.Length > 0 ? args[0].TrimStart('-') : "shots";
        // stats 的第二个参数是数量，shots 的是目录 + 数量
        var arg1 = args.Length > 1 ? args[1] : "probe-out";
        var arg2 = args.Length > 2 && int.TryParse(args[2], out var n) ? n
            : mode == "stats" && int.TryParse(arg1, out var m) ? m : 6;

        UseAppAssemblyAsResourceAssembly();
        var app = Application.Current ?? new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // 真程序的 UI 线程上有 WPF 装的 DispatcherSynchronizationContext，await 的续体才会回到 UI 线程。
        // 探针这条线程只跑过 PushFrame 没有 Run，不显式装一份的话 VM 里那些 await 的续体会落到线程池，
        // 于是 VM 的日志缓冲被两个线程同时改（FlushLog 枚举 vs AppendLog 追加）。
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        foreach (var relative in new[] { "Themes/Controls.xaml", "Themes/Components.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/{relative}"),
            });
        ThemeManager.Apply(ThemeManager.Boutique);
        L10n.Apply(L10n.Zh);

        try
        {
            switch (mode)
            {
                case "dump": Dump(arg1); break;
                case "calib": Calib(arg1); break;
                case "stats": Stats("probe-out", arg2); break;
                case "pose": Pose(arg1, arg2); break;
                case "shots": Shots(arg1, arg2, args.Skip(3).ToArray()); break;
                default: Console.WriteLine($"未知模式 {mode}（可用：calib / stats / pose / shots）"); return 1;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"探针异常：{ex}");
            return 1;
        }
    }

    // ── 通用：消息泵、渲染、像素读数 ──

    /// <summary>泵消息直到条件成立或超时。被测的预览是 async void + Task.Run，续体排在 UI 线程的
    /// Dispatcher 上，不泵就永远等不到。</summary>
    private static bool PumpUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            if (condition() || sw.ElapsedMilliseconds > timeoutMs)
            {
                frame.Continue = false;
                timer.Stop();
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        return condition();
    }

    private static void PumpRender() =>
        Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.Loaded);

    private sealed record Frame(int W, int H, byte[] Px)
    {
        public int Stride => W * 4;

        public (byte B, byte G, byte R, byte A) At(int x, int y)
        {
            var i = y * Stride + x * 4;
            return (Px[i], Px[i + 1], Px[i + 2], Px[i + 3]);
        }
    }

    private static Frame Render(Visual visual, int w, int h)
    {
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var px = new byte[w * 4 * h];
        rtb.CopyPixels(px, w * 4, 0);
        return new Frame(w, h, px);
    }

    private static void SavePng(Frame f, string path)
    {
        var bmp = BitmapSource.Create(f.W, f.H, 96, 96, PixelFormats.Pbgra32, null, f.Px, f.Stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>一帧的全部读数。掩膜用 alpha：Viewport3D 自身没有底色，没画到东西的地方 alpha 为 0，
    /// 所以"有没有画上"不依赖主题色，也不受离屏 PNG 色偏影响。</summary>
    private static string Report(Frame f)
    {
        var mask = new bool[f.H, f.W];
        var covered = 0;
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        var lum = new double[f.H, f.W];
        var distinct = new HashSet<int>();
        var dark = 0;
        double hueTop = 0, hueBottom = 0;
        var topCount = 0;
        var bottomCount = 0;
        var speckle = 0;
        var specklePairs = 0;

        for (var y = 0; y < f.H; y++)
            for (var x = 0; x < f.W; x++)
            {
                var (b, g, r, a) = f.At(x, y);
                if (a < 24)
                    continue;
                mask[y, x] = true;
                covered++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                var l = 0.114 * b + 0.587 * g + 0.299 * r;
                lum[y, x] = l;
                if (l < 24) dark++;
                distinct.Add(((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3));
                if (y <= minY + (maxY - minY) * 0.2) { hueTop += r - b; topCount++; }
                if (y >= minY + (maxY - minY) * 0.8) { hueBottom += r - b; bottomCount++; }
            }

        if (covered == 0)
            return "覆盖 0% —— 什么都没画出来";

        for (var y = minY; y <= maxY; y++)
            for (var x = minX; x < maxX; x++)
                if (mask[y, x] && mask[y, x + 1])
                {
                    specklePairs++;
                    if (Math.Abs(lum[y, x] - lum[y, x + 1]) > 60) speckle++;
                }

        var bw = maxX - minX + 1;
        var bh = maxY - minY + 1;
        var bands = new StringBuilder();
        for (var k = 0; k < 5; k++)
        {
            // 从上到下的轮廓宽度（占包围盒宽的比例）：人形体应是"头窄 → 肩宽 → 腰 → 胯 → 脚窄"
            var y0 = minY + bh * k / 5;
            var y1 = minY + bh * (k + 1) / 5;
            var widest = 0;
            for (var y = y0; y < y1 && y < f.H; y++)
            {
                var row = 0;
                for (var x = minX; x <= maxX; x++)
                    if (mask[y, x]) row++;
                widest = Math.Max(widest, row);
            }
            bands.Append((100.0 * widest / bw).ToString("0 "));
        }

        return $"覆盖 {100.0 * covered / (f.W * f.H):0.0}%  " +
               $"包围盒 {bw}x{bh} (H/W={bh / (double)bw:0.00})  " +
               $"竖带宽度[顶→底] {bands}%  " +
               $"色数 {distinct.Count}  暗面 {100.0 * dark / covered:0.0}%  " +
               $"相邻突变 {100.0 * speckle / Math.Max(specklePairs, 1):0.0}%  " +
               $"R-B[上 {hueTop / Math.Max(topCount, 1):+0;-0} 下 {hueBottom / Math.Max(bottomCount, 1):+0;-0}]";
    }

    // ── 反射：生产代码里的私有的东西 ──

    private static readonly Type PageType = typeof(OutputConflictPage);

    private static object? PageGet(OutputConflictPage page, string name) =>
        PageType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page);

    private static void PageSet(OutputConflictPage page, string name, object value) =>
        PageType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, value);

    private static void PageCall(OutputConflictPage page, string name) =>
        PageType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);

    /// <summary>调生产渲染路径里那个"一个形状 → 几何 + 材质"的私有静态方法。
    /// 刻意不抄一份到探针里：抄一遍就测不到真代码。</summary>
    private static void AddShapeViaProduction(Model3DGroup group, NifShape shape, ImageSource? texture) =>
        PageType.GetMethod("AddShape", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { group, shape, texture });

    private static T Named<T>(FrameworkElement root, string name) where T : class =>
        (T)root.FindName(name)!;

    // ── 模式 1：合成网格校准 ──

    private static NifShape GlyphShape()
    {
        // 「L」形：竖条 x∈[0,10] z∈[0,10]，底座 x∈[0,20] z∈[0,3]，全部贴在 y=0 平面上。
        // 顶点写的是 nif 的 Z-up 坐标，经生产代码的轴映射后应该变成"竖条朝上、底座朝下"。
        var verts = new List<Vector3>
        {
            new(0, 0, 0), new(20, 0, 0), new(20, 0, 3), new(0, 0, 3),      // 底座
            new(0, 0, 3), new(10, 0, 3), new(10, 0, 10), new(0, 0, 10),    // 竖条
        };
        var indices = new List<int>();
        foreach (var base0 in new[] { 0, 4 })
        {
            // 绕序按"从 -Y 方向看是逆时针"给（nif 里 -Y 是朝向观察者的一侧）
            indices.AddRange([base0, base0 + 2, base0 + 1, base0, base0 + 3, base0 + 2]);
        }
        return new NifShape
        {
            Name = "glyph",
            Positions = verts,
            Normals = Enumerable.Repeat(new Vector3(0, -1, 0), verts.Count).ToList(),
            Uvs = Array.Empty<Vector2>(),
            Indices = indices,
            TexturePath = null,
            BoundsMin = new Vector3(0, 0, 0),
            BoundsMax = new Vector3(20, 0, 10),
        };
    }

    private static NifShape UvQuadShape()
    {
        // 一个面：x∈[0,10]（横向）、z∈[0,10]（nif 的"上"），UV 给成 v=0 在下、v=1 在上。
        var verts = new List<Vector3>
        {
            new(0, 0, 0), new(10, 0, 0), new(10, 0, 10), new(0, 0, 10),
        };
        return new NifShape
        {
            Name = "uvquad",
            Positions = verts,
            Normals = Enumerable.Repeat(new Vector3(0, -1, 0), 4).ToList(),
            Uvs = [new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1)],
            Indices = [0, 2, 1, 0, 3, 2],
            TexturePath = null,
            BoundsMin = new Vector3(0, 0, 0),
            BoundsMax = new Vector3(10, 0, 10),
        };
    }

    /// <summary>上半红、下半蓝的位图（行 0 = 内存里的第一行 = 位图顶部）。</summary>
    private static BitmapSource HalfHalfBitmap()
    {
        const int size = 64;
        var px = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var i = (y * size + x) * 4;
                var top = y < size / 2;
                px[i] = top ? (byte)0 : (byte)255;      // B
                px[i + 1] = 0;                          // G
                px[i + 2] = top ? (byte)255 : (byte)0;  // R
                px[i + 3] = 255;
            }
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4);
        bmp.Freeze();
        return bmp;
    }

    private static void Calib(string outDir)
    {
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"渲染能力等级 RenderCapability.Tier = {RenderCapability.Tier >> 16}" +
                          "（0 = 软件光栅化，1/2 = 硬件；只影响快慢，不影响像素结论）");

        var shape = GlyphShape();
        var uvQuad = UvQuadShape();

        foreach (var (label, textured) in new[]
                 {
                     ("glyph", false), ("uvfront", true), ("uvback", true),
                     ("noback-ahead", false), ("noback-behind", false),
                 })
        {
            var group = new Model3DGroup();
            if (!label.StartsWith("noback", StringComparison.Ordinal))
                AddShapeViaProduction(group, textured ? uvQuad : shape, textured ? HalfHalfBitmap() : null);

            // 只给满额环境光：材质色 = 贴图色，明暗不参与，判 UV 时没有干扰变量
            var lights = new Model3DGroup { Children = { new AmbientLight(Colors.White) } };
            var camera = new PerspectiveCamera
            {
                Position = new Point3D(10, 5, 40),
                LookDirection = new Vector3D(0, 0, -40),
                UpDirection = new Vector3D(0, 1, 0),
                FieldOfView = 20,
            };
            if (label == "uvback")
            {
                camera.Position = new Point3D(10, 5, -40);
                camera.LookDirection = new Vector3D(0, 0, 40);
            }

            var viewport = new Viewport3D { Camera = camera, Width = RenderWidth, Height = RenderHeight };
            viewport.Children.Add(new ModelVisual3D { Content = lights });
            if (label.StartsWith("noback", StringComparison.Ordinal))
            {
                // 生产路径永远给 BackMaterial；这里单独试"不设背面材质"，用来判
                // 那条注释里"没开双面就会画成纯黑"的断言到底是"纯黑"还是"被剔掉"。
                // 顶点直接写 WPF 坐标（XY 平面、朝 +Z 那一面按绕序 0-2-1 是正面）。
                group.Children.Add(new GeometryModel3D(new MeshGeometry3D
                {
                    Positions = new Point3DCollection([new Point3D(0, 0, 0), new Point3D(10, 0, 0),
                        new Point3D(10, 10, 0), new Point3D(0, 10, 0)]),
                    Normals = new Vector3DCollection(Enumerable.Repeat(new Vector3D(0, 0, 1), 4)),
                    TriangleIndices = new Int32Collection([0, 2, 1, 0, 3, 2]),
                }, new DiffuseMaterial(Brushes.LimeGreen)));   // 刻意不设 BackMaterial
                camera.Position = label == "noback-behind" ? new Point3D(5, 5, -40) : new Point3D(5, 5, 40);
                camera.LookDirection = label == "noback-behind" ? new Vector3D(0, 0, 40) : new Vector3D(0, 0, -40);
            }
            viewport.Children.Add(new ModelVisual3D { Content = group });

            var host = new Window
            {
                Content = viewport,
                Width = RenderWidth,
                Height = RenderHeight,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowActivated = false,
                Opacity = 0,
            };
            host.Show();
            PumpUntil(() => viewport.IsArrangeValid, 5000);
            viewport.UpdateLayout();
            PumpRender();

            var frame = Render(viewport, RenderWidth, RenderHeight);
            var png = Path.Combine(outDir, $"calib_{label}.png");
            SavePng(frame, png);
            Console.WriteLine($"\n[{label}] {png}");
            Console.WriteLine(Report(frame));
            if (textured)
                Console.WriteLine("   （位图：上半行红 / 下半行蓝。看屏幕上哪一端偏红，即知 V=0 落在位图的哪一行）");
            host.Close();
        }
    }

    // ── 模式 0：单个 nif 的节点树（判断"变换到底挂在哪一层"）──

    private static void Dump(string path)
    {
        var nif = new NifFile();
        if (nif.Load(path) != 0)
        {
            Console.WriteLine($"加载失败：{path}");
            return;
        }
        Console.WriteLine($"{Path.GetFileName(path)}");
        var roots = nif.GetRootNodes().ToList();
        Console.WriteLine($"块 {nif.Blocks.Count} 个，根节点 {roots.Count} 个：" +
                          string.Join(" / ", roots.Select(r => r.GetType().Name + "\"" + Name(r) + "\"")));

        foreach (var block in nif.Blocks)
        {
            if (block is not NiAVObject avo) continue;
            var chain = new List<string>();
            object? cur = block;
            for (var hop = 0; hop < 10 && cur is not null; hop++)
            {
                cur = nif.GetParentNode((INiObject)cur);
                if (cur is not null) chain.Add($"{cur.GetType().Name}\"{Name(cur)}\" {TransformLine(cur)}");
            }
            Console.WriteLine($"\n  {avo.GetType().Name} \"{Name(avo)}\"  {TransformLine(avo)}");
            Console.WriteLine($"    父链 {chain.Count} 级：{(chain.Count > 0 ? string.Join(" ← ", chain) : "（GetParentNode 取不到父节点）")}");
            var b = RawBounds(block);
            if (b is not null)
                Console.WriteLine($"    顶点包围盒 X[{b.Value.min.X:0.#}..{b.Value.max.X:0.#}] " +
                                  $"Y[{b.Value.min.Y:0.#}..{b.Value.max.Y:0.#}] Z[{b.Value.min.Z:0.#}..{b.Value.max.Z:0.#}] " +
                                  $"（{b.Value.count} 顶点）");
            if (block is INiShape sh)
            {
                var bones = nif.GetShapeBoneNames(sh);
                Console.WriteLine($"    蒙皮骨骼 {bones?.Count ?? 0} 根：{(bones is null ? "" : string.Join(", ", bones.Take(8)))}");
                var rot = block.GetType().GetProperty("Rotation")?.GetValue(block);
                if (b is not null && !IsIdentity("Rotation", rot))
                {
                    // 把两种轴序都算一遍：到底是"施加形状自身变换就能归位"还是"得靠骨骼"，
                    // 用这个文件自己的顶点说话，不靠我推
                    var center = (b.Value.min + b.Value.max) * 0.5f;
                    Console.WriteLine($"    施加自身旋转后中心落在（M·v）{Show(ApplyMatrix(rot, center, false))} " +
                                      $"／（Mᵀ·v）{Show(ApplyMatrix(rot, center, true))}，" +
                                      $"而同一件里位置正确的形状中心在 {CorrectCenter(nif, block)}");
                }
            }
        }
    }

    private static string Name(object block)
    {
        if (block is not NiObjectNET named) return "";
        try { return named.Name.String ?? ""; }
        catch (Exception) { return ""; }
    }

    private static string TransformLine(object block)
    {
        var parts = new List<string>();
        foreach (var prop in new[] { "Translation", "Rotation", "Scale" })
        {
            var value = block.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance)?.GetValue(block);
            if (value is null) continue;
            var tag = IsIdentity(prop, value) ? "" : "!";
            parts.Add(prop switch
            {
                "Translation" => $"T{tag}={Describe(value)}",
                "Rotation" => $"R{tag}={RotationAngle(value):0.#}°",
                _ => $"S{tag}={Describe(value)}",
            });
        }
        return string.Join(" ", parts);
    }

    private static float RotationAngle(object? rotation)
    {
        if (rotation is null || float.IsNaN(Num(rotation, "M11"))) return 0;
        var trace = Num(rotation, "M11") + Num(rotation, "M22") + Num(rotation, "M33");
        return (float)(Math.Acos(Math.Clamp((trace - 1) / 2, -1, 1)) * 180 / Math.PI);
    }

    private static (Vector3 min, Vector3 max, int count)? RawBounds(object block)
    {
        List<Vector3>? verts = block switch
        {
            BSTriShape tri => tri.VertexPositions,
            NiGeometry legacy => legacy.GeometryData?.Vertices,
            _ => null,
        };
        if (verts is not { Count: > 0 }) return null;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in verts) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        return (min, max, verts.Count);
    }

    /// <summary>按 nif 的两种可能轴序把点乘一遍（M·v 与 Mᵀ·v）。</summary>
    private static Vector3 ApplyMatrix(object rotation, Vector3 v, bool transpose)
    {
        float M(int row, int col)
        {
            var names = new[] { "11", "12", "13", "21", "22", "23", "31", "32", "33" };
            if (transpose) (row, col) = (col, row);
            return Num(rotation, "M" + names[row * 3 + col]);
        }
        float Row(int i) => M(i, 0) * v.X + M(i, 1) * v.Y + M(i, 2) * v.Z;
        return new Vector3(Row(0), Row(1), Row(2));
    }

    private static string Show(Vector3 v) => $"({v.X:0.#}, {v.Y:0.#}, {v.Z:0.#})";

    /// <summary>同一文件里那些"自身旋转是恒等"的形状中心——它们的位置是对的，拿来当参照。</summary>
    private static string CorrectCenter(NifFile nif, object self)
    {
        var centers = nif.GetShapes()
            .Where(s => !ReferenceEquals(s, self))
            .Select(s => (shape: s, rot: s.GetType().GetProperty("Rotation")?.GetValue(s), bounds: RawBounds(s)))
            .Where(t => t.rot is not null && IsIdentity("Rotation", t.rot) && t.bounds is not null)
            .Select(t => (t.bounds.Value.min + t.bounds.Value.max) * 0.5f)
            .ToList();
        return centers.Count == 0
            ? "（没有可参照的形状）"
            : Show(centers.Aggregate((a, b) => a + b) / centers.Count) + $" 等 {centers.Count} 个";
    }

    // ── 模式 2：真实 .nif 几何体检 ──

    private static float Num(object? v, string field)
    {
        if (v is null) return float.NaN;
        var t = v.GetType();
        var f = t.GetField(field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        if (f is not null) return Convert.ToSingle(f.GetValue(v)!);
        var p = t.GetProperty(field, BindingFlags.Public | BindingFlags.Instance);
        return p is not null ? Convert.ToSingle(p.GetValue(v)!) : float.NaN;
    }

    private static string Describe(object? v)
    {
        if (v is null) return "null";
        var t = v.GetType();
        var parts = new List<string>();
        foreach (var name in new[] { "X", "Y", "Z", "W", "M11", "M12", "M13", "M21", "M22", "M23", "M31", "M32", "M33" })
        {
            var n = Num(v, name);
            if (!float.IsNaN(n)) parts.Add($"{name}={n:0.###}");
        }
        return $"{t.Name}({string.Join(",", parts)})";
    }

    private static bool IsIdentity(string prop, object? v)
    {
        if (v is null) return true;
        float N(string f, float missing) => float.IsNaN(Num(v, f)) ? missing : Num(v, f);
        // 容差是必需的：nif 里的旋转常是四元数往返后的 0.9999999，按 ==1 比会把纯恒等也报成异常
        const float eps = 1e-3f;
        bool Near(float a, float b) => Math.Abs(a - b) < eps;
        switch (prop)
        {
            case "Translation":
                return Near(N("X", 0), 0) && Near(N("Y", 0), 0) && Near(N("Z", 0), 0);
            case "Rotation":
                if (!float.IsNaN(Num(v, "M11")))
                    return Near(N("M11", 0), 1) && Near(N("M22", 0), 1) && Near(N("M33", 0), 1) &&
                           Near(N("M12", 1), 0) && Near(N("M13", 1), 0) && Near(N("M21", 1), 0) &&
                           Near(N("M23", 1), 0) && Near(N("M31", 1), 0) && Near(N("M32", 1), 0);
                return Near(N("X", 0), 0) && Near(N("Y", 0), 0) && Near(N("Z", 0), 0);
            case "Scale":
                return Near(N("X", 1), 1) && Near(N("Y", 1), 1) && Near(N("Z", 1), 1) && Near(N("W", 1), 1);
            default:
                return true;
        }
    }

    /// <summary>形状自己的 + 各级祖先节点的变换是否都是恒等。</summary>
    private static (bool identity, string detail) WorldTransform(NifFile nif, object shape)
    {
        var detail = new StringBuilder();
        var identity = true;
        object? node = shape;
        for (var hop = 0; node is not null && hop < 12; hop++)
        {
            var label = node.GetType().Name;
            foreach (var prop in new[] { "Translation", "Rotation", "Scale" })
            {
                var value = node.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance)?.GetValue(node);
                if (value is null) continue;
                if (!IsIdentity(prop, value))
                {
                    identity = false;
                    detail.Append($"{label}.{prop}={Describe(value)}");
                    // 光看矩阵读不出严重程度，补一个"转了多少度 / 挪了多少厘米"
                    if (prop == "Rotation" && !float.IsNaN(Num(value, "M11")))
                    {
                        var trace = Num(value, "M11") + Num(value, "M22") + Num(value, "M33");
                        var angle = Math.Acos(Math.Clamp((trace - 1) / 2, -1, 1)) * 180 / Math.PI;
                        detail.Append($"→转 {angle:0.#}° ");
                    }
                    else if (prop == "Translation")
                        detail.Append($"→挪 {new Vector3(Num(value, "X"), Num(value, "Y"), Num(value, "Z")).Length():0.#} 单位 ");
                    else
                        detail.Append(' ');
                }
            }
            node = nif.GetParentNode((INiObject)node);
        }
        return (identity, detail.ToString().Trim());
    }

    private static float Percent(List<float> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];

    private static string ShapeName(INiShape shape) =>
        shape is NiObjectNET { Name.String: { Length: > 0 } } named ? named.Name.String : shape.GetType().Name;

    /// <summary>两个轴对齐包围盒之间的最短距离（有重叠则为 0）。</summary>
    private static float GapBetween((Vector3 Min, Vector3 Max) a, (Vector3 Min, Vector3 Max) b)
    {
        static float Axis(float aMin, float aMax, float bMin, float bMax) =>
            Math.Max(0, Math.Max(aMin - bMax, bMin - aMax));
        var dx = Axis(a.Min.X, a.Max.X, b.Min.X, b.Max.X);
        var dy = Axis(a.Min.Y, a.Max.Y, b.Min.Y, b.Max.Y);
        var dz = Axis(a.Min.Z, a.Max.Z, b.Min.Z, b.Max.Z);
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static void Stats(string outDir, int limit)
    {
        Directory.CreateDirectory(outDir);
        var vm = NewScannedVm();
        var outfits = vm.Scan!.Outfits;
        var paths = outfits.Where(o => o.SourceNif is { Length: > 0 } && File.Exists(o.SourceNif))
            .Select(o => o.SourceNif!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"扫描到 {outfits.Count} 件服装，其中源网格存在于磁盘的有 {paths.Count} 个不同文件；本次体检 {Math.Min(limit, paths.Count)} 个。");

        // 取有代表性的一批：按模组分散，避免整批都是同一个作者的导出习惯
        var sample = paths.GroupBy(p => Path.GetDirectoryName(p) ?? "", StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Take(2))
            .Take(limit)
            .ToList();

        int files = 0, shapes = 0, nonIdentity = 0, uvMismatch = 0, normalMismatch = 0, badIndex = 0;
        int slot0EmptyButOther = 0, effectShader = 0, noShader = 0, doubleSided = 0, legacyGeom = 0;
        var offenders = new List<string>();
        var bounds = new List<(string File, string Shape, Vector3 Min, Vector3 Max)>();
        var swAll = Stopwatch.StartNew();

        foreach (var path in sample)
        {
            var nif = new NifFile();
            try
            {
                if (nif.Load(path) != 0) { offenders.Add($"加载失败 {path}"); continue; }
            }
            catch (Exception ex) { offenders.Add($"加载抛异常 {Path.GetFileName(path)}: {ex.GetType().Name}"); continue; }
            files++;

            foreach (var shape in nif.GetShapes())
            {
                shapes++;
                var (identity, detail) = WorldTransform(nif, shape);
                if (!identity)
                {
                    nonIdentity++;
                    if (offenders.Count < 40) offenders.Add($"非恒等变换 {Path.GetFileName(path)} :: {detail}");
                }

                List<Vector3>? verts = null;
                List<Vector3>? normals = null;
                int uvCount = 0;
                switch (shape)
                {
                    case BSTriShape tri:
                        verts = tri.VertexPositions;
                        normals = tri.Normals;
                        uvCount = tri.UVs?.Count ?? 0;
                        break;
                    case NiGeometry legacy:
                        legacyGeom++;
                        verts = legacy.GeometryData?.Vertices;
                        normals = legacy.GeometryData?.Normals;
                        uvCount = legacy.GeometryData?.UVSets?.Count ?? 0;
                        break;
                }
                var tris = shape.Triangles;
                if (verts is { Count: > 0 })
                {
                    if (uvCount != verts.Count) uvMismatch++;
                    if (normals is { Count: > 0 } && normals.Count != verts.Count) normalMismatch++;
                    if (tris is { Count: > 0 } && tris.Max(t => Math.Max(t.V1, Math.Max(t.V2, t.V3))) >= verts.Count)
                    {
                        badIndex++;
                        offenders.Add($"越界索引 {Path.GetFileName(path)}");
                    }
                }
                if (tris is { Count: > 0 } && verts is { Count: > 0 } && normals is { Count: > 0 } && uvCount == verts.Count)
                {
                    foreach (var v in verts)
                        if (float.IsNaN(v.X) || float.IsInfinity(v.X))
                        {
                            offenders.Add($"顶点含 NaN/Inf {Path.GetFileName(path)}");
                            break;
                        }
                    var min = new Vector3(float.MaxValue);
                    var max = new Vector3(float.MinValue);
                    foreach (var v in verts) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
                    bounds.Add((Path.GetFileName(path), ShapeName(shape), min, max));
                }

                INiShader? shader;
                try { shader = nif.GetShader(shape); }
                catch (Exception) { shader = null; }
                if (shader is null) { noShader++; continue; }
                if (shader is BSEffectShaderProperty) effectShader++;
                if (shader is BSShaderProperty { DoubleSided: true }) doubleSided++;

                if (shader.HasTextureSet && nif.GetBlock<BSShaderTextureSet>(shader.TextureSetRef) is { Textures: { Count: > 0 } list })
                {
                    var first = list[0]?.Content;
                    var rest = list.Skip(1).Any(t => !string.IsNullOrWhiteSpace(t?.Content));
                    if (string.IsNullOrWhiteSpace(first) && rest) slot0EmptyButOther++;
                }
            }
            if (files >= sample.Count) break;
        }

        Console.WriteLine($"\n── 真实 .nif 几何体检：{files} 个文件 / {shapes} 个形状（耗时 {swAll.ElapsedMilliseconds} ms）──");
        static void Row(string name, int count, int total) =>
            Console.WriteLine($"  {name,-34} {count,6}  ({100.0 * count / Math.Max(total, 1):0.00}% of {total})");
        Row("世界变换非恒等（渲染端不施加）", nonIdentity, shapes);
        Row("UV 数与顶点数不符 → 退回纯色", uvMismatch, shapes);
        Row("法线数与顶点数不符 → 自算平法线", normalMismatch, shapes);
        Row("三角形索引越界", badIndex, shapes);
        Row("老式 NiGeometry（走 GeometryData）", legacyGeom, shapes);
        Row("无着色器（被丢弃）", noShader, shapes);
        Row("BSEffectShaderProperty", effectShader, shapes);
        Row("DoubleSided 打开", doubleSided, shapes);
        Row("槽 0 空但其余槽有贴图", slot0EmptyButOther, shapes);

        // 不施加变换的失效模式不是"某个零件躺倒"，而是"零件各待各处"：一件衣服的几个形状本该互相
        // 贴着（衣身/袖子/腰带），如果某个形状离其它所有形状都隔着一大段空距离，它就是被骨骼或节点
        // 摆到别处去的，静态读顶点只会把它画在原地（本机实例：项链的 4 块落在脚边）。
        var scattered = new List<string>();
        foreach (var file in bounds.GroupBy(b => b.File))
        {
            var list = file.ToList();
            if (list.Count < 2) continue;
            foreach (var a in list)
            {
                var nearest = list.Where(b => b != a)
                    .Min(b => GapBetween((a.Min, a.Max), (b.Min, b.Max)));
                if (nearest > 20)
                    scattered.Add($"{file.Key} :: {a.Shape} 离最近的形状还有 {nearest:0} 单位");
            }
        }
        if (scattered.Count > 0)
        {
            Console.WriteLine($"\n  零件散落（顶点不在它该在的地方）：{scattered.Count} 个形状，涉及 " +
                              $"{scattered.Select(s => s.Split(" :: ")[0]).Distinct().Count()} 个文件");
            foreach (var line in scattered.Take(12)) Console.WriteLine("    " + line);
        }
        if (offenders.Count > 0)
        {
            Console.WriteLine("\n  明细（最多 40 条）：");
            foreach (var line in offenders.Take(40)) Console.WriteLine("    " + line);
        }

        // 第二遍走生产加载器：这才有"界面到底会画成什么样"的口径
        Console.WriteLine($"\n── 同一批文件走 NifPreviewLoader（生产读取路径）──");
        int loaded = 0, failed = 0, truncated = 0, textured = 0, plain = 0, ownBody = 0, smallBody = 0, tallNoBody = 0;
        var notes = new List<string>();
        var bodyHeights = new List<(string File, float BodyHeight, float Height)>();
        var detached = new List<string>();
        foreach (var path in sample)
        {
            if (!NifPreviewLoader.TryLoad(path, out var model, out var error) || model is null)
            {
                failed++;
                notes.Add($"读取失败 {Path.GetFileName(path)}：{error}");
                continue;
            }
            loaded++;
            if (model.SkippedShapeCount > 0)
            {
                truncated++;
                notes.Add($"超 {NifPreviewLoader.TriangleBudget} 三角形预算被丢 {model.SkippedShapeCount} 个形状：{Path.GetFileName(path)}");
            }
            textured += model.Shapes.Count(s => s.IsTextured);
            plain += model.Shapes.Count(s => !s.IsTextured);

            // 只算**真会被画出来**的形状：VirtualGround 一类辅助网格上面那道无着色器的统计已经
            // 把它们丢掉了，混进来只会把"零件散落"的比例灌水
            if (model.Shapes.Count > 1)
                foreach (var a in model.Shapes)
                {
                    var nearest = model.Shapes.Where(a2 => a2 != a)
                        .Min(a2 => GapBetween((a.BoundsMin, a.BoundsMax), (a2.BoundsMin, a2.BoundsMax)));
                    if (nearest > 20)
                        detached.Add($"{Path.GetFileName(path)} :: {a.Name} 离最近形状 {nearest:0} 单位");
                }

            var height = model.BoundsMax.Z - model.BoundsMin.Z;
            var bodyShapes = model.Shapes.Where(s => s.TexturePath is { } t &&
                (t.Contains("femalebody", StringComparison.OrdinalIgnoreCase) ||
                 t.Contains("malebody", StringComparison.OrdinalIgnoreCase))).ToList();
            if (bodyShapes.Count > 0)
            {
                ownBody++;
                // 「自带身体」为真就不再垫身体。可手套/靴子/面具这类只带一小块皮肤网格的件也会命中，
                // 于是预览里是一枚悬空的零件。用"那块皮肤有多高"把两种情况分开。
                var bodyHeight = bodyShapes.Max(s => s.BoundsMax.Z - s.BoundsMin.Z);
                bodyHeights.Add((Path.GetFileName(path), bodyHeight, height));
                if (bodyHeight < height * 0.5f)
                {
                    smallBody++;
                    notes.Add($"CarriesOwnBody 命中但皮肤块只有 {bodyHeight:0} 高（整件 {height:0}）→ 不垫身体：{Path.GetFileName(path)}");
                }
            }
            else if (height > 60) tallNoBody++;
        }
        Console.WriteLine($"  读成 {loaded} 件 / 失败 {failed} 件；形状：贴图 {textured} 个、纯色回退 {plain} 个；" +
                          $"被预算截断的 {truncated} 件");
        Console.WriteLine($"  自带身体（不垫）{ownBody} 件，其中皮肤块高度不到整件一半的 {smallBody} 件；" +
                          $"自身高于 60 单位但不带皮肤的 {tallNoBody} 件（这些会垫上女体）");
        // 皮肤块高度的分布决定"多高才算真身体"的阈值该划在哪：两堆之间有空档才好分
        var sorted = bodyHeights.Select(b => b.BodyHeight).OrderBy(h => h).ToList();
        Console.WriteLine($"  「皮肤块」高度分布（nif 单位，共 {sorted.Count} 件）：" +
                          $"最小 {sorted.FirstOrDefault():0} / 10 分位 {Percent(sorted, 0.1):0} / 中位 {Percent(sorted, 0.5):0} / " +
                          $"90 分位 {Percent(sorted, 0.9):0} / 最大 {sorted.Last():0}");
        foreach (var bucket in new[] { (0f, 30f), (30f, 60f), (60f, 90f), (90f, 120f), (120f, 999f) })
            Console.WriteLine($"    {bucket.Item1,3}-{bucket.Item2,3}：{sorted.Count(h => h >= bucket.Item1 && h < bucket.Item2)} 件");
        Console.WriteLine($"  画出来的形状里零件散落的：{detached.Count} 个，涉及 " +
                          $"{detached.Select(d => d.Split(" :: ")[0]).Distinct().Count()} 个文件");
        foreach (var line in detached.Take(10)) Console.WriteLine("    " + line);
        foreach (var line in notes.Take(25)) Console.WriteLine("    " + line);
    }

    // ── 模式 3：真实服装走生产页面出图 ──

    private static MainViewModel NewScannedVm()
    {
        Console.WriteLine("开始按真实设置扫描实例（这一步与程序启动时完全同一条路径）…");
        var vm = new MainViewModel();
        if (!PumpUntil(() => vm.Scan is { Outfits.Count: > 0 } && !vm.IsScanning, 600_000))
            throw new TimeoutException("600 秒内没扫完；IsScanning=" + vm.IsScanning + " Scan=" + (vm.Scan?.Outfits.Count ?? -1));
        Console.WriteLine($"扫描完成：模组 {vm.Mods.Count} / 服装 {vm.Scan!.Outfits.Count} / 冲突组 {vm.ConflictGroups.Count}");
        var withOut = vm.Scan.Outfits.Count(o => o.OutputFilePath is { Length: > 0 });
        var byPath = vm.Scan.Outfits.Where(o => o.OutputFilePath is { Length: > 0 })
            .GroupBy(o => o.OutputFilePath!, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"   声明了输出路径的服装 {withOut} 件，去重后 {byPath.Count} 个路径，" +
                          $"其中被 ≥2 件共用的 {byPath.Count(g => g.Count() > 1)} 个");
        return vm;
    }

    /// <summary>探针用的冲突清单：一件真实服装一组。冲突页的预览只认 <c>SourceNifOf</c> 与 <c>Assets</c>，
    /// 有没有真的冲突与渲染无关，所以当前 Profile 恰好零冲突时也能照同一条生产路径出图。</summary>
    private static ConflictRequest BuildProbeRequest(MainViewModel vm, IReadOnlyList<OutfitEntry> picks)
    {
        var roots = vm.Mods.Select(m => m.Dir).ToList();
        if (vm.Resolution?.GameDataPath is { Length: > 0 } gameData)
            roots.Add(gameData);
        var byName = picks.GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().SourceNif, StringComparer.OrdinalIgnoreCase);
        return new ConflictRequest
        {
            Groups = picks.Select(o => new OutputConflictGroup
            {
                OutputFilePath = o.OutputFilePath ?? o.SourceNif ?? o.Name,
                KeySpellings = [o.OutputFilePath ?? o.Name],
                GenWeights = o.GenWeights,
                Candidates = [new ConflictCandidate(o.Name, o.OwnerLabel, o.SourceFile, o.LayerIndex, o.GenWeights)],
            }).ToList(),
            Choices = new Dictionary<string, string>(StringComparer.Ordinal),
            IsGrouped = _ => false,
            UserGroups = () => [],
            IsGroupCollapsed = _ => false,
            SetGroupCollapsed = (_, _) => { },
            SetGroupsCollapsed = _ => { },
            SourceNifOf = name => byName.TryGetValue(name, out var p) ? p : null,
            Save = _ => { },
            BuildSelectionPath = () => "",
            Export = () => (false, null),
            Assets = roots.Count > 0 ? new GameDataResolver(roots) : null,
        };
    }

    /// <summary>挑一条"同一个输出文件被两个以上模组争"的真实冲突，把视角摆成非默认姿态，
    /// 然后逐行换候选，把相机摆位与渲染参数原样打出来。
    /// <para>
    /// 这一页的用途是对比同一个 nif 换个模组长什么样，所以行与行之间相机必须一动不动：
    /// 每换一行重新取景会让中心与距离跟着新包围盒变，数字会跳、图会跟着缩放，两件的差别正好被
    /// 这点跳动盖掉。摆位逐行相同 = 锁住了；相同件之间该有的重新取景（换身体、换冲突）由 shots 模式验。
    /// </para></summary>
    private static void Pose(string outDir, int limit)
    {
        Directory.CreateDirectory(outDir);
        var vm = NewScannedVm();
        var request = vm.CurrentConflict ?? throw new InvalidOperationException("没扫出冲突清单");
        static bool RealSource(ConflictRequest r, ConflictCandidate c) =>
            r.SourceNifOf(c.Name) is { } p && File.Exists(p);
        // 「自动」垫身体时，一件自带身体的会不垫、另一件没带的会补一具——"实际垫了哪具"跟着行走。
        // 这种组才是这道锁真正要过的关：拿它当取景身份的话，换行就会重新取景、右键平移被抹掉。
        static bool CarriesOwnBody(ConflictRequest r, ConflictCandidate c) =>
            r.SourceNifOf(c.Name) is { } p && NifPreviewLoader.TryLoad(p, out var m, out _) && m?.CarriesOwnBody == true;
        // 必须是跨模组组：左栏默认勾着「只看跨模组」，同模组内部的组根本不在列表里。
        // 只在头 40 条里挑（判"自带身体"要真解一次 nif，扫全部会白等几分钟）
        var multi = request.Groups.Where(g => g.CrossMod && g.Candidates.Count(c => RealSource(request, c)) > 1)
            .Take(40).ToList();
        static int RealCount(ConflictRequest r, OutputConflictGroup g) => g.Candidates.Count(c => RealSource(r, c));
        static int OwnCount(ConflictRequest r, OutputConflictGroup g) => g.Candidates.Count(c => CarriesOwnBody(r, c));
        var group = multi.FirstOrDefault(g => OwnCount(request, g) > 0 && OwnCount(request, g) < RealCount(request, g))
            ?? multi.FirstOrDefault()
            ?? throw new InvalidOperationException("当前 Profile 里没有一条候选不止一个真实来源的冲突");
        Console.WriteLine($"比这一条：{group.OutputFilePath}（{group.Candidates.Count} 个来源，" +
                          $"其中源网格在盘的 {RealCount(request, group)} 个、" +
                          $"自带身体的 {OwnCount(request, group)} 个）");

        var page = new OutputConflictPage();
        var window = new Window
        {
            Content = page,
            Width = 1400,
            Height = 900,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
            Opacity = 0,
        };
        window.Show();
        PumpUntil(() => page.IsArrangeValid, 5000);
        page.ShowRequest(request);
        PumpRender();
        page.UpdateLayout();

        var groupList = Named<ListBox>(page, "GroupList");
        var candidateList = Named<ListBox>(page, "CandidateList");
        var viewport = Named<Viewport3D>(page, "Preview");
        var meshVisual = Named<ModelVisual3D>(page, "MeshVisual");
        var camera = Named<PerspectiveCamera>(page, "Camera");
        var status = Named<TextBlock>(page, "PreviewStatus");

        // 按输出路径找回左栏那一行（GroupRow 是页内的私有类型，只能按属性认）
        var row = groupList.Items.Cast<object>().FirstOrDefault(o =>
            o.GetType().GetProperty("Group")?.GetValue(o) is { } g &&
            (string?)g.GetType().GetProperty("OutputFilePath")?.GetValue(g) == group.OutputFilePath)
            ?? throw new InvalidOperationException("左栏里找不到那条冲突（过滤器把它藏了？）");
        groupList.SelectedItem = row;
        PumpRender();
        page.UpdateLayout();
        var candidates = candidateList.Items.Cast<object>().ToList();

        // 一个非默认姿态：转过来、拉近一点、再拖开一段——重新取景会把这三样里的距离与平移抹掉
        void StrikeAPose()
        {
            PageSet(page, "_yaw", Math.PI / 2);
            PageSet(page, "_pitch", -0.3);
            PageSet(page, "_zoom", 0.7);
            PageSet(page, "_pan", new Vector3D(6, -4, 0));
            PageCall(page, "UpdateCamera");
        }

        string Readout() =>
            $"机位({camera.Position.X:0.##},{camera.Position.Y:0.##},{camera.Position.Z:0.##}) " +
            $"朝向({camera.LookDirection.X:0.##},{camera.LookDirection.Y:0.##},{camera.LookDirection.Z:0.##}) " +
            $"距离 {PageGet(page, "_distance"):0.##} 中心({PageGet(page, "_center")}) " +
            $"半盒({PageGet(page, "_half")}) 平移({PageGet(page, "_pan")})";

        var poses = new List<(string Label, string Pose)>();

        // 按给定的行序逐个换候选并读数；tag 只进 PNG 文件名，两遍不互相覆盖
        void Walk(IReadOnlyList<object> order, string tag)
        {
            foreach (var candidate in order)
            {
                if (poses.Count >= limit * 2)
                    return;
                var label = candidate.GetType().GetProperty("Label")?.GetValue(candidate) as string ?? "?";
                if (ReferenceEquals(candidateList.SelectedItem, candidate))
                {
                    // 反着走第二遍时会撞回当前这一行：选中项没变就没有 SelectionChanged，也就没有新渲染，
                    // 干等 120 秒只会等到一句假超时
                    Console.WriteLine($"   [{tag}] {label}：本来就是当前那一行，跳过");
                    continue;
                }
                if (candidate.GetType().GetProperty("SourceNif")?.GetValue(candidate) is not string nif
                    || !File.Exists(nif))
                    continue;
                var before = meshVisual.Content; // Render 每次都新建一个 Model3DGroup，换实例=这一行画完了
                candidateList.SelectedItem = candidate;
                // 必须等"有网格"而不是只等实例变：换行时页内会先清空渲染面（Content 变 null），
                // 只比引用就会在读数前一瞬放行，量到的是空的
                if (!PumpUntil(() => meshVisual.Content is { } now && !ReferenceEquals(now, before), 120_000))
                {
                    Console.WriteLine($"   {label}：120 秒内没出网格（{status.Text}）");
                    continue;
                }
                if (poses.Count == 0)
                {
                    // 姿态只摆一次，而且要在第一行落图之后：首次渲染本来就得取景，摆早了会被那次取景抹掉；
                    // 往后再摆就等于把"这一行的渲染有没有把姿态保住"遮掉。
                    StrikeAPose();
                    PumpRender();
                }
                viewport.UpdateLayout();
                PumpRender();
                var pose = Readout();
                Console.WriteLine($"   [{tag}{poses.Count}] {label}");
                Console.WriteLine($"          {pose}");
                // 网格的世界范围与取景盒放在一起看：0% 覆盖是"东西不在框里"还是"还没画完"，靠这个分
                if (meshVisual.Content is Model3DGroup parts)
                    Console.WriteLine($"          网格 {parts.Children.Count} 个部件，范围 " +
                                      $"X[{parts.Bounds.X:0}..{parts.Bounds.X + parts.Bounds.SizeX:0}] " +
                                      $"Y(高)[{parts.Bounds.Y:0}..{parts.Bounds.Y + parts.Bounds.SizeY:0}] " +
                                      $"Z[{parts.Bounds.Z:0}..{parts.Bounds.Z + parts.Bounds.SizeZ:0}]");
                var png = Path.Combine(outDir,
                    $"pose{tag}{poses.Count:00}_{string.Join("_", label.Split(Path.GetInvalidFileNameChars()))}.png");
                var frame = Render(viewport, RenderWidth, RenderHeight);
                SavePng(frame, png);
                Console.WriteLine($"          {Path.GetFileName(png)}  {Report(frame)}");
                poses.Add((label, pose));
            }
        }

        // 走两遍，第二遍反着来：摆位读数在两遍里都该一模一样。
        // 而"某一行空帧"若跟着行走，是那件网格本身的性质（单面剔背面那一类）；跟着次序走则是探针没等到画完。
        Walk(candidates, "a");
        Walk(candidates.AsEnumerable().Reverse().ToList(), "b");

        if (poses.Count > 0)
        {
            typeof(OutputConflictPage)
                .GetMethod("Reset_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(page, [null, new RoutedEventArgs()]);
            PumpRender();
            Console.WriteLine($"   按「复位视角」：{Readout()}");
        }

        window.Close();
        // 一句话结论，免得靠人眼比对几串数字
        var distinct = poses.Select(p => p.Pose).Distinct().ToList();
        Console.WriteLine(poses.Count > 1 && distinct.Count == 1
            ? $"结论：{poses.Count} 次换来源（正走 + 反走），相机摆位逐次相同 → 视角没动。"
            : $"结论：{poses.Count} 次换来源里有 {distinct.Count} 种摆位 → 换来源时相机动了，逐次读数见上。");
    }

    private static void Shots(string outDir, int limit, string[] picks)
    {
        Directory.CreateDirectory(outDir);
        var vm = NewScannedVm();
        ConflictRequest request;
        var wanted = vm.Scan!.Outfits
            .Where(o => o.SourceNif is { Length: > 0 } && File.Exists(o.SourceNif))
            .ToList();
        if (picks.Length > 0)
        {
            // 指名道姓：按 set 名子串挑，用来复现"某一类"具体文件（手套、高跟鞋…）
            var chosen = picks
                .Select(f => wanted.FirstOrDefault(o => o.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                .Where(o => o is not null)
                .Select(o => o!)
                .ToList();
            Console.WriteLine($"按名字点了 {chosen.Count} 件：{string.Join(" / ", chosen.Select(o => o.Name))}");
            request = BuildProbeRequest(vm, chosen);
        }
        else if (vm.CurrentConflict is { Groups.Count: > 0 } real)
        {
            Console.WriteLine($"用真实冲突清单（{real.Groups.Count} 组）驱动预览。");
            request = real;
        }
        else
        {
            var sample = wanted
                .GroupBy(o => o.OwnerLabel, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Take(limit)
                .ToList();
            Console.WriteLine($"当前 Profile 没有冲突组：改用 {sample.Count} 件真实服装自造清单（同一条生产渲染路径）。");
            request = BuildProbeRequest(vm, sample);
        }

        var page = new OutputConflictPage();
        var window = new Window
        {
            Content = page,
            Width = 1400,
            Height = 900,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
            Opacity = 0,
        };
        window.Show();
        PumpUntil(() => page.IsArrangeValid, 5000);
        page.ShowRequest(request);
        PumpRender();
        page.UpdateLayout();

        var groupList = Named<ListBox>(page, "GroupList");
        var candidateList = Named<ListBox>(page, "CandidateList");
        var bodyChoice = Named<ComboBox>(page, "BodyChoice");
        var viewport = Named<Viewport3D>(page, "Preview");
        var meshVisual = Named<ModelVisual3D>(page, "MeshVisual");
        var status = Named<TextBlock>(page, "PreviewStatus");
        var stats = Named<TextBlock>(page, "MeshStats");

        var groups = groupList.Items.Cast<object>().ToList();
        Console.WriteLine($"左栏 {groups.Count} 行；本次取 {limit} 行（跨列表均匀取样）。");
        var angles = new (string Label, double Yaw, double Pitch)[]
        {
            ("front", Math.PI, 0),
            ("back", 0, 0),
            ("left", Math.PI / 2, 0),
            ("right", -Math.PI / 2, 0),
            ("high", Math.PI, -0.5),
        };

        for (var i = 0; i < limit; i++)
        {
            var at = (int)((long)i * groups.Count / limit);
            groupList.SelectedItem = groups[at];
            PumpRender();
            var candidates = candidateList.Items.Cast<object>().ToList();
            if (candidates.Count == 0) { Console.WriteLine($"#{i} 没有候选行，跳过"); continue; }
            candidateList.SelectedItem = candidates[0];

            var label = candidates[0].GetType().GetProperty("Label")?.GetValue(candidates[0]) as string ?? "?";
            var ok = PumpUntil(() => meshVisual.Content is not null, 120_000);
            Console.WriteLine($"\n#{i} 组行 {at} / 候选 {label} / 候选数 {candidates.Count - 1} → {(ok ? "已出网格" : "超时未出网格：" + status.Text)}");
            if (!ok) continue;
            Console.WriteLine("   统计行：" + stats.Text);
            if (meshVisual.Content is Model3DGroup parts)
            {
                var twoSided = parts.Children.OfType<GeometryModel3D>().Count(m => m.BackMaterial is not null);
                Console.WriteLine($"   其中按 BodySlide 口径两面都画 {twoSided} 个、剔背面 {parts.Children.Count - twoSided} 个");
                for (var p = 0; p < parts.Children.Count; p++)
                    if (parts.Children[p] is GeometryModel3D { Geometry: MeshGeometry3D geo })
                        Console.WriteLine($"   部件 {p} 顶点 {geo.Positions.Count}  " +
                                          $"X[{geo.Bounds.X:0}..{geo.Bounds.X + geo.Bounds.SizeX:0}] " +
                                          $"Y(高)[{geo.Bounds.Y:0}..{geo.Bounds.Y + geo.Bounds.SizeY:0}] " +
                                          $"Z[{geo.Bounds.Z:0}..{geo.Bounds.Z + geo.Bounds.SizeZ:0}]");
            }

            foreach (var (angle, yaw, pitch) in angles)
            {
                PageSet(page, "_yaw", yaw);
                PageSet(page, "_pitch", pitch);
                PageSet(page, "_zoom", 1.0);
                PageCall(page, "UpdateCamera");
                viewport.UpdateLayout();
                PumpRender();
                var frame = Render(viewport, RenderWidth, RenderHeight);
                var png = Path.Combine(outDir, $"shot{i:00}_{angle}.png");
                SavePng(frame, png);
                Console.WriteLine($"   {angle,-6} {Path.GetFileName(png)}");
                Console.WriteLine($"          {Report(frame)}");
            }
        }

        // 同一件衣服：垫身体 vs 不垫，用来量"两层网格重合处"的椒盐像素
        Console.WriteLine("\n── 同一候选：身体下拉逐个取值（Auto / Female / Male / 无）──");
        for (var b = 0; b < 4; b++)
        {
            bodyChoice.SelectedIndex = b;
            PumpUntil(() => true, 1200);
            var ok = PumpUntil(() => meshVisual.Content is not null, 60_000);
            if (!ok) { Console.WriteLine($"   身体取值 {b}：未出网格"); continue; }
            PageSet(page, "_yaw", Math.PI);
            PageSet(page, "_pitch", 0.0);
            PageCall(page, "UpdateCamera");
            PumpRender();
            var frame = Render(viewport, RenderWidth, RenderHeight);
            var png = Path.Combine(outDir, $"body{b}.png");
            SavePng(frame, png);
            Console.WriteLine($"   身体[{b}] {Path.GetFileName(png)}  {stats.Text}");
            Console.WriteLine($"          {Report(frame)}");
        }

        window.Close();
    }
}
