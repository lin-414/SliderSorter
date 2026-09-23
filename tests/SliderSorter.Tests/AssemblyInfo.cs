using Xunit;

// CoreStrings.Localizer 是静态可写属性（Core 不依赖 UI 层，只能靠它取词）。
// 一旦有测试写入它，并行跑的其他测试就会看到被换掉的取词器——而其他测试断言的正是
// "没有取词器时返回键名"。这类共享可变全局必须关掉程序集级并行，否则测试会随机红。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
