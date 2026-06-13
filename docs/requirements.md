# AutomaticDSP 需求说明

> 当前 M1 实现以 `docs/development-plan.md` 为准：`GET /game` 返回轻量游戏状态，`POST /game/state` 使用 GraphQL 字段选择 DSL 按需查询游戏状态，`GET /tasks` 与 `GET /history` 独立返回任务和历史。
> M1 状态查询结果保存在内存中；历史命令写入 `BepInEx/cache/AutomaticDSP/data/history.sqlite`。

## 背景

AutomaticDSP 是一个用于《戴森球计划》的自动化控制 Mod。目标是让外部 AI Agent 能够程序化读取游戏状态，并向游戏提交顺序执行的任务，从而逐步完成生产线建设、物流建设和戴森球建设。

第一阶段聚焦“行星内生产线建立”。高级规划由外部 AI Agent 完成，Mod 提供可靠、可查询、可验证的游戏内状态与指令执行能力。

## 目标

第一阶段需要做到：

1. 外部程序可以通过 GraphQL 字段选择 DSL 一次性查询同一游戏查询 tick 内的多个游戏状态。
2. 外部程序可以提交任务到唯一顺序任务队列。
3. 游戏内按顺序执行任务中的命令。
4. 外部程序可以把 `task` 作为实体查询状态，也可以取消任务。
5. 行星内生产线建设命令遵守科技、背包、地形、建造距离和游戏内建造流程。

## 阶段边界

第一阶段聚焦行星内生产线建立，以下能力进入后续阶段：

- 跨星际物流自动化。
- 戴森球节点、框架、壳层自动规划。
- 完整 AI 规划器。
- 多队列并行执行。
- 多人模式兼容保证。
- 遵守科技、材料、距离和地形限制。
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
待执行或执行中的任务通过 `GET /tasks` 查询，历史任务通过 `GET /history` 查询。

唯一任务队列通过 `GET /tasks` 获得，返回结果中的每一项都是一个任务。

命令作为 task 列表项中的嵌套对象返回，每一项都是一条命令。需要查看命令状态时，查询对应 task 的 `commands` 字段。

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

查询队列中的任务时使用 `GET /tasks`：

```json
{
  "tasks": [
    {
      "id": "task-id",
      "status": "RUNNING",
      "queueIndex": 0,
      "currentCommandIndex": 1,
      "commands": []
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
- 已完成的任务保留最终状态。

## 命令范围

第一阶段命令包括：

- `moveTo`：移动伊卡洛斯到指定位置。
- `craftInventory`：在背包中制造指定物品。
- `placeBuilding`：放置建筑。
- `placeBelt`：铺设传送带。
- `placeSorter`：放置分拣器。
- `setRecipe`：设置生产设施配方。
- `waitUntil`：等待条件满足。

命令作为任务输入和 `task.commands` 输出中的嵌套对象。

任务提交示例：

```graphql
mutation EnqueueIronLine {
  enqueueTask(
    input: {
      clientRequestId: "build-iron-line-001"
      stopOnFailure: true
      commands: [
        {
          id: "move-to-ore"
          moveTo: {
            planetId: CURRENT_PLANET
            position: { x: 120.5, y: 35.0, z: -80.2 }
            tolerance: 5
          }
        }
        {
          id: "place-miner"
          placeBuilding: {
            itemId: 2301
            position: { x: 123.0, y: 35.0, z: -78.5 }
            rotation: 90
          }
        }
        {
          id: "place-smelter"
          placeBuilding: {
            itemId: 2302
            position: { x: 130.0, y: 35.0, z: -78.5 }
            rotation: 90
          }
        }
        {
          id: "set-iron-recipe"
          setRecipe: {
            target: { taskCommandId: "place-smelter" }
            recipeId: 1
          }
        }
        {
          id: "wait-iron-output"
          waitUntil: {
            condition: { nearbyItemProduced: { itemId: 1101, count: 1 } }
            timeoutSeconds: 120
          }
        }
      ]
    }
  ) {
    task {
      id
      status
      queueIndex
    }
  }
}
```

取消任务示例：

```graphql
mutation CancelTask {
  cancelTask(id: "task:42") {
    task {
      id
      status
    }
  }
}
```

## 校验与错误

每条命令执行前应做最小必要校验，并返回可被 Agent 理解的错误码。

常见错误码：

- `MISSING_ITEM`：背包缺少物品。
- `TECH_LOCKED`：科技锁定。
- `RECIPE_LOCKED`：配方锁定。
- `TERRAIN_BLOCKED`：地形阻挡。
- `COLLISION`：建筑碰撞。
- `OUT_OF_RANGE`：目标超出建造或交互范围。
- `PATH_UNREACHABLE`：伊卡洛斯无法到达目标。
- `TIMEOUT`：等待条件超时。
- `GRAPHQL_VALIDATION_ERROR`：GraphQL 请求格式错误。
- `INVALID_COMMAND`：命令结构错误。

## 验收标准

第一阶段完成时，应能验证：

1. `GET /game` 可以判断游戏是否处于可查询对局。
2. `POST /game/state` 可以在同一游戏查询 tick 内按需读取玩家、背包、当前行星、工厂、生产和供电状态。
3. `POST /game/state` 查询由主线程读取游戏对象并直接生成最终 JSON，HTTP 线程返回主线程结果。
4. 大列表支持 `limit` 和 `offset`，字段读取失败返回 `null`，复杂对象默认展开一层可序列化字段。
5. `GET /tasks` 可以查询内存中的待执行或执行中任务；第一阶段可以为空列表。
6. `GET /history` 可以查询 SQLite 中的历史命令；第一阶段可以为空列表。
7. 外部程序后续提交任务后，任务进入唯一队列，队列按顺序执行。
8. 后续建造阶段中，一个简单铁块生产线任务可以在当前行星内完成。
9. 后续建造阶段中，缺少科技、材料或可建造地形时，任务失败并返回明确错误。
