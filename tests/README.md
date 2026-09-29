# 能力边界验证

从仓库根目录运行，使用 .NET 10 SDK。以下 .NET 测试不会连接或修改正在运行的游戏。

```powershell
dotnet run --project tests/CapabilityValidation/CapabilityValidation.csproj -v quiet
dotnet run --project tests/QueueValidation/QueueValidation.csproj -v quiet
dotnet run --project tests/QueryValidation/QueryValidation.csproj -v quiet
dotnet run --project tests/StateSummaryValidation/StateSummaryValidation.csproj -v quiet -p:NoWarn=0649
```

- CapabilityValidation：40 项，链接真实 Mod 源文件；假对象仅提供原生状态和回调，验证权限、提示身份、取消、反转边界和垃圾入口。
- QueueValidation：24 项，链接真实调度器，隔离命令执行与历史存储，包含地形与植被操作的机甲互斥。需先构建 Mod，使输出目录包含 Newtonsoft.Json.dll；不添加测试依赖包。
- QueryValidation：2 项，加载仓库已构建的 AutomaticDSP.dll 及本机 Steam 安装中的游戏程序集，通过真实查询器读取离线数据。需先构建 Mod；从其他目录运行时可传仓库根目录参数。本机游戏路径在测试入口中明确列出，非通用安装探测工具。

这些检查不证明原生物理、吞吐或实际玩家 UI 操作。

StateSummaryValidation 链接真实垃圾与建造摘要实现，用隔离数据验证 10 项：全局／当前星球分类、建筑名称、回收槽过滤、种类截断不漏计、垃圾块数与物品数区分，以及按需垃圾明细保留。

## 地形与植被实机验证

脚本会修改当前对局。通过 Mod 加载独立测试档并先保存基线，再查询当前存档的坐标和实体。接口见 [API 文档](../docs/api.md)。


[TerrainValidation.ps1](TerrainValidation.ps1) 的必填参数：

- `Position`：由 x、y、z 构成的数组，指向建造范围内一格未改造、远离建筑和矿脉的空地。
- `VegeId`：可收取的现场植被 ID。

需要已解锁地基、有地基和足够沙土，且手持为空或地基。`BaseUrl` 默认 `http://127.0.0.1:39270`。脚本不会修改科技、补发物品或切换沙盒。

脚本验证铺设、重复铺设、无装饰地基、还原及重复还原、植被收取／种植、碰撞与非法参数，并对照实际资源和对象查询。缺少植被用例从当前原型目录选择未收藏的原型；收藏齐全时跳过。失败时检查任务历史与现场，不应盲目重跑。

持久化需另外验证：记录网格、高度、现场植被位置／旋转、地基总数、沙土及收藏，调用 `/game/save` 确认 `saved: true`，退出并重新加载后比对。不要假设整平和还原的沙土收支完全抵消。

尚未实机覆盖：实际水面填海、10×10／纬度分段、增产地基、缺地基／缺沙土、科技未解锁、气态星、手持非地基、满背包还原、矿脉掩埋／露出、战斗基地／基地坑及玩家 UI 并发。保留原生约束不等于这些分支已验证。
