# AutomaticDSP 开发计划

本文是当前实现顺序的执行计划。早期需求和技术方案中提到的 GraphQL-first 设计保留为后续查询增强候选，不作为 M1 的交付范围。

## 当前方向

AutomaticDSP 保持独立 BepInEx Mod，不依赖 Nebula。Nebula 只作为设计参考：它对玩家位置使用高频同步，对统计、告警、研究、戴森球等状态使用较低频率或事件同步。AutomaticDSP 第一阶段不追求联机同步，只需要让外部 AI Agent 稳定观察游戏状态。

第一步采用简单模式：

- 游戏主线程每 `60 game ticks` 生成一次状态快照，约 1 秒一次。
- 外部查询只读取最新内存快照，不直接访问 DSP 游戏对象。
- 任务状态不放进状态查询。
- `GET /tasks` 查询内存中的待执行或执行中任务。
- `GET /history` 查询 SQLite 中已完成、失败或取消的历史命令。
- 运行时数据和快照统一输出到 `BepInEx/cache/AutomaticDSP`。
- 暂不实现 GraphQL，先用 REST/JSON 跑通可观测性。
- HTTP 服务使用 .NET 内置 `HttpListener`，JSON 序列化使用 `Newtonsoft.Json`。
- HTTP 默认监听 `127.0.0.1:39270`，其中 `HTTP.Host` 和 `HTTP.Port` 都是配置项；需要外部访问时可以把 `HTTP.Host` 改成 `0.0.0.0`。
- 未加载存档、主菜单、菜单演示或加载界面不生成状态快照，`GET /state` 返回明确的不可用状态，并清理旧的 `snapshots/latest.json` 与 `diagnostics/gameMain.json`。

采样间隔参考：

- Nebula 的统计广播按 `time % 60 == 0` 节流，约 1 秒一次。
- Nebula 的玩家移动同步是 10Hz，但 AutomaticDSP 第一阶段不做实时玩家同步。
- Nebula 的研究、戴森球、告警等状态使用 1-4 秒级别更新。

## 第一阶段目标：只读状态观测 MVP

第一阶段只解决一件事：让 AI Agent 能通过外部接口知道游戏当前状态。

### 成功标准

1. Mod 能在 BepInEx 中加载。
2. 游戏载入后，主线程每 60 game ticks 采样一次状态。
3. `GET /health` 能返回 Mod 状态、游戏是否载入、最近快照 tick。
4. `GET /state` 能返回最新状态快照。
5. `GET /tasks` 能返回内存中的待执行或执行中任务；第一步可以为空列表。
6. `GET /history` 能返回 SQLite 中的历史命令；第一步可以为空列表。
7. HTTP 请求线程不直接读取 DSP 游戏对象。
8. 构建通过，游戏内日志能看到快照采样成功。

### 第一阶段不做

- GraphQL。
- 任务提交。
- 任务执行。
- 移动伊卡洛斯。
- 背包制造。
- 建造生产线。
- Nebula 运行时依赖。
- 全星系或全宇宙状态导出。

## 第一阶段快照内容

快照按 AI Agent 玩游戏需要的决策信息组织，而不是直接暴露 DSP 内部对象。

### metadata

- `snapshotId`
- `gameTick`
- `capturedAt`
- `captureDurationMs`
- `gameLoaded`
- `currentPlanetId`
- `currentStarId`
- `schemaVersion`

### game

`game` 是从 `GameMain` 和 `GameMain.data` 抽取的稳定对局摘要，用来判断当前快照属于哪个存档、处于什么运行状态，以及哪些根系统可用。

- 存档名称。
- 存档创建时间。
- 当前 `gameTick` 和 `gameTime`。
- 当前运行段 `onceGameTick` 和 `onceGameTime`。
- 沙盒工具是否启用。
- 生命周期状态：运行中、加载中、暂停、结束、加载失败、其他场景、菜单演示。
- 当前恒星和当前行星 ID、名称。
- 玩家是否在行星上。
- 星系摘要：恒星数量。
- 存档数据摘要：行星工厂数量、戴森球数组数量、引导任务状态、地表是否就绪。
- 根系统可用性：玩家、科技历史、统计、偏好、太空层、星际物流、告警、垃圾系统。

### player

- 当前行星内位置。
- 宇宙位置。
- 当前行星 ID。
- 当前恒星 ID。
- 朝向。
- 移动状态。
- 是否在行星内。
- 是否飞行、航行或曲速。
- 建造范围。
- 交互范围。

### mecha

- 生命值。
- 能量。
- 最大能量。
- 当前燃料。
- 沙土数量。
- 背包容量摘要。
- 建造无人机数量。
- 空闲建造无人机数量。
- 正在工作的建造无人机数量。
- 物流配送能力摘要。

### inventory

- 背包物品列表。
- 每个物品的 `itemId`、名称、数量。
- 空槽数量。
- 总容量。
- 建筑类物品数量摘要。
- 基础材料数量摘要。

### replicator

- 背包制造队列。
- 当前正在制造的物品。
- 每个队列项的 `recipeId`、`itemId`、剩余数量、预计完成 tick。
- 当前可手搓配方摘要。
- 关键建筑是否可手搓。
- 关键配方材料缺口。

### research

- 当前研究项目。
- 研究队列。
- 当前研究进度。
- 所需矩阵。
- 剩余 hash。
- 已解锁科技摘要。
- 已解锁配方摘要。
- 可研究科技摘要。
- 研究是否停滞。
- 停滞原因。

### currentPlanet

- 行星 ID。
- 行星名称。
- 行星类型。
- 半径。
- 所属恒星 ID。
- 风能倍率。
- 太阳能倍率。
- 资源摘要。
- 矿脉列表。
- 油井列表。
- 玩家附近资源点。
- 玩家附近可建造区域摘要。

### factory

- 当前行星建筑数量摘要。
- 玩家附近建筑列表。
- 矿机、熔炉、制造台、电塔、仓储、研究站等建筑摘要。
- 传送带数量和堵塞摘要。
- 分拣器数量和异常摘要。
- 无配方设施数量。
- 缺电设施数量。
- 输出堵塞设施数量。
- 输入不足设施数量。

### production

- 关键物品最近 1 分钟产量。
- 关键物品最近 1 分钟消耗。
- 关键物品净产量。
- 当前行星产量摘要。
- 生产停滞原因摘要。
- 基础物品优先包括铁矿、铜矿、铁块、铜块、磁铁、齿轮、电路板、传送带、分拣器、线圈。

### power

- 总发电量。
- 总耗电需求。
- 当前实际耗电。
- 电力满足率。
- 蓄电量。
- 电网数量。
- 玩家所在电网状态。
- 缺电建筑摘要。
- 燃料发电设施状态摘要。

### alerts

- 缺电。
- 缺材料。
- 产物堵塞。
- 建筑无配方。
- 研究停滞。
- 背包制造停滞。
- 玩家不在行星上。
- 建造无人机忙。
- 当前星球没有目标资源。

### buildContext

- 当前可建造建筑摘要。
- 背包已有建筑数量。
- 可通过手搓获得的建筑。
- 玩家附近已有电网。
- 玩家附近资源点。
- 基础生产线所需物品缺口摘要。

### debug

`debug` 用于早期字段映射，不作为 Agent 长期依赖的稳定查询契约。

- `GameMain` 静态字段和属性摘要。
- 当前 `GameMain` 实例字段和属性摘要。
- `GameMain.data` 字段和属性摘要。
- `DSPGame` 静态字段和属性摘要。
- 同步写入 `BepInEx/cache/AutomaticDSP/diagnostics/gameMain.json` 便于调试。
- 仅在真实对局载入后保存；主菜单、菜单演示或加载界面不保存。

## 第一阶段接口

### GET /health

返回 Mod 与快照服务状态。
当 `/state` 不可用时，`sessionGate` 会给出当前被拦截的原因，方便区分主菜单、加载中、菜单演示或真实对局字段缺失。

```json
{
  "status": "ok",
  "gameLoaded": true,
  "hasState": true,
  "latestSnapshotId": 12,
  "latestGameTick": 123456,
  "snapshotIntervalTicks": 60,
  "sessionGate": {
    "loaded": true,
    "reason": null
  }
}
```

### GET /state

返回最新状态快照。

第一步不做复杂查询参数。后续可以增加 `sections`、`nearPlayerRadius`、`limit` 等参数。

### GET /tasks

返回内存中的待执行或执行中任务。

第一阶段可以返回：

```json
{
  "tasks": []
}
```

### GET /history

返回 SQLite 中已完成、失败或取消的历史命令。

第一阶段可以返回：

```json
{
  "items": []
}
```

## 里程碑计划

### M0：工程基础

状态：已完成。

交付：

- BepInEx 插件工程骨架。
- 基础 README。
- 需求和技术方案文档。

验证：

- `dotnet build` 通过。
- BepInEx 和 DSP 程序集引用可解析。

### M1：只读状态观测 MVP

交付：

- 配置项：端口、快照间隔 tick、是否启用 HTTP。
- 配置项：HTTP host 默认 `127.0.0.1`，HTTP port 默认 `39270`。
- `StateSnapshot` DTO。
- `StateSnapshotService`。
- 主线程 60 tick 采样。
- `GET /health`。
- `GET /state`。
- `GET /tasks` 空实现。
- `GET /history` 空实现。
- SQLite 初始化和历史表结构，数据库位于 `BepInEx/cache/AutomaticDSP/data/history.sqlite`。
- 最新状态快照写入 `BepInEx/cache/AutomaticDSP/snapshots/latest.json`。
- `GameMain` 调试摘要写入 `BepInEx/cache/AutomaticDSP/diagnostics/gameMain.json`。

验证：

- 进入游戏后日志显示快照定时生成。
- `GET /health` 返回最近快照 tick。
- `GET /state` 至少返回 metadata、game、player、inventory、replicator、research、currentPlanet、factory、production、power、alerts。
- 不进入游戏时接口返回明确状态，而不是异常。
- 停留在主菜单或菜单演示时不会保留旧的 `latest.json` 或 `gameMain.json`。

### M2：状态完整性补齐

交付：

- 更完整的背包和背包制造队列。
- 研究队列和配方解锁状态。
- 当前行星矿脉和资源点。
- 当前行星建筑摘要。
- 产量、消耗和供电摘要。
- 告警和 buildContext。

验证：

- AI Agent 能根据 `/state` 判断“能不能建一条铁块生产线”。
- 快照生成耗时可观测。
- 快照过大时有服务端上限或摘要策略。

### M3：任务队列壳

交付：

- 内存任务队列。
- `POST /tasks` 提交任务。
- `POST /tasks/{id}/cancel` 请求取消任务。
- `GET /tasks` 返回待执行和执行中任务。
- `GET /history` 从 SQLite 返回历史命令。
- no-op 命令执行器。

验证：

- 多任务按顺序进入同一个队列。
- no-op 命令可以完成并写入 SQLite 历史。
- queued 任务可以取消。

### M4：伊卡洛斯移动与背包制造

交付：

- `moveTo` 命令。
- `craftInventory` 命令。
- 命令执行状态。
- 错误码：缺材料、不可达、超时。

验证：

- Agent 可以要求伊卡洛斯移动到指定点。
- Agent 可以要求背包制造基础建筑。
- 命令成功、失败、取消都会进入历史。

### M5：基础建造命令

交付：

- `placeBuilding`。
- `placeBelt`。
- `placeSorter`。
- `setRecipe`。
- 建造前校验：科技、物品、地形、碰撞、范围。

验证：

- 能放置矿机、熔炉、电塔、传送带、分拣器。
- 能设置熔炉配方。
- 失败时返回明确错误码。

### M6：行星内生产线闭环

交付：

- `waitUntil`。
- 简单铁块生产线任务。
- 生产成功检测。
- 基础恢复策略。

验证：

- 外部 Agent 能提交任务，完成从矿机到熔炉的铁块生产线。
- `/state` 能反映新建筑、供电和产量变化。
- `/history` 能追踪每条命令的结果。

### M7：查询能力增强

交付：

- `/state` 支持按 section 返回。
- 支持玩家附近半径过滤。
- 支持建筑、矿脉、告警数量上限。
- 评估是否引入 GraphQL 作为只读查询适配层。

验证：

- Agent 可以低成本获取小范围状态。
- 大型工厂存档不会因为一次查询返回过多数据。

## 实现约束

- 所有 DSP 游戏对象读取必须发生在 Unity 主线程。
- HTTP 请求线程只能读取不可变快照、内存任务状态或 SQLite 历史。
- 快照 DTO 不暴露 DSP 内部对象引用。
- 第一阶段优先摘要信息，避免全量导出巨大数组。
- 默认只监听 `127.0.0.1`。
- 默认端口 `39270`。
- SQLite 只存历史命令，不存完整游戏快照。
- 最新 JSON 快照写入 `BepInEx/cache/AutomaticDSP/snapshots/latest.json`，用于调试和外部观测。
- GameMain 调试摘要写入 `BepInEx/cache/AutomaticDSP/diagnostics/gameMain.json`，只在真实对局载入后保存。

## 待确认问题

- `/state` 第一阶段返回所有 M1 字段，后续再增加 `?sections=`。
- 历史保留策略：无限保留、按条数保留，还是按天清理。
- 是否需要为外部 Agent 提供 OpenAPI 描述。
