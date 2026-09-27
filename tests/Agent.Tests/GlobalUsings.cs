// xunit 的 ImplicitUsings 不会自动引入 Xunit 命名空间（只有 BCL 的少数命名空间会）。
// 经典 xunit 模板靠一个 GlobalUsings.cs 解决；这里沿用同一做法，
// 避免每个测试文件顶部都重复写 using Xunit。
global using Xunit;
