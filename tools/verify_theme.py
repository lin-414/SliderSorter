#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""主题接线检查（CI 门禁）。

挡住的是「色板里有这个键，但控件根本没拿到它」这一类问题——色板正确 ≠ 界面正确。
起因是一次真实回归：MainWindow.xaml 去掉了根元素上的 Background，以为
Themes/Controls.xaml 里 TargetType="Window" 的隐式样式会兜住；实际隐式样式按元素
**确切类型**匹配，对 x:Class 生成的派生窗口（MainWindow 及全部对话框）根本不生效，
于是主窗口退回系统默认白底，暗色主题下整窗发白。

检查项：
  §1 每个 Views/**/*.xaml 的根 <Window> 是否显式套用了 AppWindow 样式（否则前景/字体/渲染选项全丢）
  §2 每个根 <Window> 是否显式声明了 Background（兜底；本地值优先于样式 setter）
  §3 两套色板的 B.* 键集合是否完全一致（少一个键 → 某个主题下 DynamicResource 静默解析为空）
  §4 XAML 里引用的每个 B.* 键是否在两套色板里都存在
  §5 每个 B.* 画刷引用的 C.* 颜色是否真的定义了
  §6 Views/Pages 下的每份页面是否真的被壳（Views/MainWindow.xaml）引用
  §7 各资源字典之间的 x:Key 是否重名（后合并的会静默覆盖先合并的）

§1/§2 只作用于根 <Window>：Views/Pages 下的页面根是 <UserControl>，背景/前景/字体由壳继承而来，
套窗口样式既无对应 TargetType 也无意义。但扫描必须是**递归**的——以前只扫 Views/*.xaml，
页面放进 Views/Pages/ 会被静默跳过，等于新增界面悄悄退出门禁；那比误报严重得多。

用法：python tools/verify_theme.py [仓库根目录]
退出码 0 = 全部通过，1 = 有命中。
"""

from __future__ import annotations

import glob
import os
import re
import sys

try:  # Windows 上重定向到管道/文件时，按本地代码页编码会抛异常
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

BRUSH_KEY = re.compile(r'x:Key="(B\.[A-Za-z0-9_]+)"')
COLOR_KEY = re.compile(r'x:Key="(C\.[A-Za-z0-9_]+)"')
BRUSH_COLOR_REF = re.compile(
    r'<SolidColorBrush\s+x:Key="(B\.[A-Za-z0-9_]+)"\s+Color="\{StaticResource\s+(C\.[A-Za-z0-9_]+)\}"'
)
ANY_RESOURCE_REF = re.compile(r'\{(?:Dynamic|Static)Resource\s+(B\.[A-Za-z0-9_]+)\}')
COMMENT = re.compile(r"<!--.*?-->", re.S)

APP_WINDOW = 'Style="{StaticResource AppWindow}"'

# 页面根：不是窗口，由壳提供背景/前景/字体，因此 §1/§2 对它们不适用。
PAGE_ROOTS = ("UserControl", "Page")


def find_repo_root() -> str | None:
    """从脚本位置向上找含 src/BSGroupGenerator.Wpf/Views 的目录。"""
    here = os.path.dirname(os.path.abspath(__file__))
    cur = here
    for _ in range(8):
        if os.path.isdir(os.path.join(cur, "src", "BSGroupGenerator.Wpf", "Views")):
            return cur
        parent = os.path.dirname(cur)
        if parent == cur:
            break
        cur = parent
    return None


def read(path: str) -> str:
    with open(path, encoding="utf-8") as f:
        return COMMENT.sub("", f.read())


ROOT_START = re.compile(r"<([A-Za-z_][\w.]*)")


def root_open_tag(text: str) -> tuple[str | None, str | None]:
    """根元素的（名字, 完整开标签）。属性可跨行，引号内的 > 不算结束；
    注释已由 read() 剥掉，`<?xml?>` 处理指令被 ROOT_START 排除。"""
    for m in ROOT_START.finditer(text):
        start = m.start()
        quote = None
        i = start
        while i < len(text):
            ch = text[i]
            if quote:
                if ch == quote:
                    quote = None
            elif ch in "\"'":
                quote = ch
            elif ch == ">":
                return m.group(1), text[start : i + 1]
            i += 1
    return None, None


def main() -> int:
    repo = sys.argv[1] if len(sys.argv) > 1 else find_repo_root()
    if not repo:
        print("找不到仓库根目录（预期存在 src/BSGroupGenerator.Wpf/Views）。")
        print("请显式传入：python tools/verify_theme.py <仓库根目录>")
        return 2

    wpf = os.path.join(repo, "src", "BSGroupGenerator.Wpf")
    views = sorted(glob.glob(os.path.join(wpf, "Views", "**", "*.xaml"), recursive=True))
    themes = os.path.join(wpf, "Themes")
    light_path = os.path.join(themes, "Palette.Light.xaml")
    bout_path = os.path.join(themes, "Palette.Boutique.xaml")
    for p in (light_path, bout_path):
        if not os.path.isfile(p):
            print(f"缺少色板文件：{p}")
            return 2

    failures = 0

    # ── §1 / §2 根窗口必须显式套样式、显式给背景 ──
    print("§1 根 <Window> 是否显式套用 AppWindow 样式")
    print("§2 根 <Window> 是否显式声明 Background")
    missing_style, missing_bg, odd_root = [], [], []
    win_n = page_n = 0
    for path in views:
        name = os.path.relpath(path, wpf)
        tag_name, tag = root_open_tag(read(path))
        if tag_name is None:
            odd_root.append((name, "找不到根元素开标签"))
        elif tag_name in PAGE_ROOTS:
            page_n += 1  # 页面壳：背景/前景/字体由宿主窗口继承，§1/§2 不适用
        elif tag_name == "Window":
            win_n += 1
            if APP_WINDOW not in tag:
                missing_style.append((name, "缺 " + APP_WINDOW))
            if not re.search(r'\bBackground="', tag):
                missing_bg.append((name, "缺 Background="))
        else:
            odd_root.append((name, f"根元素是 <{tag_name}>，既不是 Window 也不是页面"))
    for name, why in missing_style:
        print(f"  [命中] §1 {name}: {why}")
    for name, why in missing_bg:
        print(f"  [命中] §2 {name}: {why}")
    for name, why in odd_root:
        print(f"  [命中] §1/§2 {name}: {why}")
    if not missing_style:
        print(f"  通过（{win_n} 个窗口全部显式套用；另有 {page_n} 份页面根为 {'/'.join(sorted(PAGE_ROOTS))}，不适用）")
    if not missing_bg:
        print(f"  通过（{win_n} 个窗口全部显式声明）")
    failures += len(missing_style) + len(missing_bg) + len(odd_root)

    # ── §3 两套色板键集合一致 ──
    print("\n§3 两套色板 B.* 键集合是否一致")
    light_txt, bout_txt = read(light_path), read(bout_path)
    light_keys = set(BRUSH_KEY.findall(light_txt))
    bout_keys = set(BRUSH_KEY.findall(bout_txt))
    only_light = sorted(light_keys - bout_keys)
    only_bout = sorted(bout_keys - light_keys)
    if only_light or only_bout:
        for k in only_light:
            print(f"  [命中] {k} 只在 Palette.Light.xaml 里有")
        for k in only_bout:
            print(f"  [命中] {k} 只在 Palette.Boutique.xaml 里有")
        failures += len(only_light) + len(only_bout)
    else:
        print(f"  通过（两套各 {len(light_keys)} 个 B.* 键，集合相同）")

    # ── §4 XAML 引用的键两套都要有 ──
    print("\n§4 XAML 引用的 B.* 键是否两套色板都有")
    used: dict[str, set[str]] = {}
    for path in sorted(glob.glob(os.path.join(wpf, "**", "*.xaml"), recursive=True)):
        rel = os.path.relpath(path, wpf)
        for key in ANY_RESOURCE_REF.findall(read(path)):
            used.setdefault(key, set()).add(rel)
    bad_refs = 0
    for key in sorted(used):
        miss = []
        if key not in light_keys:
            miss.append("Light")
        if key not in bout_keys:
            miss.append("Boutique")
        if miss:
            bad_refs += 1
            where = ", ".join(sorted(used[key]))
            print(f"  [命中] {key} 在 {'/'.join(miss)} 里不存在；引用自 {where}")
    if not bad_refs:
        print(f"  通过（{len(used)} 个被引用的键，两套色板均存在）")
    failures += bad_refs

    # ── §5 B.* 画刷引用的 C.* 颜色必须存在 ──
    print("\n§5 B.* 画刷引用的 C.* 颜色是否已定义")
    bad_color = 0
    for label, txt in (("Palette.Light.xaml", light_txt), ("Palette.Boutique.xaml", bout_txt)):
        colors = set(COLOR_KEY.findall(txt))
        for brush, color in BRUSH_COLOR_REF.findall(txt):
            if color not in colors:
                bad_color += 1
                print(f"  [命中] {label}: {brush} 引用未定义的 {color}")
    if not bad_color:
        print("  通过（所有画刷的 C.* 引用均已定义）")
    failures += bad_color

    # ── §6 页面必须被壳引用 ──
    # 挡的是"页面写了却没接进 TabControl"：这种文件编译得过、门禁查得到，运行时却永远不出现。
    print("\n§6 Views/Pages 下的页面是否被 Views/MainWindow.xaml 引用")
    shell_path = os.path.join(wpf, "Views", "MainWindow.xaml")
    pages = sorted(glob.glob(os.path.join(wpf, "Views", "Pages", "*.xaml")))
    if not pages:
        print("  跳过：Views/Pages 下暂无页面")
    elif not os.path.isfile(shell_path):
        print("  [命中] 找不到壳 Views/MainWindow.xaml，无从判断页面是否被引用")
        failures += 1
    else:
        shell_txt = read(shell_path)  # 注释已剥掉：在注释里提一嘴不算接上
        orphans = [os.path.splitext(os.path.basename(p))[0] for p in pages
                   if os.path.splitext(os.path.basename(p))[0] not in shell_txt]
        for stem in orphans:
            print(f"  [命中] Views/Pages/{stem}.xaml 未被壳引用（TabControl 里没有它）")
        if not orphans:
            print(f"  通过（{len(pages)} 份页面全部被壳引用）")
        failures += len(orphans)

    # ── §7 跨字典的 x:Key 不得重名 ──
    # 挡的是"后合并的字典静默覆盖先合并的"。
    #
    # ⚠️ 只查**同时驻留**的那对：Controls.xaml 与 Components.xaml。
    # 两套色板刻意定义同名 C.* / B.*（同一套语义、两套取值），而 ThemeManager 每次
    # 只把其中一套装进 Application（另一份被替换掉），所以它们同名是设计意图而非冲突。
    # 把色板也纳进来会得到五十多条"命中"，那只会把这条检查淹掉、逼后人加白名单。
    print("\n§7 Controls.xaml 与 Components.xaml 之间是否有重名 x:Key（后者会静默覆盖前者）")
    live_dicts = [
        os.path.join(wpf, "Themes", "Controls.xaml"),
        os.path.join(wpf, "Themes", "Components.xaml"),
    ]
    key_owner: dict[str, list[str]] = {}
    for path in live_dicts:
        if not os.path.isfile(path):
            continue
        # 注释已剥掉：注释里提到某个键名不算定义
        for key in re.findall(r'x:Key="([^"]+)"', read(path)):
            key_owner.setdefault(key, []).append(os.path.basename(path))
    clashes = {k: v for k, v in key_owner.items() if len(v) > 1}
    for key, owners in sorted(clashes.items()):
        print(f"  [命中] x:Key=\"{key}\" 在 {', '.join(owners)} 中都定义，后者会覆盖前者")
    if not clashes:
        print(f"  通过（{len(key_owner)} 个键名在两份常驻字典间互不重复）")
    failures += len(clashes)

    print()
    if failures:
        print(f"共 {failures} 处命中。")
        return 1
    print("全部通过。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
