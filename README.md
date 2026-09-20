# BS Group Generator

读取 **Mod Organizer 2 (MO2)** 安装的模组，让用户把服装模组批量划进 **BodySlide 分组（SliderGroups）** 的 Windows 独立小工具。

BodySlide 自带的 Group Manager 只有一个服装平铺列表，不知道哪个服装来自哪个模组；本工具补上这一环：**按模组勾选，一键整组归组**，也能展开后逐个服装微调。

界面为 WPF，自带**暗色 / 亮色**两套主题，支持**中文 / English / Русский / Français** 四种界面语言切换（菜单栏顶层的「界面主题」「语言」即时生效）。

**运行要求**：Windows x64 + [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。发布包为**框架依赖**版（6 个文件、约 0.6 MB），不含运行时，首次使用请先安装上面的运行时。

## 使用方法

1. 启动后自动发现 MO2 实例（全局实例 + 便携安装）。找不到就用「添加 MO2 目录…」选择含 `ModOrganizer.ini` 的实例目录或含 `ModOrganizer.exe` 的便携安装目录。
2. 选择配置（Profile）——工具解析该 profile 的 `modlist.txt`，列出所有**启用**的模组。
3. BodySlide 通常自动检测成功（在启用模组中寻找含 `BodySlide*.exe` 与 `Config.xml` 的目录）。界面会显示「有效项目路径」——即 BodySlide 实际读取服装/分组的位置，与 BodySlide 自己的 `ProjectUtil::GetProjectPath()` 逻辑完全一致。
4. 在右侧**新建组**（面板标题栏的 **「⋯」→ 新建组**，组列表为空时面板中间也有按钮）并选中它，然后在左侧**勾选**模组（或展开勾选单个服装；**勾选分隔符 = 全选其下所有模组**），再点两栏中间搬运栏上的 **「加入组」**——这一步才真正把服装写进组；勾错的用「移出组」撤销。搬运按钮上会显示当前勾选了多少个服装，置灰时把鼠标移上去会说明原因。
   - 已在当前组里的服装在名字前显示**成员标记**（不再是文本前缀），模组标题会附 `[组内 x/总数]`。
   - 模组树遵循 **MO2 左侧栏的顺序**，只显示含 BodySlide 服装的启用模组；你的分隔符显示为灰色分组标题（`_separator` 后缀自动去掉），方便在 MO2 的结构里对照定位。
   - **组管理都在「⋯」里**（也可直接右键组列表）：新建 / 重命名 / 查看组 / 规则归组 / 删除组——低频动作不再占用主面板按钮位。
   - 「规则归组」：按 模组关键字 / 服装关键字 / 排除关键字（分号分隔，不区分大小写）批量把服装加入或移出某个组；模组关键字先筛模组、再取其下服装，不会把其他模组里的同名服装混进来；可限定仅未分配服装，实时预览命中数量并按 分隔符 → 模组 → 命中服装 树形展示。
   - 「新装模组提醒」：装了新服装模组后再打开程序，扫描会自动弹窗，按 分隔符 → 模组 → 服装 分层列出新增内容（与主窗口一致，可展开查看具体服装），勾选并选好目标组即可一键归组；弹窗顶部也有**两个过滤框**（左筛服装名、右筛模组 / 分隔符名），列表长时用它快速定位，**勾选状态不会因过滤丢失**。关闭则暂不处理，之后仍可用「仅看未分配」找到它们。
   - 「撤销」（**编辑 → 撤销** 或 Ctrl+Z）：最近 30 步分组操作可逐步回退，误点不慌。
   - 「查看组」（或双击组名）：预览该组的全部服装，可按名称过滤、可勾选批量移出。
   - 「仅看未分配」：只显示还没进任何组的服装。
   - **两个过滤框各管一类**：「过滤服装名…」只匹配服装名，「过滤模组名…」只匹配模组名（连续子串，命中模组时显示该模组全部服装并标注"匹配 x/总数"）。
   - 菜单 **文件 → 导入组文件…**：把已有分组 XML 合并进来继续编辑。
5. 点击**保存分组文件**（或 Ctrl+S，或菜单 **文件 → 保存分组文件**）写出。完成后重启 BodySlide（通过 MO2 启动的话建议连 MO2 一起重启），分组下拉里即可看到新组。

界面其他要点：菜单栏为 **文件 / 编辑 / 工具 / 界面主题 / 语言 / 帮助**（四种语言都带访问键，可用 Alt 序列操作）；**运行日志**默认折叠成保存按钮下方的一条摘要（有警告/错误时用警示色并带计数徽标），点一下展开——展开后按级别着色、可复制、可清空、高度可拖拽。

## 写到哪里

保存时**每个组生成一个文件，文件名即组名**（如 `UBE.xml`、`护甲.xml`）——在 BodySlide 的 SliderGroups 目录里一眼看出哪个文件是哪个组。绝不改动其他分组文件；重复保存会覆盖同名组文件，并通过清单自动清理改名/删除组留下的旧文件。输出位置有五种模式：

| 模式 | 位置 | 适用 |
|---|---|---|
| 自动（推荐） | 按下述规则 | 绝大多数情况 |
| BodySlide 程序目录 | `<BodySlide 目录>\SliderGroups\` | BodySlide 目录旁有 SliderSets 的安装 |
| MO2 专用模组 | `<MO2 mods>\BS Group Generator\CalienteTools\BodySlide\SliderGroups\` | 通过 MO2 启动 BodySlide（最干净：可按 profile 开关、重装 BodySlide 不丢） |
| 游戏真实 Data | `<游戏 Data>\CalienteTools\BodySlide\SliderGroups\` | 不经 MO2 启动 BodySlide 时 |
| 自定义（浏览选择） | 「浏览…」选择的任意路径 | 想自己管理文件；注意确认 BodySlide 能读到该位置 |

「自动」的规则：BodySlide 的有效项目路径若是真实目录（如 BodySlide 程序目录），直接写它的 `SliderGroups`；若是 MO2 虚拟 Data 下的 `CalienteTools\BodySlide`（最常见），则写入 MO2 专用模组（无 MO2 时退回游戏真实 Data）。

## 它是如何知道 BodySlide 会显示哪些服装的

工具复刻了 BodySlide（v5.8.2 / dev 分支）源码里的两段关键逻辑：

- **有效项目路径**：`Config.xml` 的 `ProjectPath` → 若 `BodySlide.exe` 旁存在 `SliderSets` 目录则用 exe 目录 → `<GameDataPath>\CalienteTools\BodySlide` → `<GameDataPath>\Tools\BodySlide`。
- **服装清单**：`<有效项目路径>\SliderSets\*.xml|*.osp` 中的 `<SliderSet name="…">` 名称，逐字符原样使用（BodySlide 的成员匹配是大小写敏感的精确比较）。通过 MO2 启动时该目录是虚拟 Data 的汇聚点，工具按 profile 的模组优先级**模拟 USVFS 覆盖**（同名相对路径文件由更强的模组获胜），因此列出的服装与 BodySlide 启动后看到的完全一致。

同名服装出现在多个模组（同名冲突）时，归属给优先级最高的模组并在树里以蓝色标注；这不影响分组的正确性（BodySlide 的组成员本来就是按名称匹配的）。

## 常见问题

**MO2 正在运行时能用吗？** 能。工具读取的是磁盘上的 `ModOrganizer.ini` / `modlist.txt`（MO2 退出时才回写设置，运行中改动可能略有滞后）；写出后需重启 BodySlide，必要时重启 MO2 以刷新虚拟文件系统。

**能从 MO2 里启动它吗？** 能，但**没必要**——工具与 MO2 只有磁盘读写关系，不需要虚拟文件系统，双击即用。想顺手一点就在 MO2 右下的「执行文件」下拉 → **配置…** → 添加，选 `BSGroupGenerator.exe`，参数留空（程序不读命令行）。**不要把它装成模组**：它不是游戏数据，装成模组只会让它经由虚拟文件系统启动。

**为什么是 .NET 8 而不是更新的版本？** 实测：.NET 10 的 `BSGroupGenerator.exe` 从 MO2 启动**必崩**——MO2 会往子进程注入 `usvfs_x64.dll` 挂钩文件 API，.NET 10 的引导层在这种注入下 100% 触发访问违例（`0xc0000005`，故障模块 unknown，进程还没加载 coreclr 就死了），而同一份程序改成 net8 目标就正常。四种发布形态（框架依赖/自包含 × 多文件/单文件）都救不了 net10。所以 `TargetFramework` 钉在 `net8.0-windows`，**升回 net10 之前务必先在 MO2 里点一次验证**。

**生成的组在 BodySlide 里看不到？** 1) 确认重启了 BodySlide/MO2；2) 在工具「**工具 → 诊断信息**」里检查「有效项目路径」和「写出目标」是否对应同一个目录——BodySlide 只从有效项目路径的 `SliderGroups` 读分组。

**支持哪些游戏？** 全部——分组机制与游戏无关（天际 SE/AE、辐射4 等都适用），跟随 MO2 实例与 BodySlide 安装自动适配。

**组和成员可以重名/大小写不同吗？** 组名在工具内忽略大小写（避免混乱）；成员名严格保留原样，与 BodySlide 的精确匹配行为一致。

## 开发

```
dotnet build src/BSGroupGenerator.Wpf/BSGroupGenerator.Wpf.csproj
dotnet test tests/BSGroupGenerator.Tests/BSGroupGenerator.Tests.csproj
dotnet test tests/BSGroupGenerator.Wpf.Tests/BSGroupGenerator.Wpf.Tests.csproj
publish.ps1                  # 生成 dist\（框架依赖，6 个文件约 0.6 MB，需 .NET 8 Desktop Runtime）
publish.ps1 -SelfContained   # 生成 dist\（自包含，约 133 MB，用户无需安装任何依赖）

python tools/verify_l10n.py src/BSGroupGenerator.Wpf   # 语言文件键一致性 / Core 层不得出现中文字面量 / 菜单访问键（CI 门禁）
python tools/verify_contrast.py                        # 两套色板按 WCAG 2.1 逐对算对比度（CI 门禁）
python tools/verify_theme.py                           # 窗口是否显式套用主题样式 / 两套色板键集合是否一致（CI 门禁）
dotnet run --project tools/layout-probe -c Release     # 4 语言 × 2 主题 × 11 窗口 × 各档尺寸，断言 0 处溢出/裁切（CI 门禁）
```

- 技术栈：C# / .NET 10 WPF（界面）+ 纯 C# Core 类库（扫描/解析/读写），仅第三方依赖 CommunityToolkit.Mvvm。
- 界面语言：`src/BSGroupGenerator.Wpf/Strings/Lang.{zh,en,ru,fr}.xaml` 四份资源字典，键必须一一对应（缺键时界面会直接显示键名）。新增语言需改四处：新建语言文件、在 `L10n.Supported` 登记、在 `MainWindow.xaml` 加菜单项、在 `SyncLangChecks()` 加勾选同步，改完跑一次上面的校验脚本。
- Core 层不依赖 UI，面向用户的文案一律经 `CoreStrings.Get/Format("L.Core_…")` 取词，由 WPF 层在启动时注入 `L10n.TrF`；Core 里出现中文字符串字面量会被门禁拦下（注释不算）。
- 主题色集中在 `Themes/Palette.{Boutique,Light}.xaml`，控件模板在 `Themes/Controls.xaml`，颜色一律经 `DynamicResource B.*` 引用，两套色板的键集合必须一致且对比度达标（门禁会查）。视图里不得再出现硬编码颜色。
- 窗口样式必须**显式**套用：`Themes/Controls.xaml` 里的 `AppWindow`（背景 / 前景 / 字体 / 渲染选项）要靠根元素 `Style="{StaticResource AppWindow}"` 引用才生效。隐式样式按元素**确切类型**匹配，`TargetType="Window"` 的隐式样式对 `x:Class` 生成的 `MainWindow` 与各对话框（`Window` 的派生类）不起作用，漏掉就会退回系统默认的白底黑字。根元素同时保留一份 `Background="{DynamicResource B.Window}"` 作兜底（本地值优先于样式 setter）。新增窗口时照抄现有窗口的根元素写法，跑 `verify_theme.py` 即可确认没漏。
- 容器尺寸不得按单一语言的标签宽度定死：固定像素列（`Width="96"`）、`UniformGrid` 等分列都会在更长的译文下静默裁掉文字，而中文的窄标签恰好能把这类问题掩盖住。用 `WrapPanel` 让它按内容定宽，长文本加 `TextWrapping`，单行标签加 `TextTrimming`；改完跑布局探针（它加载真实资源字典与真实窗口 XAML 离屏排一遍，能拦住"按钮压住列表""主操作行被裁"这类回归）。
- 改 `MainWindow.xaml` 的 `MinWidth` / `MinHeight` 时同步改 `tools/layout-probe/Program.cs` 里的 `SizesFor("MainWindow")`，否则最小尺寸那一档就失去意义。
- 测试覆盖：modlist.txt 与 ModOrganizer.ini 解析、GetProjectPath 复刻、VFS 覆盖扫描、分组文件读写（UTF-8 BOM）、Core 取词契约、树视图模型。
- 分组 XML 格式依据 ousnius/BodySlide-and-Outfit-Studio 的源码行为逆向确认（`SliderGroup.cpp` / `BodySlideApp.cpp` / `ProjectUtil.cpp`），未复制其代码。
