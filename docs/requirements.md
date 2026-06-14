# AutomaticDSP 需求说明

> 当前实现以 `docs/development-plan.md` 为准：`GET /game` 返回轻量游戏状态，`POST /game/state` 使用 GraphQL 字段选择 DSL 按需查询游戏状态，任务通过 `/tasks` 顺序队列提交和查询，`GET /history` 返回命令历史。
> M1 状态查询结果保存在内存中；历史命令写入 `BepInEx/cache/AutomaticDSP/data/history.sqlite`。

## 背景

AutomaticDSP 是一个用于《戴森球计划》的自动化控制 Mod。目标是让外部 AI Agent 能够程序化读取游戏状态，并向游戏提交顺序执行的任务，从而逐步完成生产线建设、物流建设和戴森球建设。

第一阶段聚焦“行星内生产线建立”。高级规划由外部 AI Agent 完成，Mod 提供可靠、可查询、可验证的游戏内状态与指令执行能力。

## 目标

第一阶段需要做到：

1. 外部程序可以通过 GraphQL 字段选择 DSL 一次性查询同一游戏查询 tick 内的多个游戏状态。
2. 外部程序可以通过 `POST /tasks` 提交任务到唯一顺序任务队列。
3. 游戏内按提交顺序执行任务，并按任务内命令顺序执行。
4. 外部程序可以通过 `GET /tasks` 查询待执行或执行中的任务，也可以通过任务接口取消任务。
5. 行星内生产线建设命令遵守科技、背包、地形、建造距离和游戏内建造流程。

## 阶段边界

第一阶段聚焦行星内生产线建立，以下能力进入后续阶段：

- 跨星际物流自动化。
- 戴森球节点、框架、壳层自动规划。
- 完整 AI 规划器。
- 多队列并行执行。
- 多人模式兼容保证。
- 直接修改存档来伪造建造成果。

## 核心概念

### 状态查询

`POST /game/state` 使用 GraphQL 查询语法作为字段选择 DSL，当前形态是面向游戏状态读取的字段选择接口。

一次 `/game/state` 请求会被放入主线程查询队列。游戏主线程在查询 tick 内直接按该请求的字段、别名和分页参数生成最终 JSON，HTTP 线程等待并返回主线程结果。

状态查询至少覆盖：

- `metadata`：查询 tick、查询时间、当前星球和恒星 ID。
- `game`：对局状态和基础配置。
- `player`：伊卡洛斯状态。
- `inventory`：背包物品。
- `forge` / `replicator`：背包制造队列。
- `localPlanet`：当前行星状态。
- `factory` / `localFactory`：当前行星工厂对象。
- `factoryDetails` / `localFactoryDetails`：当前行星工厂实体摘要，用于确认建成实体、配方和缺电/缺料/堵塞等状态。
- `production`：生产统计。
- `power`：当前行星供电系统。
- `data`：`GameMain.data` 中可序列化字段的按需读取入口。

### 实体

查询接口围绕游戏实体设计。实体是游戏内或 Mod 运行时可被查询的对象。

第一阶段可查询实体包括：

- `player`：伊卡洛斯位置、移动状态、建造范围、当前星球。
- `inventory`：背包物品、手搓队列、可制造物品。
- `technology`：科技解锁状态。
- `planet`：当前行星、地形和基础元数据。
- `vein`：矿脉点。
- `resourcePatch`：矿区聚合。
- `building`：矿机、熔炉、制造台、电塔、仓储等已存在建筑。
- `belt`：传送带片段。
- `sorter`：分拣器。
- `powerNetwork`：供电网络和供电状态。
- `recipe`：配方及解锁状态。
- `item`：物品原型。
待执行或执行中的任务通过 `GET /tasks` 查询；当前进程内单个任务快照通过 `GET /tasks/{id}` 查询；跨进程历史命令通过 `GET /history` 查询。

唯一任务队列通过 `POST /tasks` 写入，通过 `GET /tasks` 查询。返回结果中的每一项都是一个任务。

命令作为 task 列表项中的嵌套对象返回，每一项都是一条命令。需要查看命令状态时，读取对应 task 的 `commands` 字段。

后续阶段可以增加 `logisticStation`、`star`、`dysonSphere` 等实体。

## GraphQL 查询

查询协议采用 GraphQL 查询语法作为字段选择 DSL。

选择 GraphQL 的原因：

- 一次查询可以同时获取玩家、背包、建筑、生产、电力等游戏状态。
- 字段投影天然适合 AI Agent 按需取数。
- 嵌套结构可以按对象层级直接选择。
- 单个请求在同一个游戏查询 tick 内读取，获得一致的状态视图。

第一阶段查询必须支持：

- 多 root 字段组合查询。
- 嵌套字段投影。
- `limit`、`offset` 和 `where` 列表过滤。
- `_schema` 查询根和对象上的 `_fields` 字段发现。
- 字段读取失败以 `null` 表示。
- 复杂对象默认展开一层可序列化字段。

第一阶段支持范围：

- 字段选择、别名、fragment、分页和 `where` 过滤。
- `_schema` 查询根和对象 `_fields` 字段发现。
- 游戏状态读取由主线程执行。
- 游戏状态修改通过后续任务/命令接口表达。

示例：

```graphql
query ObserveFactory {
  metadata {
    gameTick
    localPlanetId
  }
  game {
    gameName
    gameTick
  }
  player {
    planetId
    uPosition {
      x
      y
      z
    }
  }
  inventory {
    grids(limit: 20) {
      itemId
      count
    }
  }
  factory {
    entityCursor
    entityPool(limit: 20) {
      id
      protoId
      pos {
        x
        y
        z
      }
    }
  }
}
```

过滤示例：

```graphql
query FilterFactory {
  factory {
    entityPool(where: { id_gt: 0, protoId_in: [2301, 2302] }, limit: 20) {
      id
      protoId
    }
  }
}
```

`where` 条件按 AND 组合，过滤在分页前执行。条件字段读取失败时视为匹配失败。

分页示例：

```graphql
query PagedEntities {
  firstPage: factory {
    entityPool(limit: 100, offset: 0) {
      id
      protoId
    }
  }
  secondPage: factory {
    entityPool(limit: 100, offset: 100) {
      id
      protoId
    }
  }
}
```

## 任务队列

系统只允许一个任务队列，因为游戏内执行始终是顺序的。外部 Agent 可以提交多个任务，但任务只能追加到同一个队列中，队列按提交顺序执行。

任务提交使用 REST JSON 接口：

```http
POST /tasks
```

任务取消使用：

```http
POST /tasks/{id}/cancel
```

任务接口不使用 GraphQL 写操作。`/game/state` 只负责只读状态查询，任务提交、取消和命令执行通过独立 REST 接口表达。

查询队列中的任务时使用 `GET /tasks`：

```json
{
  "tasks": [
    {
      "id": "task-id",
      "status": "RUNNING",
      "queueIndex": 0,
      "currentCommandIndex": 1,
      "stopOnFailure": true,
      "commands": [
        {
          "id": "place-miner",
          "type": "placeBuilding",
          "status": "RUNNING",
          "phase": "waitingBuilt",
          "startedAt": "2026-06-13T12:00:00Z",
          "completedAt": null,
          "errorCode": null,
          "errorMessage": null
        }
      ]
    }
  ]
}
```

任务状态：

- `QUEUED`：已入队，等待执行。
- `RUNNING`：正在执行。
- `SUCCEEDED`：全部命令执行成功。
- `FAILED`：执行失败。
- `CANCEL_REQUESTED`：收到取消请求，等待安全停止。
- `CANCELLED`：已取消。

取消规则：

- `QUEUED` 任务可以直接取消。
- `RUNNING` 任务只能在当前原子命令完成或到达安全中断点后取消。
- 取消或超时时，移动、采集和自动靠近类建造命令会清理伊卡洛斯当前玩家订单。
- 已完成的任务保留最终状态。
- 已完成、失败、跳过或取消的命令写入历史记录，通过 `GET /history` 查询；成功命令的 `result` 会保留用于排查和回放实体引用。

## 命令范围

第一阶段命令包括：

- `moveTo`：移动伊卡洛斯到指定位置。
- `mineTarget`：直接采集当前行星上的矿脉或地面资源。
- `autoReplenishMechaFuel`：调用 `Mecha.AutoReplenishFuelAll()`，把背包中的可用燃料补充到机甲燃烧室。
- `entityFastFillIn`：调用 `PlanetFactory.EntityFastFillIn()`，对当前行星建筑实体执行游戏原生快速填充。
- `entityFastTakeOut`：调用 `PlanetFactory.EntityFastTakeOut()`，对当前行星实体执行游戏原生快速取出。
- `dismantleEntity`：调用游戏原生拆除逻辑拆除已建成实体，并回收建筑和实体内部物品。
- `craftInventory`：在背包中制造指定物品。外部优先传入目标 `itemId` 和产物数量，Mod 通过游戏内部手搓配方解析和 `MechaForge` 递归判定材料；外部不需要提前感知或传入配方。材料不足时返回递归汇总后的所有缺失物品。
- `researchTech`：通过游戏原生 `EnqueueTech` 选择正常研究目标，等待当前局研究系统自然上传 hash 和解锁。
- `buyoutTech`：显式使用跨存档结转的 `PropertySystem` 元数据买断科技，只用于加快测试或玩家明确要求的跳过流程。
- `placeBuilding`：放置建筑。
- `placeBelt`：铺设传送带。
- `placeSorter`：放置分拣器。
- `setRecipe`：设置生产设施配方。
- `setLabResearchMode`：把矩阵研究站切换到游戏原生研究模式。
- `waitUntil`：等待条件满足。

命令作为任务输入和 `task.commands` 输出中的嵌套对象。命令必须按数组顺序执行，当前命令未结束前不得开始下一条命令。服务端不得并行执行、重排或提前执行后续命令。

建造命令的成功标准是游戏内建造完成，而不是仅创建预建。`placeBuilding`、`placeBelt` 和 `placeSorter` 在执行过程中可以进入移动靠近、创建预建、等待无人机建成等阶段，但只有对应预建被游戏建造成实体后才算命令成功。

移动与建造的关系：

- `moveTo` 是独立命令，用于外部 Agent 主动控制伊卡洛斯移动。
- 建造、采集、填充和拆除命令没有移动策略参数。
- 目标在内部允许的命令下发半径内时，命令可以先移动靠近，再调用游戏原生逻辑。
- 目标超过命令下发半径时，命令必须失败并返回 `out_of_range`；外部 Agent 需要先显式提交 `moveTo`。
- 自动靠近不得绕过游戏规则，最终建造、采集、填充或拆除仍必须通过游戏原生逻辑完成。

AI Agent 需要关注普通玩家通过屏幕提示感知到的特殊对象。查询接口不为太空舱增加独立状态对象，任务接口也不为它增加专用命令；Agent 应在开局和关键循环查询中读取 `factory.vegePool(where: { id_gt: 0 protoId: 9999 })`，并同时查看 `warningSystem` 的 warning/broadcast 摘要作为辅助提示。无论新游戏是否跳过序幕，只要查询到太空舱就应先回收；回收时使用普通 `mineTarget`，传入 `targetType = "vege"` 和查询得到的 `targetId`。

第一阶段 Agent 的产线选址规则：

- 手动采集优先距离近；自动化采矿应综合矿脉数量、距离、附近空间和传送带长度。
- 采矿机位置应尽量覆盖更多矿脉点，以提升采矿速度并降低单个矿脉枯竭导致停线的风险。
- 发电机优先靠近采矿机、熔炉等用电建筑；可直接覆盖时不必额外放电力感应塔。
- 建筑放错位置可以通过 `dismantleEntity` 拆除后重放，游戏内拆除通常返还物品；Agent 应把原生 `TooFar`、`Collide` 等错误作为重新选点和重试的反馈。

机甲燃烧室与建筑发电机燃料槽不是同一套数据结构。`autoReplenishMechaFuel` 只处理 `Mecha.reactorStorage`；建筑实体燃料槽通过 `entityFastFillIn` 交给 `PlanetFactory.EntityFastFillIn()` 处理。储物仓、传送带、矿机和制造设施等实体中的物品通过 `entityFastTakeOut` 交给 `PlanetFactory.EntityFastTakeOut()` 处理。

任务提交示例：

```json
{
  "clientRequestId": "build-iron-line-001",
  "stopOnFailure": true,
  "commands": [
    {
      "id": "move-to-ore",
      "type": "moveTo",
      "planetId": "current",
      "position": { "x": 120.5, "y": 35.0, "z": -80.2 },
      "tolerance": 5,
      "timeoutSeconds": 60
    },
    {
      "id": "place-miner",
      "type": "placeBuilding",
      "itemId": 2301,
      "position": { "x": 123.0, "y": 35.0, "z": -78.5 },
      "rotation": 90,
      "timeoutSeconds": 120
    },
    {
      "id": "place-smelter",
      "type": "placeBuilding",
      "itemId": 2302,
      "position": { "x": 130.0, "y": 35.0, "z": -78.5 },
      "rotation": 90,
      "timeoutSeconds": 120
    },
    {
      "id": "set-iron-recipe",
      "type": "setRecipe",
      "target": { "commandId": "place-smelter" },
      "recipeId": 1
    },
    {
      "id": "wait-iron-output",
      "type": "waitUntil",
      "condition": { "type": "factoryProductDeltaAtLeast", "itemId": 1101, "count": 1 },
      "timeoutSeconds": 120
    }
  ]
}
```

取消任务示例：

```http
POST /tasks/task:42/cancel
```

## 校验与错误

每条命令执行前应做最小必要校验，并返回可被 Agent 理解的错误码。结构性校验由 Mod 完成，例如请求格式、任务状态、目标是否在当前行星。科技、物品、地形、碰撞、距离和连接规则尽量交给游戏原生逻辑判断，Mod 不维护第二套建造规则。

常见错误码：

- `MISSING_ITEM`：背包缺少物品。
- `TECH_LOCKED`：科技锁定。
- `RECIPE_LOCKED`：配方锁定。
- `TERRAIN_BLOCKED`：地形阻挡。
- `COLLISION`：建筑碰撞。
- `OUT_OF_RANGE`：目标超出建造或交互范围。
- `PATH_UNREACHABLE`：伊卡洛斯无法到达目标。
- `TIMEOUT`：等待条件超时。
- `INVALID_COMMAND`：命令结构错误。
- `GAME_NOT_READY`：当前不在可执行命令的游戏状态。
- `COMMAND_CANCELLED`：命令因任务取消而停止。

## 验收标准

按实现阶段验收：

M1 只读状态查询完成时，应能验证：

1. `GET /game` 可以判断游戏是否处于可查询对局。
2. `POST /game/state` 可以在同一游戏查询 tick 内按需读取玩家、背包、当前行星、工厂、生产和供电状态。
3. `POST /game/state` 查询由主线程读取游戏对象并直接生成最终 JSON，HTTP 线程返回主线程结果。
4. 大列表支持 `limit` 和 `offset`，字段读取失败返回 `null`，复杂对象默认展开一层可序列化字段。
5. `GET /tasks` 可以查询内存中的待执行或执行中任务；M1 可以为空列表。
6. `GET /history` 可以查询 SQLite 中的历史命令；M1 可以为空列表。

任务队列完成时，应能验证：

1. 外部程序通过 `POST /tasks` 提交任务后，任务进入唯一队列。
2. 队列按任务提交顺序执行，任务内命令按数组顺序执行。
3. `POST /tasks/{id}/cancel` 可以取消 `QUEUED` 任务，并让 `RUNNING` 任务在安全点停止。
4. 命令成功、失败、跳过或取消后写入 `GET /history` 可查询的历史记录。

行星内生产线控制完成时，应能验证：

1. 一个简单铁块生产线任务可以在当前行星内完成。
2. 建造命令等待游戏内建造完成后才返回成功。
3. 缺少科技、材料、可建造地形或存在碰撞时，任务失败并返回明确错误。
4. 目标在命令下发半径内但当前交互距离不足时，建造或采集命令可以先移动靠近，再由游戏原生逻辑完成最终校验和执行；超过下发半径时返回 `out_of_range`。
