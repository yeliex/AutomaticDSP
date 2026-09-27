# 能力边界验证

从仓库根目录运行，使用 .NET 10 SDK。测试不会连接或修改正在运行的游戏。

```powershell
dotnet run --project tests/CapabilityValidation/CapabilityValidation.csproj -v quiet
dotnet run --project tests/QueueValidation/QueueValidation.csproj -v quiet
dotnet run --project tests/QueryValidation/QueryValidation.csproj -v quiet
```

- CapabilityValidation：40 项，链接真实 Mod 源文件；假对象仅提供原生状态和回调，验证权限、提示身份、取消、反转边界和垃圾入口。
- QueueValidation：15 项，链接真实调度器，隔离命令执行与历史存储。需先构建 Mod，使输出目录包含 Newtonsoft.Json.dll；不添加测试依赖包。
- QueryValidation：2 项，加载仓库已构建的 AutomaticDSP.dll 及本机 Steam 安装中的游戏程序集，通过真实查询器读取离线数据。需先构建 Mod；从其他目录运行时可传仓库根目录参数。本机游戏路径在测试入口中明确列出，非通用安装探测工具。

这些检查不证明原生物理、吞吐或实际玩家 UI 操作。
