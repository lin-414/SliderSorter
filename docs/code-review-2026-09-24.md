# SliderSorter 代码审查 · 2026-09-24

范围：`src/SliderSorter/Core`（19 个文件）+ `src/SliderSorter.Wpf`（ViewModels / Views / Services）。
基线：工作区当前未提交的版本。两套单测全绿（Core 194 例、Wpf 97 例），下面这些**都是现有用例没盖住的**。

前两条我把探针用例真跑了（临时文件已删，仓库无残留），证据附在下面。其余是靠读代码 + 精确行号确认的，标注了置信度。

---

## 修复情况（同日全部落地）

两套单测：Core 219 例、Wpf 100 例全绿；`tools/verify_l10n|contrast|theme` 与 `layout-probe`（160 个
语言×主题×视图×尺寸组合）全过；`publish.ps1` 出的 `dist\SliderSorter.exe` 实机跑过一轮真实扫描
（292 模组 / 6875 服装 / 581 组冲突），验收截图见对话记录。

| # | 条目 | 状态 |
|---|---|---|
| 1 | 「重新扫描」静默失效 | 已修（新增 `SelectBodySlide`）+ 用例 `RescanTriggerTests` |
| 2 | 组列表过滤后选错组 | 已修（改按 `SelectedItem` 身份）+ 用例 `FilteredGroupListKeepsAndSelectsTheVisibleRow` |
| 3 | 导入按钮丢掉所选文件 | 已修 |
| 4 | 复制把程序弄崩 | 已修（`Notify.CopyText` 兜 `ExternalException`） |
| 5 | 预览缓存并发 | 已修（缓存上锁 + 任务体内查取消 + `_textures`/`Assets` 快照） |
| 6 | 状态行「模组 N」慢一轮 | 已修 + 实机核对（首扫即显示 292） |
| 7 | 新建/重命名不刷未保存标志 | 已修（`RefreshGroupsList` 补 `UpdateTitle`） |
| 8 | 组名大小写两套口径 | 已修（统一到忽略大小写；-1 由界面报出来）+ 两条用例 |
| 9 | 新模组弹窗同名吞项 | **结论改了**：组成员本来就是按服装名记的（BodySlide 同口径），按名字去重与剔除是一致的；只修了「勾选不刷新摘要」那半边 |
| 10 | explorer 路径不加引号 | 已修（`ArgumentList`） |
| 11 | 设置非原子写 + 损坏静默清零 | 已修（临时文件+`File.Replace`、坏文件留档、`LoadNote`/`SaveNote` 进启动日志）+ 两条用例 |
| 12 | 归档查找层序 | 已修（逐层先散文件后归档）。没有纯托管的 .bsa 写入器，这一条**没法加用例**，与 `GameDataResolverTests` 的既有说明一致 |
| 13 | `OrdinalIgnoreCase` 与只折 ASCII 不等价 | 已修（新增 `AsciiCaseInsensitiveComparer`，换掉三处复刻 BodySlide 的字典；两处假注释改正）+ 用例 |
| 14 | 带 DOCTYPE 的文件整件作废 | 已修（`DtdProcessing.Ignore` + `XmlResolver = null`）+ 用例 |
| 15 | modlist 用 `FileShare.Read` | 已修（与 IniParser 同口径） |
| 16 | `backup` 正则过宽 | 已修（锚到结尾那一段）+ 七条 `Theory` 用例，含用户真实列表里那条 `MCM Memory - Settings Backup and Restore` |
| 17 | 实例登记未兜异常 / 去重键未归一 | 已修 |
| 20 | `\xHH` 按字节直投成 Latin-1 乱码 | 已修（整段收字节按 UTF-8 解，解不动才回落）+ 三条用例 |
| 18 | 主树过滤丢勾选 | 已修（`RebuildTree(preserveChecks: true)` 按 (模组,服装) 回灌）+ 用例 `TreeFilterCheckPersistenceTests` |
| 19 | 冲突页整表重建 | 部分：滚动位置已保住（记偏移 + 补一次布局后放回）；行仍是不可变记录、每次改赢家重建一次视图。要真做到就地更新，得先把 `GroupRow` 换成可通知对象，那是另一次改动 |
| 附 | 规则抽屉的模组归属缓存过期 | 已修（订阅 `TreeRoots` 变更作废并重算预览） |
| 附 | 空格键无行时也吞掉 | 已修 |
| 附 | 「全部折叠」逐组重写设置文件 | 已修（批量攒成一次落盘） |

---

## 一、实锤（复现过）

### 1. 「重新扫描」按钮第二次起就彻底失效，切 Profile 也不重扫

- 位置：`src/SliderSorter.Wpf/ViewModels/MainViewModel.cs:664`（`SelectedBodySlide = pick;`）
- 链条：`DetectBodySlideAsync` 只做「探测 → 赋 `SelectedBodySlide`」，而唯一的扫描入口是
  `OnSelectedBodySlideChanged`（:669-679）。`BodySlideCandidate` 是位置 record（`Core/BodySlideLocator.cs:27`），
  `[ObservableProperty]` 生成的 setter 用 `EqualityComparer<T>.Default` 比较 → 值相等的赋值**不发通知、不触发钩子**。
  再点一次按钮时 `previous = SelectedBodySlide?.AppDir`，`FindCandidates` 把 previousDir 作为第一条候选、
  Source 仍是「上次使用」（`BodySlideLocator.cs:59-60`），于是回来的 record 与当前值逐字段相等。
  （第一次点还算有效，因为原来那条的 Source 是「模组根目录」之类；从此以后永远相等。）
- 探针证据（真 VM，观察 `IsScanning` 变化次数 = 扫描执行次数）：
  | 动作 | PropertyChanged 次数 | 扫描次数 |
  |---|---|---|
  | 首次赋候选 | 1 | 1（IsScanning 变化 2） |
  | 赋一个值相等的候选 | 1（没涨） | 0 |
  | 赋一个换目录的候选 | 2 | 1 |
- 触发操作：装/删模组后点「重新扫描」（`GroupGenerationPage.xaml:93`、`OutputConflictPage.xaml:154`、设置页 :170 全绑同一个命令）；
  同一实例下切 Profile（`OnSelectedProfileChanged` → `LoadProfileMods` → `DetectBodySlideAsync`，BS 目录通常没变 → 不重扫）。
- 影响：树、冲突清单、"上次扫描"读数停在上一轮，而 `IsDetecting` 遮罩会闪一下，看着像"扫过了"。
  用户会拿着旧 Profile 的服装清单继续分组并写盘。
- 修法：把 `RunScanAsync()` 从属性钩子里挪出来，在探测完成后显式调用（钩子里只留"值真的变了"才做的那些事）。

### 2. 组列表一过滤，「当前组」就跳到别的组上

- 位置：`src/SliderSorter.Wpf/Views/Pages/GroupGenerationPage.xaml:239`（`ItemsSource="{Binding GroupsView}"` + `SelectedIndex="{Binding SelectedGroupIndex}"`）
  配 `src/SliderSorter.Wpf/ViewModels/MainViewModel.Tree.cs:436`（`Store.SelectGroup(Store.Groups[value].Name)`）
- 机理：`GroupsView` 是构造期建立在 `Groups` 上的**过滤投影**（`MainViewModel.cs:406-408`），
  `SelectedIndex` 是视图内下标；VM 却拿它去索引未过滤的 `Store.Groups`。`Selector.SelectedIndex` 默认双向绑定，
  所以视图会把这个下标推回 VM。反向也一样错：`RefreshGroupsList`（`Tree.cs:416`）用全量 `FindIndex` 写回下标。
- 探针证据：建 A-3BA / B-CBBE / C-UBE 三组，选中第三行后在过滤框输入 `C-`：
  `过滤后可见行: [C-UBE]`，`视图 SelectedIndex=0`，`VM.SelectedGroupIndex=0`，`Store.Current=A-3BA`（应为 C-UBE），`GroupInfo` 跟着说错话。
  **用户不必点行，光是打字就把当前组换掉了。**
- 影响：之后的重命名 / 删除 / 查看成员 / 「添加勾选」全部作用在另一个组上 —— 右键「删除分组」会删掉用户没打算动的组（有撤销，但要用户发现）。
- 现有用例为什么没挡住：`GroupListRenderTests.SelectionFollowsCurrentGroupAcrossRefresh` 只在**无过滤**时验证下标一致。
- 修法：改绑 `SelectedItem`（按 `GroupItem` 身份），或在下标进 VM 前做 视图下标 → 全量下标 的换算。

---

## 二、其它已确认的缺陷

### 3. 「导入现有组文件…」选完文件什么都不发生
`MainViewModel.Save.cs:431` 与 `:433` 都是 `_ = FilePicker?.Invoke(...)`，返回值被丢掉；`ImportFiles` 只有拖拽路径会调
（`Views/MainWindow.xaml.cs:76`，`FilePicker` 在 :36 赋成 `PickImportFile()`）。按钮弹完对话框即结束，无日志无提示。
修法：接住返回值 `if (f is not null) ImportFiles([f]);`。置信度：确认。

### 4. 点「复制」把整个程序弄崩
`Views/Pages/GroupGenerationPage.xaml.cs:131`、`Views/Pages/SettingsPage.xaml.cs:119` 裸调 `Clipboard.SetText`。
WPF 在剪贴板被别的进程占着时抛 `ExternalException`，而 `App.xaml.cs:60-66` 的可恢复白名单里没有它
→ `args.Handled` 保持 false → 进程按致命崩溃退出（还会再弹一个崩溃框）。
修法：try/catch 后走 `Notify.Warn`。置信度：确认（触发依赖剪贴板占用，远程桌面/剪贴板历史下常见）。

### 5. 3D 预览的缓存字典被两个后台线程同时读写
`Views/Pages/OutputConflictPage.xaml.cs:745-786`：`cts.Token` 只交给 `Task.Run`，任务体内从不 `ThrowIfCancellationRequested`；
`Task.Run` 的 token 只在委托**开始前**检查，已经跑起来的解码停不掉。于是快速连点两行候选（或选完一行立刻换身体下拉）
会有两条线程池线程并发 `TryGetValue`/`Add`/`Clear` 普通的 `_meshCache`（:624）与 `_bodies`（:672），
而 `_textures` 还在 :186 被 UI 线程整个换掉。修法：任务体内检查 token + 同一时刻只跑一份解码（或对两个字典上锁）。
置信度：确认（`PreviewTextureCache` 自己有锁，故只有这两个字典和 `_textures` 受影响）。

### 6. 扫描状态行的「模组 N」永远慢一轮
`MainViewModel.cs:848` 先设 `LastScanAt`，`OnLastScanAtChanged`（:211-216）当场重算 `ScanSummaryText`，
而它取的 `StatusCountsShort`（:245）数的是**旧** `TreeRoots`；`RefreshTree()` 在 :872 才跑，跑完没人再发这条通知。
表现：首扫显示「模组 0」，之后显示上一轮的数字。修法：`RebuildTree` 末尾（`UpdateCounts()` 旁）补一次
`OnPropertyChanged(nameof(ScanSummaryText))`。置信度：确认。

### 7. 新建 / 重命名组后，未保存标志与标题栏不动
`Tree.cs:459`、`:482` 成功后只调 `RefreshGroupsList`（:402-422），其中没有 `UpdateTitle()`；而 `IsDirty`、
`WindowTitle`、`SaveButtonLabel` 只由 `UpdateTitle`（:393-397）刷新。关窗时靠 `Store.Dirty` 兜底所以不丢数据，
但界面全程不说"有未保存改动"。修法：`RefreshGroupsList` 末尾补 `UpdateTitle()`。置信度：确认。

### 8. 大小写口径两套，改过一次大小写后规则静默失效
`Core/GroupStore.cs`：`Current`/`GetGroup`/`GroupNameExists` 用 `OrdinalIgnoreCase`（:23/:26/:48），
`RenameGroup`/`DeleteGroup`/`ApplyToGroup` 用 `Ordinal`（:85/:103/:140）。
把 `Armor` 重命名成 `ARMOR` 是允许的（`GroupNameExists` 排除了自身），之后 `ApplyToGroup("Armor", …)` 找不到组返回 -1，
规则预设一声不响地什么都不做，而调用方仍按 `GetGroup` 打出成员数，日志看着像成功了。
修法：组名查找统一到一个比较器，-1 要变成显式错误。置信度：确认。

### 9. 「发现新模组」弹窗按服装名去重，同名跨模组会被一起吞掉
`Views/NewModsWindow.xaml.cs:248-251`（`SelectedOutfits` 用 `Distinct(OutfitName)`）、`:201-207`（`RemoveApplied` 按名字剔）。
勾选状态本身是按 (分隔符, 模组, 服装) 记的（`_checked`），所以只要两个模组提供同名 set（正是输出冲突页存在的理由），
只勾其中一个，另一条也从待办里消失；若那是它所在模组的最后一条，整个模组行一起没了，全处理完还会自动关窗。
另外摘要「来自 M 个模组」（`CountOwners` :205-214）按名字匹配父节点，会多算。
修法：按 `OutfitKey` 精确剔除。置信度：确认。
顺带：`UpdateSummary()` 只在 `Rebuild()` 里调，抽屉里勾选不会刷新摘要（:179-184），一直是初始值直到用户动过滤框。

### 10. `OpenDirectory` 给 explorer 的路径不加引号
`MainViewModel.Save.cs:507` `Process.Start("explorer.exe", path)`。输出目录在 `Mod Organizer 2`、
`Steam library`、`Program Files` 这类含空格的路径下会被拆成多个参数，Explorer 打开错目录或退回「文档」。
触发点：保存成功框的「打开输出目录」（`SaveSuccessWindow.xaml.cs:36,43`）。
修法：`$"\"{path}\""` 或 `ProcessStartInfo { UseShellExecute = true }`。置信度：确认。

### 11. 设置文件是唯一没有"临时文件 + 替换"的写盘点
`Core/AppSettings.cs:132` 直接 `File.WriteAllText` 截断重写，`:113-124` 的 `Load()` 对任何异常返回全新默认对象，
`Save()` 的 catch 让失败也无声。写盘途中断电/磁盘满/杀软打断 → 下次启动静默丢掉全部设置，
里面包括用户在冲突页手定的赢家（`OutputChoices`）、实例/Profile/写盘模式。
修法：写 `.tmp` 后 `File.Replace`；`Load` 失败时保留坏文件并提示，而不是回默认。置信度：确认（窗口窄但代价是重配）。

### 12. 归档查找被推到所有层之后，与类注释相反
`Core/GameDataResolver.cs:36-52`：实现是「全部层的散文件 → 再查归档索引」，注释（:7-8）写的是「每层先看散文件、再看**该层**的归档」。
强层模组把贴图打进 `.bsa`、弱层模组留同名散文件时，预览显示的是弱层那张，与进游戏所见不一致 —— 恰好误导用户在冲突页选赢家。
修法：按层循环，层内先散文件再查该层归档。置信度：确认（只影响预览正确性）。

### 13. `OrdinalIgnoreCase` 与 BodySlide 的"只折 ASCII"并不等价，注释里那句断言是假的
`Core/OutputConflict.cs:87-88` 与 `Core/SliderSetScanner.cs:220-223` 都断言「`OrdinalIgnoreCase` 同样不动非 ASCII 字符」，
实测为假：`[string]::Equals('Ы','ы','OrdinalIgnoreCase')` → `True`，`É/é` 同样为 `True`。
于是两个只差西里尔/音标大小写的 set 名或输出路径，在这里会被合成一条，BodySlide 那边其实是两件衣服：
弱层那条整条从清单消失，其输出路径也不再参与冲突判定（或反过来把两组并成一组导出 choice）。
项目里带 Lang.ru，俄语命名不算冷门。修法：换只折 ASCII 的比较器，并改掉这两处注释。置信度：确认（差异存在），影响面视用户命名而定。

### 14. 带 DOCTYPE 的 set 文件整件作废
`Core/SliderSetScanner.cs:335` 的 `DtdProcessing.Prohibit` 对任何 `<!DOCTYPE>` 抛异常，`:458-462` 的 catch 里 `sets.Clear()`
→ 该文件一条服装都不贡献，只留一行警告；tinyxml2 会跳过 DTD 正常解析。用某些 XML 工具/导出器存过的 `.xml/.osp` 会踩到。
修法：`DtdProcessing.Ignore`（仍禁外部实体）。置信度：确认（机制）。

### 15. `modlist.txt` 用 `FileShare.Read` 打开
`Core/ModListParser.cs:21` 的 `File.ReadAllText` 默认 `FileShare.Read`，而同项目的 `Core/IniParser.cs:10` 专门为了
「MO2 运行中」用了 `FileShare.ReadWrite`。在 MO2 刚写完 modlist 时点扫描会 IOException。修法：与 IniParser 对齐。置信度：疑似（时序窗口）。

### 16. `backup` 过滤表达式过宽
`Core/ModListParser.cs:17` 的 `^.*backup[0-9]*$`（IgnoreCase）会丢掉**任何**以 backup 结尾的模组名，
`[0-9]*` 还允许零位数字；而 MO2 常见的 `X (Backup 1)` 反而匹配不上。被丢的模组静默消失，无警告。
修法：按 MO2 实际备份命名精确匹配。置信度：确认（正则过宽），是否命中取决于用户命名。

### 17. 一个坏 ini 能把启动打挂；实例可能重复登记
`Core/Mo2Discovery.cs:29/:85` 的去重用未归一化的原始字符串（`Mo2Instance.cs:26` 内部才 `GetFullPath().TrimEnd`），
带尾分隔符的手工目录会在列表里出现两条。`TryAdd`（:78-86）没有 try/catch，而 `Mo2Instance` 构造函数里的
`IniParser.ParseFile` 与 `Path.GetFullPath` 都能抛 —— 与同文件 :33-37 那段"这里抛出去等于启动即崩"的设计意图自相矛盾。
修法：`TryAdd` 内逐实例兜异常并降级登记；去重键归一化。置信度：疑似（依赖外部文件状态）。

### 18. 主树过滤会吞掉勾选
`ViewModels/MainViewModel.Tree.cs:70-113`：`RebuildTree` 整棵换新 `NodeVM`，勾选是节点上的状态，所以过滤框打字（防抖 350ms）
就把刚勾的一批清零，「添加勾选(N)」变 0。同一套代码在弹窗里是**保留**勾选的（`NewModsFilterTests.cs:106-110` 钉住了那条契约），
两处行为不一致。修法：重建前按 (模组, 服装) 捕获勾选集再回灌。置信度：确认（是否算缺陷取决于产品意图）。

### 19. 冲突页每点一个赢家就整表重建
`Views/Pages/OutputConflictPage.xaml.cs:361` 每次 `RebuildGroups` 都换掉 `GroupList.ItemsSource`：
左栏滚动位置与视觉树每点一次全重来，还白跑一次 `ShowPreview` 解码。几百组冲突往下点时最难受。
修法：只更新对应行的状态。置信度：确认（性能/观感，非正确性）。

---

### 20. `@ByteArray` 里的 `\xHH` 按字节直投，非 ASCII 路径读成乱码
`Core/IniParser.cs` 的 Qt 转义解码把每个 `\xHH` 直接 `(char)b` 塞进结果，等于按 Latin-1 解。
而 `@ByteArray` 里装的是**字节**，Qt 对非 ASCII 一律写 `\xHH`：一个中文字是三个字节（它的 UTF-8）。
本机这份真实 ini 是纯 ASCII（`gamePath=@ByteArray(E:\\Skyrim AE\\Skyrim Special Edition)`）所以没暴露；
游戏目录带中文/俄文的用户一踩就是：`GamePath` 变乱码 → `Directory.Exists` 全失败 → 模组一个都扫不到，
而诊断报告里显示的还是那个错路径。修法：整段先收成字节、末尾按 UTF-8 解，解不动才退回逐字节直投。
置信度：机制确认，触发取决于用户的游戏目录名。

## 三、看过但没采信的三条

- 「`PreviewTextureCache` 的 `_order` 会重复增长、LRU 失效」：不成立。`_cached` 与 `_order` 成对增删，
  `Touch()`（`Services/PreviewTextureCache.cs:108-115`）是 Remove+Add 不会留重复项，容量也确实封顶在 32。
- 「本地化缺键 / 占位符与参数不匹配」：四份 `Lang.*.xaml` 与代码用到的键比对下来没有缺失。
- 「还有后台线程用 `Application.Current.Dispatcher`」：日志那次修复之后没再找到残留，`.Wait()/.Result` 也没有。

## 四、建议的动手顺序

1、2、3 是"用户按了却没发生 / 发生在错的对象上"这一类，最该先修；其中 1 和 2 修完各补一条钉住行为的用例
（1 钉「点两次按钮要扫两次」，2 钉「过滤状态下点第一行选中的就是可见的那一行」）。
4、5、10 是会崩或崩相邻功能的，成本都很低，可以顺手一起。8、9、13 属于"静默做错事"，
修法明确但要先定口径（组名到底按哪种大小写规则、同名跨模组在待办里怎么算）。
