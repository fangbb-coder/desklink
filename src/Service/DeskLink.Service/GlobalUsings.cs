// 全局 using 声明（.NET 6+ ImplicitUsings 已默认含 System 等；这里补 Service 专用）。
//
// 为何不在每个文件里写 using：
//   - P4 Service 文件多（Security / Pipe / Process / Relay / Configuration / 根）
//   - 跨平台相关的 System.IO.* / System.Security.Cryptography.* 在 netstandard 2.0 即可用
//   - Service 进程同时承担 LocalSystem 服务模式与 --console 模式，Windows-only API
//     (System.IO.Pipes.AccessControl, System.Security.AccessControl) 必须显式声明
//     否则编译报 CA1416（已通过 #pragma warning disable / 局部 using 屏蔽）
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Security.Cryptography;
global using System.Security.Principal;
global using System.Text;
global using System.Threading;
global using System.Threading.Tasks;
global using Microsoft.Extensions.Logging;
