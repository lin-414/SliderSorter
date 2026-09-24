# SliderSorter

读取 **Mod Organizer 2 (MO2)** 安装的模组，让用户把服装模组批量划进 **BodySlide 分组（SliderGroups）** 的 Windows 独立小工具。

BodySlide 自带的 Group Manager 只有一个服装平铺列表，不知道哪个服装来自哪个模组；本工具补上这一环：**按模组勾选，一键整组归组**，也能展开后逐个服装微调。

界面为 WPF，自带**暗色 / 亮色**两套主题，支持**中文 / English / Deutsch / Русский / Français** 五种界面语言切换（都在**设置 → 外观**里，选定即时生效，无需重启）。

**运行要求**：Windows x64 + [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。发布包为**框架依赖**版（12 个文件、约 2.2 MB，其中 Nifly 网格解析库 0.9 MB），不含运行时，首次使用请先安装上面的运行时。

## 使用方法

1. 启动后自动发现 MO2 实例（全局实例 + 便携安装）。找不到就用「添加 MO2 目录…」选择含 `ModOrganizer.ini` 的实例目录或含 `ModOrganizer.exe` 的便携安装目录。
2. 选择配置（Profile）——工具解析该 profile 的 `modlist.txt`，列出所有**启用**的模组。
3. BodySlide 通常自动检测成功（在启用模组中寻找含 `BodySlide*.exe` 与 `Config.xml` 的目录）。界面会显示「有效项目路径」——即 BodySlide 实际读取服装/分组的位置，与 BodySlide 自己的 `ProjectUtil::GetProjectPath()` 逻辑完全一致。
4. 在右侧**新建组**（面板标题栏的 **「⋯」→ 新建组**，组列表为空时面板中间也有按钮）并选中它，然后在左侧**勾选**模组（或展开勾选单个服装；**勾选分隔符 = 全选其下所有模组**），再点两栏中间搬运栏上的 **「加入组」**——这一步才真正把服装写进组；勾错的用「移出组」撤销。搬运按钮上会显示当前勾选了多少个服装，置灰时把鼠标移上去会说明原因。
   - 已在当前组里的服装在名字前显示**成员标记**（不再是文本前缀），模组标题会附 `[组内 x/总数]`。
   - 模组树遵循 **MO2 左侧栏的顺序**，只显示含 BodySlide 服装的启用模组；你的分隔符显示为灰色分组标题（`_separator` 后缀自动去掉），方便在 MO2 的结构里对照定位。
   - **组管理都在「⋯」里**（也可直接右键组列表）：新建 / 重命名 / 查看组 / 导入组文件 / 删除组——低频动作不再占用主面板按钮位。「撤销」与「规则分组」不在菜单里，它们是「分组」标题右侧那两颗常驻按钮。
   - **「规则分组」是「分组生成」页底部的一条抽屉**，由「分组」标题右侧那颗按钮开合（`Ctrl+3` = 回到这一页并拉开它；目标组默认就填着你当前选中的那个组）。一条自动归组规则的**编辑、预览、存成预设、执行**都在这一屏里：三张卡并排——「已存预设」（每行是预设名 + "加入/移出 → 哪个组 · 三类关键字"）、「规则条件」（预设名称 / 目标组 / 方向 / 模组·服装·排除三类关键字 / 仅未分配）、「命中预览」（按 分隔符 → 模组 → 命中服装 树形展示，随条件实时刷新）。关键字分号分隔多个、不区分大小写、任一命中即算；模组关键字先筛模组、再取其下服装，不会把其他模组里的同名服装混进来。卡下面一条操作行：左半是预设的**新建 / 保存 / 删除**（同名保存即覆盖，改名字再保存＝另存一条新预设），右半是**按预设全部应用**与**应用此规则**——后者跑的是表单里的条件，不必先存下来。改了条件而没保存时，「规则条件」那张卡的标题右侧会亮一枚「未保存」。
   - 「新装模组提醒」：装了新服装模组后再打开程序，扫描会自动弹窗，按 分隔符 → 模组 → 服装 分层列出新增内容（与主窗口一致，可展开查看具体服装），勾选并选好目标组即可一键归组；弹窗顶部也有**两个过滤框**（左筛服装名、右筛模组 / 分隔符名），列表长时用它快速定位，**勾选状态不会因过滤丢失**。关闭则暂不处理，之后仍可用「仅看未分配」找到它们。
   - 「撤销」（**编辑 → 撤销** 或 Ctrl+Z）：最近 30 步分组操作可逐步回退，误点不慌。
   - 「输出归属」（**第 2 个标签页**，或直接点状态栏上的「输出冲突 N 组」）：多个模组的服装会写进**同一个 `.nif`** 时（BodySlide 的"输出文件冲突"），在这里为每组指定由谁来生成。选择先存在本工具里；按下「写入 BodySlide…」才落成 BodySlide 认得的 `BuildSelection.xml`（原文件自动备份成 `.bak`），此后 BodySlide 批建不再逐次弹窗。页面自上而下是：一句话说明（右侧一行「已指定 M / N 组」的进度，还有没指定的会补一句警示色的「· 未指定 K 组」；页内不再重复标签页的名字，三个标签页都只由标签头承担页名）→ 工具条（过滤框、只看跨模组、只看某组、显示计数、重新扫描）→ **三栏**（左「冲突组」：**按你自己的分组分类**——每个分组头下面是"有该组成员卷入"的冲突，没进任何分组的收在「未入组」里；每行是输出文件名 +「未指定／已选：谁」，该组自己有 2 个以上成员争同一个文件的还会挂一枚「组内互撞」徽标；**分组头是可折叠的**——点一下收起该组名下的冲突行、只留标题（左侧箭头指示展开态），收起过哪些组写进设置，下次打开还是那样；右键分组头另有「全部展开 / 全部折叠 / 折叠其他」（与左侧模组树的同名菜单一致，折叠分组多时不必逐个点，「折叠其他」保留被右键的那一组）；收起只是"先不看它"，批量动作仍作用于整份清单，而工具条上「只看某组」筛到单个分组时那一组一律展开（免得刚点名要看它却只看到一条光杆标题）；中「由谁生成」：每行是服装名、模组·层号与「已在组内」「优先级最高」两枚徽标，末尾一条分隔线之下的「不指定」是交回 BodySlide 再问；右 **3D 预览**）→ 操作行（左半是"对当前列出的 N 组"的批量动作，右端是写入）。左栏的分组分类是**按"卷入"**算的：某个分组只要有成员出现在这条冲突的候选里，这条冲突就归到它名下——赢家全局只有一个，输掉的那个组的成员就不会被建出来，所以两个组都得看到它（因此一条冲突可能出现在多个分组头下面，但「N 组」按冲突去重）。预览显示当前选中那一行的源网格：左键点一行看它的模型、**右键点一行直接把它设为赢家**，拖动转视角、滚轮缩放；**同一条冲突里换模组时视角一动不动**（比的就是同一个 `.nif` 换个模组长什么样：每换一行都按新网格重新取景的话，画面会轻微平移加缩放，两件衣服的差别正好被这点跳动盖掉；右键拖出来的视野同样留着——哪怕这一件自带身体、下一件要垫一具，"垫了哪具"不算换衣服）。换到**另一条冲突**才按那件重新取景并清掉拖出来的视野；「身体」换取值会重新取景（视野偏移留着）；「复位视角」回到正面并按当前这一件重新取景。默认勾着「只看跨模组冲突」——同一模组内部几百个配色预设共用一个输出文件是常态，别和"多个模组改了同一件衣服"混在一起看。
   - 「查看组」（或双击组名）：预览该组的全部服装，可按名称过滤、可勾选批量移出。
   - 「仅看未分配」：只显示还没进任何组的服装。
   - **两个过滤框各管一类**：「过滤服装名…」只匹配服装名，「过滤模组名…」只匹配模组名（连续子串，命中模组时显示该模组全部服装并标注"匹配 x/总数"）。
   - 组列表右键（或右侧「⋯」）里的 **导入组文件…**：把已有分组 XML 合并进来继续编辑。
5. 点击**保存分组文件**（或 Ctrl+S）写出。完成后重启 BodySlide（通过 MO2 启动的话建议连 MO2 一起重启），分组下拉里即可看到新组。

界面其他要点：顶部三个标签页 **分组生成 / 输出归属 / 设置**（`Ctrl+1`/`Ctrl+2`/`Ctrl+4` 直达，`Ctrl+,` 或 F1 进设置），第四个去处不是页——**规则分组**是分组生成页里的一条抽屉（`Ctrl+3`）；组的新建/重命名/查看/删除与导入收在组列表的右键菜单（以及右侧那颗「⋯」）里，**撤销**（Ctrl+Z）与「规则分组」开关常驻在「分组」标题右侧；界面语言与主题在**设置 → 外观**，五种语言的标签页标题都随语言即时切换；**运行日志**默认折叠成保存按钮下方的一条摘要（有警告/错误时用警示色并带计数徽标），点一下展开——展开后按级别着色、可复制、可清空、高度可拖拽。

## 写到哪里

保存时**每个组生成一个文件，文件名即组名**（如 `UBE.xml`、`护甲.xml`）——在 BodySlide 的 SliderGroups 目录里一眼看出哪个文件是哪个组。绝不改动其他分组文件；重复保存会覆盖同名组文件，并通过清单自动清理改名/删除组留下的旧文件。输出位置有五种模式：

| 模式 | 位置 | 适用 |
|---|---|---|
| 自动（推荐） | 按下述规则 | 绝大多数情况 |
| BodySlide 程序目录 | `<BodySlide 目录>\SliderGroups\` | BodySlide 目录旁有 SliderSets 的安装 |
| MO2 专用模组 | `<MO2 mods>\SliderSorter Output\CalienteTools\BodySlide\SliderGroups\` | 通过 MO2 启动 BodySlide（最干净：可按 profile 开关、重装 BodySlide 不丢） |
| 游戏真实 Data | `<游戏 Data>\CalienteTools\BodySlide\SliderGroups\` | 不经 MO2 启动 BodySlide 时 |
| 自定义（浏览选择） | 「浏览…」选择的任意路径 | 想自己管理文件；注意确认 BodySlide 能读到该位置 |

「自动」的规则：BodySlide 的有效项目路径若是真实目录（如 BodySlide 程序目录），直接写它的 `SliderGroups`；若是 MO2 虚拟 Data 下的 `CalienteTools\BodySlide`（最常见），则写入 MO2 专用模组（无 MO2 时退回游戏真实 Data）。

**「写入 BodySlide…」产出的 `BuildSelection.xml` 也走这张表**：它写在输出位置里的 `CalienteTools\BodySlide\`（自定义模式就是所选目录本身），也就是与分组文件**同一个模组**；确认框与成功提示都会列出全路径，原文件先备份成 `.bak`。BodySlide 读它用的是 `Config["AppDir"]`（`BodySlideApp.cpp:1196`，分组读的则是 `ProjectUtil::GetProjectPath() + "/SliderGroups"`，同文件 :3338），而 MO2 下这个"程序目录"是**虚拟**的：usvfs 会把 `GetModuleFileNameW` 的返回值按反向映射改写成虚拟路径（`usvfs/src/usvfs_dll/hooks/kernel32.cpp` 的 `hook_GetModuleFileNameW`），于是它实际读写的是 `Data\CalienteTools\BodySlide\BuildSelection.xml` 在虚拟视图里落到的那一份——用户机上的 usvfs 日志就是 `mapping file in vfs: …\Data\CalienteTools\BodySlide\BuildSelection.xml → …\mods\<模组>\CalienteTools\BodySlide\BuildSelection.xml`。所以它必须和分组文件同处一个模组才读得到；写进所选 BodySlide 安装的**真实**目录会被优先级更高的模组挡住（BodySlide 读到的还是别人那份旧的）。

## 它是如何知道 BodySlide 会显示哪些服装的

工具复刻了 BodySlide（v5.8.2 / dev 分支）源码里的两段关键逻辑：

- **有效项目路径**：`Config.xml` 的 `ProjectPath` → 若 `BodySlide.exe` 旁存在 `SliderSets` 目录则用 exe 目录 → `<GameDataPath>\CalienteTools\BodySlide` → `<GameDataPath>\Tools\BodySlide`。
- **服装清单**：`<有效项目路径>\SliderSets\*.xml|*.osp` 中的 `<SliderSet name="…">` 名称，逐字符原样使用。同名判重**忽略大小写**（BodySlide 的 `outfitNameSource` 是 `case_insensitive_compare` 的 map，`BodySlideApp.h:125`），而**分组成员**的匹配是逐字节精确比较（`SliderGroup.cpp:90-96`）——BodySlide 自己这两处就不对称，工具照抄而不是统一成一个。另外 BodySlide 是先整批读 `*.osp`、再整批读 `*.xml`（`BodySlideApp.cpp:837-839`），所以弱层 `.osp` 里的同名 set 会盖掉强层 `.xml` 里的那个。通过 MO2 启动时该目录是虚拟 Data 的汇聚点，工具按 profile 的模组优先级**模拟 USVFS 覆盖**（同名相对路径文件由更强的模组获胜），因此列出的服装与 BodySlide 启动后看到的完全一致。

同名服装出现在多个模组（同名冲突）时，归属给优先级最高的模组并在树里以蓝色标注；这不影响分组的正确性（BodySlide 的组成员本来就是按名称匹配的）。

**输出文件冲突**是另一回事：两个**不同名**的滑块组可能声明同一个 `<OutputPath>`（外加可选的 `<OutputFile>`），批建时 BodySlide 会先弹窗让你挑一个，勾中之外的整批不建。工具按 BodySlide 的 `SliderSetFile::GetSetOutputFilePath` 口径拼这个路径（`/` 换成 `\`、有 `<OutputFile>` 时追加分隔符和它，元素内空白原样保留——它用的是 tinyxml2 的 `GetText()`），聚类则**忽略大小写**：那边的容器是 `std::map<…, case_insensitive_compare>`（`BodySlideApp.h:202`），所以 `Meshes/Foo` 与 `meshes/foo` 在 BodySlide 眼里是一组冲突、会弹窗，工具也必须当成一组。组键取首个成员的原样写法（`std::map` 同样保留首次插入的拼写）；「写入 BodySlide…」产出的 `<BuildSelection><OutputChoice path="…" choice="…"/></BuildSelection>` 它能直接读回去，而**同一组的每种拼写各写一条**——它查选择用的表区分大小写（`BuildSelection.h:21`），组键拼写又取决于它的目录遍历顺序，多写的那几条它读得到却用不到，绝不会指错赢家。只改本工具认得的那些条目（原位改属性，不重排、不丢注释——tinyxml2 的 `NextSiblingElement(name)` 会跳过异名兄弟，交错排列照样读得到），`<ZapChoice>` 与别人写的条目原样保留。

## 常见问题

**MO2 正在运行时能用吗？** 能。工具读取的是磁盘上的 `ModOrganizer.ini` / `modlist.txt`（MO2 退出时才回写设置，运行中改动可能略有滞后）；写出后需重启 BodySlide，必要时重启 MO2 以刷新虚拟文件系统。

**能从 MO2 里启动它吗？** 能，但**没必要**——工具与 MO2 只有磁盘读写关系，不需要虚拟文件系统，双击即用。想顺手一点就在 MO2 右下的「执行文件」下拉 → **配置…** → 添加，选 `SliderSorter.exe`，参数留空（程序不读命令行）。**不要把它装成模组**：它不是游戏数据，装成模组只会让它经由虚拟文件系统启动。

**为什么是 .NET 8 而不是更新的版本？** 实测：.NET 10 的 `SliderSorter.exe` 从 MO2 启动**必崩**——MO2 会往子进程注入 `usvfs_x64.dll` 挂钩文件 API，.NET 10 的引导层在这种注入下 100% 触发访问违例（`0xc0000005`，故障模块 unknown，进程还没加载 coreclr 就死了），而同一份程序改成 net8 目标就正常。四种发布形态（框架依赖/自包含 × 多文件/单文件）都救不了 net10。所以 `TargetFramework` 钉在 `net8.0-windows`，**升回 net10 之前务必先在 MO2 里点一次验证**。

**生成的组在 BodySlide 里看不到？** 1) 确认重启了 BodySlide/MO2；2) 在工具「**工具 → 诊断信息**」里检查「有效项目路径」和「写出目标」是否对应同一个目录——BodySlide 只从有效项目路径的 `SliderGroups` 读分组。

**「同名冲突」和「输出冲突」有什么不同？** 同名冲突是两个模组用了**同一个滑块组名**（忽略大小写算同名），BodySlide 先见者胜、后见的那个根本不会进它的服装清单——归属给优先级最高的那个模组只是本工具的显示口径，不影响正确性。输出冲突是两个**不同名**的滑块组写**同一个 `.nif`**：BodySlide 批建时不是"后建的覆盖先建的"，而是先弹一个「选择输出文件」的框，把没勾中的那些**整个从这一轮构建里去掉**（`BodySlideApp.cpp:4217-4435`），所以一组冲突要么建赢家、要么一个都不建——这才需要人来定夺，状态栏只数这一种，树里也只给它加「（输出冲突）」后缀。没存过选择时它默认预勾的是**自己发现顺序里的第一个**（`:4289`），那个顺序在 MO2 下与模组优先级无关；页面上「全部按模组优先级」选的是**游戏本来会读到的那一个**，属于刻意覆盖它的默认，而不是复刻。

**为什么页面上的组数比模组树里标注的少？** 冲突页说明里那句「跨模组冲突 N 组、同一模组内部共用 M 组」报的是全部数量，其中一大半往往是同一个模组自己的配色预设（一件衣服几百个预设共用一个输出文件，那是模组的设计，不是模组之间打架）。状态栏与树里的「（输出冲突）」只算**跨模组**的那 N 组；在页面上取消勾选「只看跨模组冲突」就能看到全部 M 组——它们同样需要指定赢家才能安静地批建。

**3D 预览显示的是最终进游戏的样子吗？** 不是，是**基准网格 + 真实纹理**。网格是 BodySlide 建这件衣服时读的那个源 `.nif`（`<项目路径>\ShapeData\<DataFolder>\<SourceFile>`，跨模组覆盖层从强到弱解析）；贴图按游戏同一套口径解析——每一层先看散文件、再看该层的 `.bsa`/`.ba2`，所以颜色跟你装的那个纹理模组一致（纹理常常和 mesh 分开装在另一个模组里，这点必须跨层找）。衣服自己没带身体时会垫一具，而垫的也是**你装的那具**（同一套层序解析，所以 CBBE/HIMBO 生效）。**没有**的是：滑块变形后的形状（那只有回 BodySlide 建出来才看得到）、法线/高光/环境贴图与逐像素光照（WPF 的 `Viewport3D` 是顶点级光照的固定管线，没有像素着色器，那些贴图在这里没有消费者）。它回答的是"这个模组的这件衣服是个什么版型、什么颜色、和另一个模组的差在哪"。源网格找不到、或文件解析失败时，预览区会直接说原因，不会静默留白。

**支持哪些游戏？** 全部——分组机制与游戏无关（天际 SE/AE、辐射4 等都适用），跟随 MO2 实例与 BodySlide 安装自动适配。

**组和成员可以重名/大小写不同吗？** 组名在工具内忽略大小写（避免混乱）；成员名严格保留原样，与 BodySlide 的精确匹配行为一致。

## 开发

```
dotnet build src/SliderSorter.Wpf/SliderSorter.Wpf.csproj
dotnet test tests/SliderSorter.Tests/SliderSorter.Tests.csproj
dotnet test tests/SliderSorter.Wpf.Tests/SliderSorter.Wpf.Tests.csproj
publish.ps1                  # 生成 dist\（框架依赖，12 个文件约 2.2 MB，需 .NET 8 Desktop Runtime）
publish.ps1 -SelfContained   # 生成 dist\（自包含，约 133 MB，用户无需安装任何依赖）

python tools/verify_l10n.py src/SliderSorter.Wpf   # 语言文件键一致性 / Core 层不得出现中文字面量 / 菜单访问键（CI 门禁）
python tools/verify_contrast.py                        # 两套色板按 WCAG 2.1 逐对算对比度（CI 门禁）
python tools/verify_theme.py                           # 窗口是否显式套用主题样式 / 两套色板键集合是否一致（CI 门禁）
dotnet run --project tools/layout-probe -c Release     # 5 语言 × 2 主题 × Views 下全部窗口与页面 × 各档尺寸，断言 0 处溢出/裁切（CI 门禁）
```

- 技术栈：C# / .NET 8 WPF（界面，目标框架钉在 `net8.0-windows`，原因见上面的 .NET 10 条目）+ 纯 C# Core 类库（扫描/解析/读写）。第三方依赖四个，**全是纯托管程序集，不引入任何原生 DLL**：界面层的 [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/MVVM)（MIT）；Core 里读 `.nif` 的 [Nifly](https://github.com/ousnius/NiflySharp)（GPL-3.0，BodySlide 作者写的）、读 `.bsa`/`.ba2` 归档的 [Sharp.BSA.BA2](https://github.com/p1x/BSA_Browser)（GPL-3.0）、解 DDS 纹理的 [Pfim](https://github.com/nickbabcock/Pfim)（MIT）。
- 3D 预览走 WPF 自带的 `Viewport3D`，不是 HelixToolkit.SharpDX。**别把理由记成"SharpDX 会带原生 DLL"**——实测它那套包的 `dotnet restore` 里 native 资产数为 0（SharpDX 是托管 COM 包装，调的是系统自带的 `d3d11.dll`）。真正的取舍是：SharpDX 需要可用的 D3D11 设备并经由 `D3DImage` 与 WPF 合成（远程桌面/关掉硬件加速时会黑屏），而 `Viewport3D` 没有这个前提。真要换栈，资产管线（NIF 材质解析、跨层定位、归档读取、DDS 解码）可以原样复用，只有最后画出来的那一步要重写。
- 纹理必须自己解码：WPF 的 `BitmapImage`（走 WIC）只认老式 fourCC 的 DXT1/DXT3/DXT5，而天际 SE/AE 的纹理大量是 DX10 头 + BC7，喂进去直接抛 `COMException 0x88982F61`（"图像标题无法识别"）。所以用 Pfim 解成 32 位再 `BitmapSource.Create`，并把长边压到 1024（2048² 解出来一张就是 16 MB）。
- 界面结构：**没有菜单栏**（原先的 文件/编辑/工具/界面主题/语言/帮助 全部取消——剩下的每一项要么与页内控件重复，要么本身就该是个页面）。`MainWindow` 只是壳（顶部 `TabControl` + 状态栏 + 扫描遮罩），`Views/Pages/` 下四份页面——**分组生成**（树 → 搬运 → 分组 → 写文件）、**输出归属**、**设置**（工作环境 / 输出设置 / 外观 / 帮助与关于，四节单列自上而下、共用一条自适应标签轨，信息架构取自 boutique；使用说明、诊断信息、检查更新、关于全部内嵌，不再各开一个窗口），以及**规则分组**（预设 + 条件表单 + 命中预览，三张卡并排）——最后一份不挂在 `TabControl` 上，它是分组生成页底部的一条抽屉，由组面板标题右侧那颗按钮开合。选中页是 `MainViewModel.SelectedTab`（只有三页），所以状态栏的冲突计数与 F1 都能主动切页；`Ctrl+1`/`2`/`4` 直达三页，`Ctrl+3` 拉开规则抽屉，`Ctrl+,` 与 F1 也保留。组的增删改查与导入组文件收在组列表的右键菜单（以及右侧那颗「⋯」）里，撤销与规则分组开关常驻在「分组」标题右侧。新增一页要同时登记三处门禁，否则它会**静默**逃过检查（这些工具原先只扫 `Views/*.xaml`，页面放进子目录不报错、只是不再被看）：`tools/verify_theme.py` 的 §6 要求它能从壳走到（直接被壳挂上，或被另一份已上壳的页面嵌进去，都算走到）、`tools/layout-probe` 按页面根宿主进窗口量、`XamlCommandBindingTests.BoundTarget` 要登记它的 DataContext。
- 界面语言：`src/SliderSorter.Wpf/Strings/Lang.{zh,en,de,ru,fr}.xaml` 五份资源字典，键必须一一对应（缺键时界面会直接显示键名）。新增语言要改四处：新建语言文件、在 `L10n.Supported` 登记、在 `MainViewModel.BuildLanguageOptions()` 加一项、在 `tools/layout-probe` 的 `Langs` 里补上这一门语言——漏掉最后一处的话新语言从没被布局门禁量过，而它恰恰是最可能撑破标签轨的那一种（德语的长复合词）。设置里存的是**诉求**（`AppSettings.UiLanguage`）而不是生效语言，默认 `"system"`（跟随系统）：`L10n.Current` 是真正装载的那一种，设置页下拉的选中项与去重判断则要比 `L10n.Normalize(Settings.UiLanguage)`——「跟随系统」下这两者不等，比 `Current` 会让"英文系统上手动点一下 English"变成无声的空操作，`"system"` 也永远清不掉。系统语言按 `CultureInfo.CurrentUICulture`（Windows 的显示语言，不是区域格式、也不是 WPF 那个恒为 `en-US` 的 `FrameworkElement.Language`）取两字母码匹配，不在 `L10n.Supported` 里时回落 `L10n.SystemFallback`（= 英语）。语言下拉的后五项标签恒为各语言自己的写法（中文 / English / Deutsch / Русский / Français），所以不进 Lang 文件——界面被翻坏时那是唯一还认得出来的线索；「跟随系统」是例外（它描述行为不是语言名），走 `L.Settings_LanguageSystem`，五份语言文件都要有。另有一半易漏的：`{DynamicResource}` 那些靠换字典当场自己刷新，而 ViewModel 用 `L10n.Tr/TrF` **拼好的字符串**没有变更源（值早就算下了），必须逐个在 `MainViewModel.OnLanguageChanged()` 里补一句 `OnPropertyChanged`，否则切完语言还留着旧语言那一句（状态栏「上次扫描：…」踩过一次，`LanguageSwitchTests` 现在钉着它）。
- Core 层不依赖 UI，面向用户的文案一律经 `CoreStrings.Get/Format("L.Core_…")` 取词，由 WPF 层在启动时注入 `L10n.TrF`；Core 里出现中文字符串字面量会被门禁拦下（注释不算）。
- 主题色集中在 `Themes/Palette.{Boutique,Light}.xaml`，控件模板在 `Themes/Controls.xaml`，颜色一律经 `DynamicResource B.*` 引用，两套色板的键集合必须一致且对比度达标（门禁会查）。视图里不得再出现硬编码颜色。
- 下拉框不吃滚轮：`Themes/Controls.xaml` 的隐式 ComboBox 样式统一挂上 `ui:IgnoreWheel.Enabled`（`Services/IgnoreWheel.cs`），新增下拉框不必各自处理。WPF 的 ComboBox 在**自己持有键盘焦点**时会对滚轮逐项换选中值，而设置页那一列下拉框选完即落盘、还会连带重扫——刚点过语言那一项、顺手往下滚页面就又换了一种语言。没有焦点时不拦：那时 WPF 本来就不动选中项、把滚轮让给页面，一律掐掉会让长页面滚到下拉框那一格卡住。
- 窗口样式必须**显式**套用：`Themes/Controls.xaml` 里的 `AppWindow`（背景 / 前景 / 字体 / 渲染选项）要靠根元素 `Style="{StaticResource AppWindow}"` 引用才生效。隐式样式按元素**确切类型**匹配，`TargetType="Window"` 的隐式样式对 `x:Class` 生成的 `MainWindow` 与各对话框（`Window` 的派生类）不起作用，漏掉就会退回系统默认的白底黑字。根元素同时保留一份 `Background="{DynamicResource B.Window}"` 作兜底（本地值优先于样式 setter）。新增窗口时照抄现有窗口的根元素写法，跑 `verify_theme.py` 即可确认没漏。
- 容器尺寸不得按单一语言的标签宽度定死：固定像素列（`Width="96"`）、`UniformGrid` 等分列都会在更长的译文下静默裁掉文字，而中文的窄标签恰好能把这类问题掩盖住。用 `WrapPanel` 让它按内容定宽，长文本加 `TextWrapping`，单行标签加 `TextTrimming`；改完跑布局探针（它加载真实资源字典与真实窗口 XAML 离屏排一遍，能拦住"按钮压住列表""主操作行被裁"这类回归）。
- 改 `MainWindow.xaml` 的 `MinWidth` / `MinHeight` 时同步改 `tools/layout-probe/Program.cs` 里的 `ShellSizes`（壳与四页共用这一组尺寸），另外 `PageChromeAllowance` 是页面可用高要扣掉的标签头 + 状态栏，壳改这两处任一样式都要同步。
- 测试覆盖：modlist.txt 与 ModOrganizer.ini 解析、GetProjectPath 复刻、VFS 覆盖扫描、分组文件读写（UTF-8 BOM）、Core 取词契约、树视图模型、输出文件冲突的解析与聚类（含"跨模组 vs 同模组内部"）、**输出冲突按用户分组分类（卷入判据、组内互撞、未入组桶、桶顺序）**、BuildSelection.xml 导出（保留别人的条目、**原位改属性而不重排**（交错排列的 ZapChoice 留在原处、注释不丢）、同一路径的重复条目合并成一条、幂等、坏文件不覆盖）、**写入目标的落点（BuildSelection.xml 跟着输出位置走、与分组文件同处一个模组；自定义模式写进所选目录；缺实例时按模式回落）**、**冲突分组忽略大小写（两种拼写并成一组、组键取首见写法、导出覆盖每种拼写）**、**同名 set 判重忽略大小写与 `.osp` 整批先于 `.xml`**、**`GenWeights` 按 tinyxml2 的 ToBool 读法（整数前缀／精确 true・false／其余回落缺省）与"后缀跟赢家不跟最强层"**、**老格式 `version<1` 的 `<SetFolder>` 与 `<OutputPath>` 内空白原样保留**、「输出冲突」页的行渲染（列表只留文件名）、**分组头渲染与「只看某组」筛选**、**分组头的展开折叠（收起后行不再可见、展开态写回设置、只看某组时强制展开且不落盘、收起不改变批量动作的范围）**、**分组头右键菜单的整列展开折叠（三项标题本地化、批量收起照常落盘、折叠其他保留被右键的那一组）**、**切语言后代码拼串的补通知（扫描状态行、未配置引导条）**、**滚轮经过下拉框：有焦点时不改选中项，没焦点时页面照常滚**、空状态与批量按钮的启用态、右键设赢家、预览接线与切走时释放视口捕获；源网格路径的跨层解析、预览取数据文件的跨层口径（散文件优先、层序胜负、UNC/绝对路径拒绝、名字像归档但内容不是的东西不许把整次解析带崩）、预览模型的 `CarriesOwnBody`/`IsTextured` 判定。
- 预览这条链**不是全靠单测兜的**，也兜不住：Nifly 的 `VertexPositions`/`Triangles`/`UVs` 是从打包的顶点流里算出来的只读属性，测试里造不出合法的 `.nif` 夹具（等于自己重写一遍编码器）。所以网格与贴图那一段是对着真机的 2667 个模组目录、6937 个 `ShapeData` 网格和官方 v105 归档跑探针验的，UV 的 V 轴朝向则用一张"上半红下半蓝"的位图打靶回读确定（WPF 的 `V=0` 就是位图首行，与 NIF 同约定，**不要翻转**）。改这块请照做同样的实测，别拿合成数据外推。
- 分组 XML 格式依据 ousnius/BodySlide-and-Outfit-Studio 的源码行为逆向确认（`SliderGroup.cpp` / `BodySlideApp.cpp` / `ProjectUtil.cpp`），未复制其代码。

## 许可

**GNU GPLv3**（或更新版本）—— 见 `LICENSE`。本程序是自由软件：你可以自由再分发或修改它，只要连同源码一起、继续按同一许可公开，并且保留"无任何保证"的声明。

选 GPL 而不是继续用 MIT，是因为**服装预览要解析 `.nif`**，而唯一在维护的纯 C# Creation Engine NIF 解析库 [Nifly](https://github.com/ousnius/NiflySharp)（BodySlide 作者 ousnius 写的）是 GPL-3.0。按 GPL 的链接条款，用它构建出来的程序整体必须按 GPL-3.0 分发，所以本项目一并改过去，而不是维持一个"名义 MIT、实际无法合规分发"的状态。

预览后来加的两个依赖与这个许可相容，不必再改条款：读归档的 [Sharp.BSA.BA2](https://github.com/p1x/BSA_Browser) 同样是 GPL-3.0，解 DDS 的 [Pfim](https://github.com/nickbabcock/Pfim) 是 MIT。引入它们之前确认过两者都不含原生二进制（`netstandard2.0`，传递依赖只有 LZ4/SharpZipLib 一类托管包）——这条约束是硬的，见上面 Viewport3D 那一节。

