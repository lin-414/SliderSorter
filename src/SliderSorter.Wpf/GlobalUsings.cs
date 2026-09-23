// WPF 工程的隐式 usings 不含 System.IO / System.Net.Http（WindowsDesktop SDK 与基础集不同），
// Core 与 UI 代码都按 WinForms 工程的隐式集合编写，这里补齐。
// 注意：不要在这里全局引入 System.Windows.*，否则 Core 里的 Path/File 会与 WPF 类型产生二义性。
global using System.IO;
global using System.Net.Http;
