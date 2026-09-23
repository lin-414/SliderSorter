#!/usr/bin/env python3
"""带完整 Windows 用户环境的 dotnet 包装器。

背景：本沙箱的 shell 缺少一整套 Windows 环境变量（`APPDATA`、`ProgramData`、
`ALLUSERSPROFILE`、`CommonProgramFiles`、`TEMP`/`TMP` …）。NuGet 的
`XPlatMachineWideSetting` 在静态初始化里读它们算路径，缺任何一个都会在**任何项目被求值之前**
抛 `ArgumentNullException: Value cannot be null. (Parameter 'path1')`（NuGet.targets(782,5)）。

只在 Git Bash 里 `export APPDATA=...` 没用：MSYS 路径转换会把反斜杠值改成正斜杠，
而 NuGet 需要原生 Windows 路径；同理 `--no-restore` 只对**已经还原过**的项目有效，
`obj/` 一旦被清（例如改了 csproj、清了 bin/obj）就无路可走。

用法（在仓库根）：
    python tools/dotnet.py build  src/SliderSorter.Wpf/SliderSorter.Wpf.csproj -v q
    python tools/dotnet.py test   tests/SliderSorter.Wpf.Tests -v q
    python tools/dotnet.py run    --project tools/layout-probe

最后按 dotnet 的退出码退出，可直接用于 `&&` 串联。
"""
from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

# 沙箱缺失、而 NuGet / MSBuild 会去读的变量。值按本机实际情况给。
_WINDOWS_ENV = {
    "APPDATA": r"C:\Users\Administrator\AppData\Roaming",
    "LOCALAPPDATA": r"C:\Users\Administrator\AppData\Local",
    "ProgramData": r"C:\ProgramData",
    "ALLUSERSPROFILE": r"C:\ProgramData",
    "USERPROFILE": r"C:\Users\Administrator",
    "HOMEDRIVE": "C:",
    "HOMEPATH": r"\Users\Administrator",
    "TEMP": r"C:\Users\Administrator\AppData\Local\Temp",
    "TMP": r"C:\Users\Administrator\AppData\Local\Temp",
    "windir": r"C:\Windows",
    "SystemRoot": r"C:\Windows",
    "SystemDrive": "C:",
    "ProgramFiles": r"C:\Program Files",
    "CommonProgramFiles": r"C:\Program Files\Common Files",
    "PUBLIC": r"C:\Users\Public",
    "ComSpec": r"C:\Windows\System32\cmd.exe",
    "USERNAME": "Administrator",
    "OS": "Windows_NT",
    "PATHEXT": ".COM;.EXE;.BAT;.CMD",
    "PROCESSOR_ARCHITECTURE": "AMD64",
}


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    env = dict(os.environ)
    # 只补缺失的，不覆盖调用方显式给的值——这样临时想换目录仍然可行。
    for key, value in _WINDOWS_ENV.items():
        env.setdefault(key, value)

    dotnet = Path(r"C:\Program Files\dotnet\dotnet.exe")
    exe = str(dotnet) if dotnet.exists() else "dotnet"

    return subprocess.run([exe, *sys.argv[1:]], env=env, cwd=os.getcwd()).returncode


if __name__ == "__main__":
    raise SystemExit(main())
