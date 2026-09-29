# 能力验证

## 离线验证

从仓库根目录运行，使用 .NET 10 SDK。以下 .NET 测试不会连接或修改正在运行的游戏。

```powershell
dotnet run --project tests/CapabilityValidation/CapabilityValidation.csproj -v quiet
dotnet run --project tests/QueueValidation/QueueValidation.csproj -v quiet
dotnet run --project tests/QueryValidation/QueryValidation.csproj -v quiet
dotnet run --project tests/StateSummaryValidation/StateSummaryValidation.csproj -v quiet -p:NoWarn=0649
dotnet run --project tests/ProductionSettingsValidation/ProductionSettingsValidation.csproj -v quiet
```

| 项目 | 覆盖范围 | 前置条件与边界 |
| --- | --- | --- |
| CapabilityValidation | 40 项：权限、提示身份、取消、传送带反转边界和垃圾入口 | 链接真实 Mod 源文件；假对象仅提供原生状态和回调 |
| QueueValidation | 24 项：调度通道、依赖及机甲互斥，包含地形与植被操作 | 链接真实调度器，隔离命令执行与历史存储；需先构建 Mod 提供 Newtonsoft.Json.dll |
| QueryValidation | 2 项：真实查询器的离线查询与序列化 | 需先构建 Mod，并安装游戏；加载 AutomaticDSP.dll 和本机游戏程序集。可传仓库根目录参数；游戏路径目前固定在测试入口中 |
| StateSummaryValidation | 10 项：垃圾分类、建筑名称、回收槽过滤、截断计数、垃圾块数与物品数区分、按需明细 | 链接真实垃圾与建造摘要实现，使用隔离数据 |
| ProductionSettingsValidation | 27 项：科技、组件、配方、参数、距离、缓存返还、幂等和失败不修改状态 | 链接真实生产设置实现；需先构建 Mod 提供 Newtonsoft.Json.dll；同步与返还通过记录假原生方法调用验证 |

这些检查不证明原生物理、实际吞吐或玩家 UI 操作，也不能替代堆叠同步、光子产出及弹射运行的实机验证。

## 实机脚本

脚本会修改当前对局。通过 Mod 加载独立测试档，先保存基线，再查询并选择当前存档的坐标和实体。参数不要复用其他存档中的固定 ID。接口见 [API 文档](../docs/api.md)。

### 增产模式

[ProliferatorModeValidation.ps1](ProliferatorModeValidation.ps1) 的必填参数：

- `AssemblerId`：建造范围内、当前配方支持额外产出的制造组件。
- `MatrixLabId`：建造范围内、制造矩阵的研究站。
- `ResearchLabId`：建造范围内、执行科研的研究站。

三个实体必须属于当前星球；`BaseUrl` 默认 `http://127.0.0.1:39270`。

脚本切换模式，通过原生组件查询核对结果、幂等性及失败不修改状态，正常完成后恢复原模式。失败后检查现场模式和任务历史，必要时恢复基线。脚本不覆盖整叠同步或实际增产吞吐，也不覆盖射线接收站和弹射器设置。

### 地形与植被

[TerrainValidation.ps1](TerrainValidation.ps1) 的必填参数：

- `Position`：由 x、y、z 构成的数组，指向建造范围内一格未改造、远离建筑和矿脉的空地。
- `VegeId`：可收取的现场植被 ID。

需要已解锁地基、有地基和足够沙土，且手持为空或地基。`BaseUrl` 默认 `http://127.0.0.1:39270`。脚本不会修改科技、补发物品或切换沙盒。

脚本验证铺设、重复铺设、无装饰地基、还原及重复还原、植被收取／种植、碰撞与非法参数，并对照实际资源和对象查询。缺少植被用例从当前原型目录选择未收藏的原型；收藏齐全时跳过。失败时检查任务历史与现场，不应盲目重跑。

持久化需另外验证：记录网格、高度、现场植被位置／旋转、地基总数、沙土及收藏，调用 `/game/save` 确认 `saved: true`，退出并重新加载后比对。不要假设整平和还原的沙土收支完全抵消。

尚未实机覆盖：实际水面填海、10×10／纬度分段、增产地基、缺地基／缺沙土、科技未解锁、气态星、手持非地基、满背包还原、矿脉掩埋／露出、战斗基地／基地坑及玩家 UI 并发。保留原生约束不等于这些分支已验证。

## 手动实机验收

以下为可重复使用的验收方法，没有对应的独立自动化脚本。记录当次基线、操作和结果到本地报告；不要把单次存档坐标、库存、tick 或任务流水写入本说明。

| 能力 | 验收方法 | 覆盖限制 |
| --- | --- | --- |
| 堆叠研究站增产设置 | 分别从底层和中层切换模式，读取整叠 `labPool.forceAccMode`，最后恢复原模式 | 已有整叠同步实机记录；不代表实际增产吞吐 |
| 黄糖与星际物流闭环 | 外星采集、冶炼后自动入供应站，母星需求站收货并自动出站供科研；以库存变化、持续到货及科研进度确认链路，不能人工填料代替进口 | 已有闭环实机记录；短窗口不保证长期吞吐。重点验证 Mod 操作及连接结果，不重复测试原生调度；持续燃料、电力与副产物出口需另行评估 |
| 射线接收与弹射设置 | 切换发电／光子模式，读回 `productId`；设置有效轨道与自动换轨，验证省略开关保留现值、重复设置及非法参数拒绝后状态不变，最后恢复设置 | 已有设置实机记录；实际光子产出、弹射运行和非空缓存返还尚未实机覆盖，缓存返还仅有离线边界验证 |
| 运输站充电上限 | 设置原生范围内的上限，读回 `workEnergyPerTick`；检查幂等、越界、步长、小数与错误类型拒绝后状态不变 | 已有星际站实机记录；不代表行星站成功路径或运输吞吐已验证 |
| 导航与界面并行 | 在 `navigateTo` 期间打开科研、星图或背包／制造窗口，连续读取阶段和 `player.flight`，确认输入持续生效；核对到达及 `appliedTicks`，不能仅凭 RUNNING 或惯性判断成功 | 科研、星图和正常视图移动接管已有实机记录；背包／制造、文本输入及截图模式仍需逐项验证 |

导航验收还需检查：正常游戏视图中的移动、停止及显式手动导航允许接管；原任务超时不延长；游戏真正暂停时不要求物理运动推进。
