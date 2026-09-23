using Xunit;

// 与 Core 测试项目同一理由：这些用例共享**进程级静态状态**——CoreStrings.Localizer（静态可写取词器）、
// L10n 的语言快照、AppSettings 的共享实例与目录覆盖、WPF 的 Application 单例。
// 并行跑时一个用例改了取词器/设置目录，另一个用例的断言就会随机红，所以整体关掉程序集级并行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
