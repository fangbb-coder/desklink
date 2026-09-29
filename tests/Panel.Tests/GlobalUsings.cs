// net9.0-windows + UseWPF 的隐式 using **不含** System.IO（与 src\Tools\DeskLink.Panel 同因）。
// 测试里大量用 Path/临时目录，集中在这里声明一次，避免每个文件重复 using。
global using System.IO;
