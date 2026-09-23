#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""校验手写 ResourceDictionary i18n 的键一致性与引用完整性。

用法（仓库根目录下执行）：
    python tools/verify_l10n.py src/BSGroupGenerator.Wpf
    python tools/verify_l10n.py --root src/BSGroupGenerator.Wpf   # 两种写法等价

检查项：
    1. 各 Lang.*.xaml 的键集合是否完全一致（多语言项目最易漏的地方）
    2. 每个文件内部是否有重复键（XAML 重复 x:Key 会在加载时抛异常）
    3. 源码/XAML 引用的键是否都在语言文件里存在（拼错键名时界面会显示键名本身）
    4. 定义了但从未被静态引用的键（提示用，可能有动态引用）
    5. 指定「已删除成员」是否有残留引用（改名/删代码后最常见的漏网之鱼）
    6. 资源值里的格式化占位符是否合法、且各语言索引一致
       （不配对的花括号会让 string.Format 抛 FormatException；某语言多出占位符
        而调用点没传对应实参时同样会抛）
    7. Core 层（无 UI 依赖的类库）的字符串字面量里不得出现中文
       （Core 经 CoreStrings.Localizer 取词；留中文字面量 = en/ru/fr 下日志与诊断报告混中文）
    8. 每个 L.Menu_* 的值都必须带访问键 `(_X)`，且同一层菜单内不重复
       （只有中文有访问键 = 键盘用户在其他三种语言下无法用 Alt 序列操作）

为什么需要它：这类 i18n 方案没有编译期校验——键名写错、语言文件漏键、删代码留引用，
全都要么静默显示键名、要么运行时才炸。CI 跑一次这个脚本能全部拦住。

用法补充：`root` 指向 WPF 项目时，脚本会自动识别同级的 Core 项目（`../<CoreName>`）
并把它的源码纳入引用扫描——否则 Core 用的那批 L.Core_* 键会被误报成「孤儿键」。
"""

import io
import os
import re
import sys

# 输出含中文；Windows 上被重定向（CI 捕获输出）时 stdout 会退回本地代码页编码，
# 打印中文直接抛 UnicodeEncodeError。这里显式钉住 UTF-8，避免脚本因输出编码而"失败"。
try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

# 引用键的模式：C# 字符串字面量 "L.Xxx" / XAML {DynamicResource L.Xxx}
CS_KEY = re.compile(r'"(L\.[A-Za-z0-9_]+)"')
XAML_KEY = re.compile(r'Resource\s+(L\.[A-Za-z0-9_]+)')

# 资源值里的格式化占位符：{0} / {1:格式}
PLACEHOLDER = re.compile(r"\{(\d+)(?::[^{}]*)?\}")
# 取 x:Key 与元素文本（标签内可能还有 xml:space 等属性）
KV = re.compile(r'x:Key="([^"]+)"[^>]*>(.*?)</sys:String>', re.DOTALL)

# 访问键约定：`标签(_X)` —— 与既有中文语言文件一致
ACCESS_KEY = re.compile(r"\(_(.)\)")
# 菜单项：Header 走 DynamicResource 的 L.Menu_*
MENU_ITEM = re.compile(r"<(/?)MenuItem\b([^>]*?)(/?)>", re.DOTALL)
MENU_HEADER = re.compile(r'Header="\{DynamicResource\s+(L\.Menu_[A-Za-z0-9_]+)\}"')
MENU_OPEN = re.compile(r"<Menu[\s>]")

CJK = re.compile(r"[\u3400-\u9fff\uf900-\ufaff\U00020000-\U0002ffff]")

# Core 项目名（与 WPF 项目同级）；找不到就跳过第 7 节
CORE_PROJECT_NAME = "BSGroupGenerator"

SKIP_DIRS = {"obj", "bin", ".git", ".vs", "node_modules", "dist"}
SRC_EXT = (".cs", ".xaml")



def read(path):
    return io.open(path, encoding="utf-8-sig", errors="replace").read()


def find_lang_files(root):
    """找 **/Strings/Lang.*.xaml，找不到则退化为任意 Lang.*.xaml。"""
    hits = []
    for dirpath, dirnames, files in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for f in files:
            if f.startswith("Lang.") and f.endswith(".xaml") and os.path.basename(dirpath) == "Strings":
                hits.append(os.path.join(dirpath, f))
    if not hits:
        for dirpath, dirnames, files in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for f in files:
                if f.startswith("Lang.") and f.endswith(".xaml"):
                    hits.append(os.path.join(dirpath, f))
    return sorted(hits)


def keys_of(path):
    return re.findall(r'x:Key="([^"]+)"', read(path))


def values_of(path):
    """返回 [(key, 元素文本)]，供格式串校验使用。"""
    return [(m.group(1), m.group(2)) for m in KV.finditer(read(path))]


def scan_format(value):
    """扫描资源值里的格式化占位符。

    返回 (占位符索引集合, 错误说明或 None)。识别 {{ }} 转义。
    这类值会经 L10n.TrF -> string.Format 使用，值里出现不配对的花括号
    会在运行时抛 FormatException——而 TrF 常在异常处理器里被调用，
    届时会直接杀掉进程，所以必须在静态阶段拦下。
    """
    idxs = set()
    i, n = 0, len(value)
    while i < n:
        c = value[i]
        if c == "{":
            if i + 1 < n and value[i + 1] == "{":
                i += 2
                continue
            m = PLACEHOLDER.match(value, i)
            if not m:
                return idxs, f"第 {i} 个字符处有未配对或非法的 '{{'"
            idxs.add(int(m.group(1)))
            i = m.end()
        elif c == "}":
            if i + 1 < n and value[i + 1] == "}":
                i += 2
                continue
            return idxs, f"第 {i} 个字符处有未配对的 '}}'"
        else:
            i += 1
    return idxs, None


def is_lang_file(path):
    base = os.path.basename(path)
    return base.startswith("Lang.") and base.endswith(".xaml")


def iter_source_files(root):
    """产出「会引用键」的源文件。

    必须排除语言文件自身：它们的 `x:Key="L.K1"` 会被引用正则误判成一次引用，
    等于每个键都"自我引用"，第 4 节（孤儿键）将恒为空、完全失去意义。
    """
    for dirpath, dirnames, files in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for f in files:
            if f.endswith(SRC_EXT):
                path = os.path.join(dirpath, f)
                if not is_lang_file(path):
                    yield path


def find_core_project(root):
    """WPF 项目同级的 Core 项目目录（无 UI 依赖、经 CoreStrings 取词）。

    不把 Core 纳入引用扫描，L.Core_* 这一整批键都会落进「孤儿键」提示——
    提示里混进真孤儿就没人看了。找不到时返回 None（单项目仓库照常可用）。
    """
    sibling = os.path.join(os.path.dirname(os.path.abspath(root)), CORE_PROJECT_NAME)
    return sibling if os.path.isdir(sibling) else None


def csharp_string_literals(text):
    """产出 C# 源码里的 (行号, 字面量内容)，跳过注释。

    第 7 节只能看字符串字面量：Core 里的中文注释是给维护者看的，不算"界面文案泄漏"，
    直接对整份文件搜中文会满屏误报，规则就没人信了。因此这里做一个最小词法扫描：
    注释（// 与 /* */）先跳过，再逐字处理普通 / 逐字(@) / 插值($) 字符串与字符字面量。
    注释里的引号因此不会破坏字符串配对（先撞上 // 就先跳到行尾）。
    """
    out = []
    i, n, line = 0, len(text), 1
    while i < n:
        c = text[i]
        if c == "\n":
            line += 1
            i += 1
        elif c == "/" and i + 1 < n and text[i + 1] == "/":
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif c == "/" and i + 1 < n and text[i + 1] == "*":
            j = text.find("*/", i + 2)
            if j < 0:
                break
            line += text.count("\n", i, j)
            i = j + 2
        elif c == "@" and i + 1 < n and text[i + 1] == '"':
            buf, j = [], i + 2
            while j < n:
                if text[j] == '"':
                    if j + 1 < n and text[j + 1] == '"':
                        buf.append('"')
                        j += 2
                        continue
                    break
                if text[j] == "\n":
                    line += 1
                buf.append(text[j])
                j += 1
            out.append((line, "".join(buf)))
            i = j + 1
        elif c == "$":
            j = i + 1
            if j < n and text[j] == "@":
                j += 1
            if j < n and text[j] == '"':
                verbatim = j > i + 1
                buf, j = [], j + 1
                while j < n:
                    if text[j] == '"':
                        if verbatim and j + 1 < n and text[j + 1] == '"':
                            buf.append('"')
                            j += 2
                            continue
                        break
                    if text[j] == "\\" and not verbatim and j + 1 < n:
                        buf.append(text[j + 1])
                        j += 2
                        continue
                    if text[j] == "\n":
                        line += 1
                    buf.append(text[j])
                    j += 1
                out.append((line, "".join(buf)))
                i = j + 1
            else:
                i += 1
        elif c == '"':
            buf, j = [], i + 1
            while j < n and text[j] != '"':
                if text[j] == "\\" and j + 1 < n:
                    buf.append(text[j + 1])
                    j += 2
                    continue
                if text[j] == "\n":
                    line += 1
                buf.append(text[j])
                j += 1
            out.append((line, "".join(buf)))
            i = j + 1
        elif c == "'":
            buf, j = [], i + 1
            while j < n and text[j] != "'":
                if text[j] == "\\" and j + 1 < n:
                    buf.append(text[j + 1])
                    j += 2
                    continue
                buf.append(text[j])
                j += 1
            out.append((line, "".join(buf)))
            i = j + 1
        else:
            i += 1
    return out


def menu_structure(xaml_text):
    """从主窗口 XAML 里抽出菜单层级 → [(键, 深度)]。

    访问键冲突是"同层"才成立：文件菜单的 `(_S)` 与工具菜单的 `(_S)` 互不影响。
    所以不能只把语言文件里所有 L.Menu_* 凑一堆比大小写，必须还原层级。
    """
    start = MENU_OPEN.search(xaml_text)
    if not start:
        return []
    end = xaml_text.find("</Menu>", start.end())
    if end < 0:
        return []
    seg = xaml_text[start.start():end]
    stack, out = [], []
    for m in MENU_ITEM.finditer(seg):
        closing, attrs, self_close = m.group(1), m.group(2), m.group(3)
        if closing:
            if stack:
                stack.pop()
            continue
        hm = MENU_HEADER.search(attrs)
        if hm:
            out.append((hm.group(1), len(stack)))
        if not self_close:
            stack.append(True)
    return out


def parse_root(argv):
    """接受 `<根目录>` 或 `--root <根目录>` / `-r <根目录>`。

    只支持位置参数时，误传 --root 会让脚本去遍历一个名为 "--root" 的目录，
    然后报"找不到 Lang.*.xaml"——把参数错误伪装成项目结构问题。
    """
    if not argv:
        return None
    if argv[0] in ("--root", "-r"):
        return argv[1] if len(argv) > 1 else None
    return argv[0]


def main():
    root = parse_root(sys.argv[1:])
    if root is None:
        print(__doc__)
        return 2
    if not os.path.isdir(root):
        print(f"项目根目录不存在或不是目录：{root}")
        return 2
    langs = find_lang_files(root)
    if not langs:
        print("找不到 Lang.*.xaml，请确认项目结构（预期 **/Strings/Lang.<lang>.xaml）")
        return 2

    print("== 1. 语言文件 ==")
    key_sets = {}
    for path in langs:
        ks = keys_of(path)
        dupes = sorted({k for k in ks if ks.count(k) > 1})
        key_sets[os.path.basename(path)] = set(ks)
        flag = f"  !! 重复键: {dupes}" if dupes else ""
        print(f"  {os.path.relpath(path, root)}: {len(ks)} 键{flag}")

    print("\n== 2. 各语言键集合差异 ==")
    names = sorted(key_sets)
    baseline = key_sets[names[0]]
    any_diff = False
    for name in names[1:]:
        only_a = sorted(baseline - key_sets[name])
        only_b = sorted(key_sets[name] - baseline)
        if only_a or only_b:
            any_diff = True
            print(f"  {names[0]} vs {name}:")
            if only_a:
                print(f"    仅 {names[0]} 有: {only_a}")
            if only_b:
                print(f"    仅 {name} 有: {only_b}")
    if not any_diff:
        print(f"  一致：{len(baseline)} 个键在所有语言文件中都存在")

    all_keys = set().union(*key_sets.values())

    # Core 项目也引用键（经 CoreStrings.Get/Format），且用的是同一套 "L.Xxx" 字面量，
    # 所以直接把它并进引用扫描即可；否则 L.Core_* 会被当成孤儿键。
    core_root = find_core_project(root)
    scan_roots = [root] + ([core_root] if core_root else [])
    print(f"\n  （引用扫描范围：{', '.join(os.path.relpath(r, os.path.dirname(os.path.abspath(root))) for r in scan_roots)}）")

    print("\n== 3. 被引用但缺失的键 ==")
    referenced = {}
    for scan_root in scan_roots:
        for path in iter_source_files(scan_root):
            text = read(path)
            for m in list(CS_KEY.findall(text)) + list(XAML_KEY.findall(text)):
                referenced.setdefault(m, set()).add(os.path.relpath(path, root))
    missing = {k: v for k, v in referenced.items() if k not in all_keys}
    if missing:
        for k, v in sorted(missing.items()):
            print(f"  [缺失] {k}  <- {sorted(v)}")
    else:
        print(f"  无（共引用 {len(referenced)} 个键，全部存在）")

    print("\n== 4. 定义但未被静态引用（仅供参考，可能有动态引用）==")
    unused = sorted(k for k in all_keys if k not in referenced)
    print("  " + (", ".join(unused) if unused else "无"))

    print("\n== 5. 已删除成员残留引用 ==")
    # 该表按项目维护：删除某个成员/键时把名字加进来，之后就能自动发现残留引用。
    patterns = {
        "GroupRules.Matches": r"GroupRules\.Matches\b",
        "Rebadge": r"\bRebadge\b",
        "RebuildHeaderBase": r"\bRebuildHeaderBase\b",
        "DescribeKind": r"\bDescribeKind\b",
        "L.Tree_InGroupPrefix": r"L\.Tree_InGroupPrefix",
        "ConflictNames": r"\bConflictNames\b",
        # 2026-09-15 UI 改版：右侧面板按钮墙拆成中间搬运栏 + 上下文菜单，撤销改挂菜单
        "L.Main_AddToGroup": r"L\.Main_AddToGroup\b",
        "L.Main_RemoveFromGroup": r"L\.Main_RemoveFromGroup\b",
        # L.Main_Undo 曾在这里（撤销改挂右键菜单后按钮上的文案没了）；2026-09-22 又把撤销
        # 提回常驻按钮，键复活，于是从"已删除成员"里移除——留在表里会让门禁把正常引用报成残留。
        "L.Main_ImportGroups": r"L\.Main_ImportGroups\b",
        # 2026-09-22 三页改版：主题/语言/帮助那几个顶层菜单项并进「设置」页后删掉。
        "L.Menu_Theme": r"L\.Menu_Theme\b",
        "L.Menu_Language": r"L\.Menu_Language\b",
        "L.Menu_Help": r"L\.Menu_Help\b",
        "L.Menu_Manual": r"L\.Menu_Manual\b",
        "L.Menu_CheckUpdate": r"L\.Menu_CheckUpdate\b",
        "L.Menu_About": r"L\.Menu_About\b",
        "L.Menu_Diagnostics": r"L\.Menu_Diagnostics\b",
        # 这两句原本是 HelpWindow / AboutWindow 的窗口标题；正文与关于内嵌进设置页后没有标题可用了
        "L.Help_Title": r"L\.Help_Title\b",
        "L.About_Title": r"L\.About_Title\b",
        # 2026-09-22 菜单栏整条取消：剩下的每一项要么与页内控件重复（保存＝右下角那颗、
        # 退出＝关窗口），要么本身就该是个页面（输出冲突、规则预设、设置）。
        # L.Menu_ImportGroups 与 L.Menu_Undo 活着——它们搬进了组管理的右键菜单，仍是菜单项。
        "L.Menu_File": r"L\.Menu_File\b",
        "L.Menu_Save": r"L\.Menu_Save\b",
        "L.Menu_Edit": r"L\.Menu_Edit\b",
        "L.Menu_Tools": r"L\.Menu_Tools\b",
        "L.Menu_RulePresets": r"L\.Menu_RulePresets\b",
        "L.Menu_OutputConflicts": r"L\.Menu_OutputConflicts\b",
        "L.Menu_CleanupStale": r"L\.Menu_CleanupStale\b",
        "L.Menu_Settings": r"L\.Menu_Settings\b",
        "L.Menu_Exit": r"L\.Menu_Exit\b",
        # 分组生成页不再复述环境摘要（要改就去设置标签页），"去设置修改"那颗按钮随之取消
        "L.Settings_GoTo": r"L\.Settings_GoTo\b",
        # 2026-09-22 冲突页重排：页标题改用 L.Tab_Conflicts（与标签头同键，设置页同理），
        # 那句"输出冲突：同一个服装文件由谁生成"随之退休。
        "L.Conflict_Title": r"L\.Conflict_Title\b",
        # 2026-09-23 改名：这颗按钮要的是「实例目录」，不是「MO2 目录」。登记判据只有
        # ModOrganizer.ini，MO2 的程序目录（只有 exe）从来不被接受，旧名会把人引到程序目录去。
        "L.Main_AddMo2Dir": r"L\.Main_AddMo2Dir\b",
    }
    found = False
    for scan_root in scan_roots:
        for path in iter_source_files(scan_root):
            for i, line in enumerate(read(path).splitlines(), 1):
                for label, pat in patterns.items():
                    if re.search(pat, line):
                        print(f"  [残留] {label}: {os.path.relpath(path, root)}:{i}: {line.strip()[:100]}")
                        found = True
    if not found:
        print("  干净")

    print("\n== 6. 格式串占位符 ==")
    bad_fmt, per_key = [], {}
    for path in langs:
        lang = os.path.basename(path)
        for key, val in values_of(path):
            idxs, err = scan_format(val)
            per_key.setdefault(key, {})[lang] = idxs
            if err:
                bad_fmt.append(f"{lang} {key}: {err}")

    # 各语言的占位符索引必须一致：某语言多出占位符而调用点未传对应实参时，
    # string.Format 会直接抛 FormatException。
    mismatch = []
    for key, d in per_key.items():
        if len({frozenset(s) for s in d.values()}) > 1:
            mismatch.append((key, d))

    if bad_fmt:
        for e in bad_fmt:
            print(f"  [非法] {e}")
    if mismatch:
        for key, d in sorted(mismatch):
            shown = ", ".join(f"{k}={{{','.join(str(i) for i in sorted(v))}}}"
                              for k, v in sorted(d.items()))
            print(f"  [不一致] {key}: {shown}")
    if not bad_fmt and not mismatch:
        print(f"  全部合法且各语言一致（校验 {len(per_key)} 个值）")

    print("\n== 7. Core 层中文字面量 ==")
    core_leaks = []
    if core_root is None:
        print(f"  跳过：未找到同级 Core 项目（{CORE_PROJECT_NAME}）")
    else:
        for dirpath, dirnames, files in os.walk(core_root):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for f in files:
                if not f.endswith(".cs"):
                    continue
                path = os.path.join(dirpath, f)
                for line, lit in csharp_string_literals(read(path)):
                    if CJK.search(lit):
                        core_leaks.append(
                            f"{os.path.relpath(path, root)}:{line}: {lit.strip()[:90]}")
        if core_leaks:
            for e in core_leaks:
                print(f"  [中文] {e}")
        else:
            print("  干净：Core 的字符串字面量中没有中文（文案一律走 CoreStrings.Get/Format）")

    print("\n== 8. 菜单访问键 ==")
    menu_problems = []
    for path in langs:
        lang = os.path.basename(path)
        vals = {k: v for k, v in values_of(path)}
        for key in sorted(k for k in vals if k.startswith("L.Menu_")):
            if not ACCESS_KEY.search(vals[key]):
                menu_problems.append(f"{lang} {key}: 缺少访问键，值 = {vals[key]!r}")

    # 同层冲突：只有同一个菜单下的兄弟项才互相影响，所以层级必须从 XAML 还原。
    main_xaml = os.path.join(root, "Views", "MainWindow.xaml")
    if not os.path.isfile(main_xaml):
        print("  跳过同层冲突检查：找不到 Views/MainWindow.xaml")
    else:
        struct = menu_structure(read(main_xaml))
        if not struct:
            print("  跳过同层冲突检查：未能从 MainWindow.xaml 解析出菜单结构")
        else:
            children = {}
            for idx, (key, depth) in enumerate(struct):
                if depth == 0:
                    continue
                parent = next((k for k, d in reversed(struct[:idx]) if d == depth - 1), "?")
                children.setdefault(parent, []).append(key)
            for path in langs:
                lang = os.path.basename(path)
                vals = {k: v for k, v in values_of(path)}
                for parent, kids in children.items():
                    seen = {}
                    for kid in kids:
                        m = ACCESS_KEY.search(vals.get(kid, ""))
                        if not m:
                            continue  # 缺访问键已在上面报过
                        ch = m.group(1).upper()
                        if ch in seen:
                            menu_problems.append(
                                f"{lang} {parent} 下 {seen[ch]} 与 {kid} 都用访问键 ({ch})")
                        else:
                            seen[ch] = kid
    if menu_problems:
        for e in menu_problems:
            print(f"  [问题] {e}")
    else:
        print("  全部 L.Menu_* 都带访问键，且同层不冲突")

    return 1 if (missing or any_diff or bad_fmt or mismatch or core_leaks or menu_problems) else 0


if __name__ == "__main__":
    sys.exit(main())
