#!/usr/bin/env python3
"""校验两套色板（Themes/Palette.Light.xaml / Palette.Boutique.xaml）的前景/背景对比度。

为什么要有这个脚本：颜色问题在代码评审里看不出来 —— 未勾选的复选框边框压底色只有
1.24:1，肉眼看代码完全正常，只有真的把两个色值算一遍才会暴露。把阈值固化成断言，
以后改色板就不会悄悄把可读性改坏。

判定依据 WCAG 2.1：
  · 正文/图标文字 1.4.3  → 相对比度 ≥ 4.5:1
  · UI 组件边界与图形 1.4.11 → ≥ 3:1

用法：
    python tools/verify_contrast.py [src/SliderSorter.Wpf]
退出码 0 = 全部通过；1 = 有组合不达标（CI 门禁）。
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

# ── 必须达标的组合：(前景, 背景, 最低比度, 说明) ───────────────────────────────
# 键名不带 C. 前缀；"#RRGGBB" 形式表示字面色（模板里硬编码的白色文字）。
# 每个组合会在两套色板上各算一次。
REQUIRED: list[tuple[str, str, float, str]] = [
    # 正文
    ("Text", "Window", 4.5, "窗口正文"),
    ("Text", "Panel", 4.5, "面板正文"),
    ("Text", "Card", 4.5, "卡片/列表正文"),
    ("Text", "Selected", 4.5, "列表与树选中行"),
    ("Text", "Hover", 4.5, "列表与树悬停行"),
    ("Text", "AccentSoft", 4.5, "文本选中态 / 勾选底纹"),
    ("TextDim", "Window", 4.5, "次要文字（窗口底）"),
    ("TextDim", "Panel", 4.5, "次要文字（面板底）"),
    ("TextDim", "Card", 4.5, "次要文字（卡片底）"),
    # 语义色
    ("AccentText", "Card", 4.5, "链接/强调文字"),
    ("AccentText", "Panel", 4.5, "链接/强调文字（面板底）"),
    # 「已在组内」徽标就是这一对：原先只测了 AccentText 压 Card/Panel，而它真正的底是 AccentSoft，
    # 暗色下那一对只有 3.96:1，徽标上的字发糊却没人报红。
    ("AccentText", "AccentSoft", 4.5, "强调徽标文字（强调底纹）"),
    ("Member", "Card", 4.5, "已在组内的标记"),
    ("Conflict", "Card", 4.5, "同名冲突标记"),
    ("Danger", "Card", 4.5, "危险/错误文字"),
    ("WarnText", "Card", 4.5, "警告文字"),
    ("WarnText", "WarnSoft", 4.5, "警告条上的警告文字"),
    ("WarnText", "Panel", 4.5, "警告文字（面板底）"),
    # 实心按钮：模板把前景固定为 White
    ("#FFFFFF", "Accent", 4.5, "主按钮文字"),
    ("#FFFFFF", "AccentHover", 4.5, "主按钮悬停文字"),
    ("#FFFFFF", "Warn", 4.5, "未保存态按钮文字"),
    ("#FFFFFF", "DangerSolid", 4.5, "危险按钮文字"),
    # UI 组件边界（1.4.11，阈值 3:1）
    ("CheckBorder", "Card", 3.0, "未勾选复选框边框（树/列表底）"),
    ("CheckBorder", "Window", 3.0, "未勾选复选框边框（窗口底）"),
    ("CheckBorder", "Panel", 3.0, "未勾选复选框边框（面板底）"),
    ("CheckMark", "AccentSoft", 3.0, "勾选对勾压勾选底色"),
    ("AccentBorder", "Card", 3.0, "勾选态/聚焦边框（树/列表底）"),
    ("AccentBorder", "Window", 3.0, "勾选态/聚焦边框（窗口底）"),
    ("AccentBorder", "Panel", 3.0, "勾选态/聚焦边框（面板底）"),
    # 标签头选中行自己带底色，它的聚焦环只能压在这块绿上：AccentBorder 压 Selected 只有 1.94:1，
    # 环会糊进底色里看不见，所以那一圈用 TextDim。
    ("TextDim", "Selected", 3.0, "标签头聚焦环压选中底色"),
]

COLOR_RE = re.compile(r'<Color\s+x:Key="C\.(?P<name>[\w]+)"\s*>\s*(?P<hex>#[0-9A-Fa-f]{6,8})\s*</Color>')


def parse_palette(path: Path) -> dict[str, str]:
    colors: dict[str, str] = {}
    for m in COLOR_RE.finditer(path.read_text(encoding="utf-8")):
        colors[m.group("name")] = m.group("hex")[:7].upper()  # 忽略 alpha
    if not colors:
        raise SystemExit(f"未从 {path} 解析到任何 C.* 颜色，请检查色板格式")
    return colors


def _srgb_to_linear(c: float) -> float:
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def relative_luminance(hex_color: str) -> float:
    h = hex_color.lstrip("#")
    r, g, b = (int(h[i : i + 2], 16) / 255.0 for i in (0, 2, 4))
    return 0.2126 * _srgb_to_linear(r) + 0.7152 * _srgb_to_linear(g) + 0.0722 * _srgb_to_linear(b)


def contrast_ratio(fg: str, bg: str) -> float:
    l1, l2 = relative_luminance(fg), relative_luminance(bg)
    lo, hi = min(l1, l2), max(l1, l2)
    return (hi + 0.05) / (lo + 0.05)


def resolve(token: str, colors: dict[str, str], palette: str, label: str) -> str:
    if token.startswith("#"):
        return token
    if token not in colors:
        raise SystemExit(f"{palette}：缺少色板键 C.{token}（{label} 需要）")
    return colors[token]


def main(argv: list[str]) -> int:
    root = Path(argv[1] if len(argv) > 1 else "src/SliderSorter.Wpf")
    themes = root / "Themes"
    palettes = {
        "Boutique": parse_palette(themes / "Palette.Boutique.xaml"),
        "Light": parse_palette(themes / "Palette.Light.xaml"),
    }

    # 两套色板的键集合必须一致，否则切换主题会掉进"资源找不到"的静默回退
    keys = {name: set(c) for name, c in palettes.items()}
    if keys["Boutique"] != keys["Light"]:
        only_b = sorted(keys["Boutique"] - keys["Light"])
        only_l = sorted(keys["Light"] - keys["Boutique"])
        print("[X] 两套色板的键集合不一致")
        if only_b:
            print(f"    仅 Boutique 有: {', '.join(only_b)}")
        if only_l:
            print(f"    仅 Light 有: {', '.join(only_l)}")
        return 1

    failures: list[str] = []
    for palette, colors in palettes.items():
        print(f"\n=== {palette}（{len(colors)} 个颜色键）===")
        print(f"{'比度':>7}  {'要求':>5}  {'结果':<4} 组合 / 用途")
        for fg_token, bg_token, minimum, label in REQUIRED:
            fg = resolve(fg_token, colors, palette, label)
            bg = resolve(bg_token, colors, palette, label)
            ratio = contrast_ratio(fg, bg)
            ok = ratio >= minimum
            mark = "OK" if ok else "FAIL"
            pair = f"{fg_token} on {bg_token}"
            print(f"{ratio:>6.2f}:1  {minimum:>5.1f}  {mark:<4} {pair:<34} {label}")
            if not ok:
                failures.append(
                    f"{palette}: {pair} = {ratio:.2f}:1 < {minimum}:1 （{label}，"
                    f"实际色值 {fg} / {bg}）"
                )

    print()
    if failures:
        print(f"[X] {len(failures)} 个组合不达标：")
        for f in failures:
            print(f"    - {f}")
        return 1
    print(f"[OK] 两套色板全部 {len(REQUIRED)} 个组合达标")
    return 0


if __name__ == "__main__":
    # Windows 控制台默认 cp936，中文输出会抛 UnicodeEncodeError
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    raise SystemExit(main(sys.argv))
