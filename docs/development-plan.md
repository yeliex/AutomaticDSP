# AutomaticDSP 开发计划

本文是当前实现顺序的执行计划。当前方向已经切换为 GraphQL 字段选择 DSL：`GET /game` 提供轻量运行状态探针，`POST /game/state` 按需查询游戏状态。

## 当前方向

AutomaticDSP 保持独立 BepInEx Mod，不依赖 Nebula。Nebula 只作为设计参考：它对玩家位置使用高频同步，对统计、告警、研究、戴森球等状态使用较低频率或事件同步。AutomaticDSP 第一阶段不追求联机同步，只需要让外部 AI Agent 稳定观察游戏状态。

第一步采用简单模式：

- HTTP 线程接收 `POST /game/state` 后只解析 GraphQL 字段选择并入队，不直接访问 DSP 游戏对象。
- 游戏主线程每 `60 game ticks` drain 待查询队列，并在主线程为每个请求生成最终 JSON，约 1 秒一次。
- 任务状态不放进状态查询。
- `GET /tasks` 查询内存中的待执行或执行中任务。
- `GET /history` 查询 SQLite 中已完成、失败或取消的历史命令。
- 状态查询结果只保存在内存中，不默认输出快照文件；历史命令继续保存到 `BepInEx/cache/AutomaticDSP/data/history.sqlite`。
- HTTP 服务使用 .NET 内置 `HttpListener`，JSON 序列化使用 `Newtonsoft.Json`。
- HTTP 默认监听 `127.0.0.1:39270`，其中 `HTTP.Host` 和 `HTTP.Port` 都是配置项；需要外部访问时可以把 `HTTP.Host` 改成 `0.0.0.0`。
- 未加载存档、主菜单、菜单演示或加载界面不接受 `/game/state` 查询，`GET /game` 返回轻量状态，并清理旧的 `snapshots/latest.json`、`snapshots/state.json`、`snapshots/galaxy.json`、`snapshots/transport.stations.json`、`snapshots/spheres.json`、早期 `dumps/gameData.json` 与早期 `diagnostics/gameMain.json`。

采样间隔参考：

- Nebula 的统计广播按 `time % 60 == 0` 节流，约 1 秒一次。
- Nebula 的玩家移动同步是 10Hz，但 AutomaticDSP 第一阶段不做实时玩家同步。
- Nebula 的研究、戴森球、告警等状态使用 1-4 秒级别更新。

## 第一阶段目标：只读状态观测 MVP

第一阶段只解决一件事：让 AI Agent 能通过外部接口知道游戏当前状态。

### 成功标准

1. Mod 能在 BepInEx 中加载。
2. 游戏载入后，主线程每 60 game ticks 批处理待查询字段。
3. `GET /game` 能返回轻量游戏运行状态。
4. `POST /game/state` 能按 GraphQL 字段选择返回游戏状态。
5. `GET /tasks` 能返回内存中的待执行或执行中任务；第一步可以为空列表。
6. `GET /history` 能返回 SQLite 中的历史命令；第一步可以为空列表。
7. HTTP 请求线程不直接读取 DSP 游戏对象。
8. 构建通过，游戏内日志无查询批处理异常。

### 第一阶段不做

- 任务提交。
- 任务执行。
- 移动伊卡洛斯。
- 背包制造。
- 建造生产线。
- Nebula 运行时依赖。
- 全星系或全宇宙状态导出。

## 第一阶段状态查询内容

状态查询按 AI Agent 玩游戏需要的决策信息组织，而不是直接暴露 DSP 内部对象。

### metadata

- `gameTick`
- `queriedAt`
- `localPlanetId`
- `localStarId`
- `schemaVersion`

### game

`game` 是从 `GameMain` 和 `GameMain.data` 抽取的稳定对局摘要，用来判断当前查询属于哪个存档、处于什么运行状态，以及哪些根系统可用。

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

### GameMain 根系统

以下字段作为 `/game/state` 顶层对象返回，来自 `GameMain` 或 `GameMain.data` 的根系统。它们使用手写摘要 DTO，不直接返回 Unity/DSP 运行时对象，也不暴露 `type/fields/properties` 这类反射 dump 结构：

- 基本类型、字符串、枚举、时间和值类型按稳定字段名输出。
- `Vector3`、`VectorLF3` 和 `Quaternion` 转成可读 KV。
- 大数组只返回 count、summary 或有限 sample。
- 复杂对象只保留 AI Agent 可直接决策的关键字段；需要明细时后续通过 query DSL 查询。
- `preferences`
- `statistics`
- `spaceSector`
- `galaxy`
- `dysonSpheres`
- `history`
- `galacticTransport`
- `warningSystem`
- `trashSystem`
- `goalSystem`
- `milestoneSystem`
- `gameAchievement`

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

### forge

- 背包制造队列。
- 制造总剩余时间和当前实际制造速度。
- `MechaForge.extraItems` 与瓶颈物品列表。
- 每个队列项的 `recipeId`、配方名称、剩余次数、tick 进度、父任务索引、材料投入和产物缓存。
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

### localPlanet

- 行星 ID。
- 行星名称。
- 行星类型。
- 半径。
- 所属恒星 ID。
- 风能倍率。
- 太阳能倍率。
- 当前星球工厂加载状态。
- 当前星球工厂实体数量和实体游标。
- 当前星球建筑类型数量汇总。
- 当前星球传送带和分拣器游标数量。
- 当前星球缺电、缺料、产物缓存等状态汇总。

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

## 第一阶段接口

### GET /game

返回主线程每 tick 维护的轻量游戏运行状态，不进入快照采集流程。

未进入可查询对局时只返回：

```json
{
  "ready": false,
  "status": "menu"
}
```

`status` 当前取值为 `loading`、`running`、`paused`、`ended`、`prologue`、`cutscene`、`error`、`menu`、`unknown`。

进入可查询对局时返回：

```json
{
  "ready": true,
  "gameName": "Save Name",
  "gameTick": 123456,
  "gameTime": 123.45,
  "onceGameTick": 123456,
  "onceGameTime": 123.45,
  "sandboxToolsEnabled": false,
  "creationTime": "2026-06-13T12:00:00",
  "status": "running",
  "isCombatMode": false,
  "combatModeDifficulty": 0,
  "resourceMultiplier": 1,
  "oilAmountMultiplier": 1,
  "starCount": 64
}
```

### POST /game/state

接收 GraphQL 字段选择 DSL，按需返回游戏状态。

```json
{
  "query": "{ game { gameName gameTick } localPlanet { id displayName } inventory { size grids(limit: 10) { itemId count } } }"
}
```

返回：

```json
{
  "data": {
    "game": {
      "gameName": "Save Name",
      "gameTick": 123456
    },
    "localPlanet": {
      "id": 1,
      "displayName": "地中海"
    },
    "inventory": {
      "size": 120,
      "grids": [
        {
          "itemId": 1101,
          "count": 100
        }
      ]
    }
  }
}
```

查询规则：

- 只解析 GraphQL 查询 DSL，不提供 GraphQL schema。
- 不做业务字段校验。
- 不存在或不可读字段返回 `null`。
- 复杂对象未选择子字段时返回 `{}`，避免把“对象存在但未展开”误判为不存在。
- 复杂列表默认最多返回 256 项，支持 `limit`、`offset` 和 `where`，单次 `limit` 上限为 2048。
- 支持 `_schema` 查询根和对象上的 `_fields` 字段发现。
- `where` 只作用于列表字段，条件按 AND 组合，过滤在分页前执行；不存在字段进入条件时视为匹配失败，不提供 `field_exists`。
- 未进入可查询对局时返回 `409 game_not_ready`，不进入主线程查询队列。

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
- `StateSnapshotService`。
- GraphQL 字段选择解析。
- 主线程 60 tick 查询批处理。
- `GET /game`。
- `POST /game/state`。
- `GET /tasks` 空实现。
- `GET /history` 空实现。
- SQLite 初始化和历史表结构，数据库位于 `BepInEx/cache/AutomaticDSP/data/history.sqlite`。

验证：

- `GET /game` 返回轻量游戏运行状态。
- `POST /game/state` 能查询 metadata、game、GameMain、GameMain.data 和可序列化游戏对象字段。
- 不进入游戏时接口返回明确状态，而不是异常。
- 停留在主菜单或菜单演示时不会保留旧的 `latest.json`、拆分快照或早期 `gameData.json`。

### M2：状态完整性补齐

交付：

- 更完整的背包和背包制造队列。
- 研究队列和配方解锁状态。
- 当前行星矿脉和资源点。
- 当前行星建筑摘要。
- 产量、消耗和供电摘要。
- 派生告警和建造上下文不放在 `/game/state`；后续根据建造命令需求设计独立 context/query。

验证：

- AI Agent 能根据 `/game/state` 判断“能不能建一条铁块生产线”。
- 查询批处理耗时可观测。
- 大列表查询有服务端上限或分页策略。

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
- `/game/state` 能反映新建筑、供电和产量变化。
- `/history` 能追踪每条命令的结果。

### M7：查询能力增强

交付：

- `/game/state` 支持按 section 返回。
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
- 状态查询不默认写入 JSON 快照文件；如需调试 dump，后续通过显式调试接口或配置开关实现。

## 待确认问题

- `/game/state` 后续是否需要增加查询耗时、字段读取错误等诊断信息。
- 历史保留策略：无限保留、按条数保留，还是按天清理。
- 是否需要为外部 Agent 提供 OpenAPI 描述。
