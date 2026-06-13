# AutomaticDSP 需求说明

> 当前 M1 实现以 `docs/development-plan.md` 为准：`GET /state/game` 返回轻量游戏状态，`POST /state` 使用 GraphQL 字段选择 DSL 按需查询游戏状态，`GET /tasks` 与 `GET /history` 独立返回任务和历史。
> M1 状态查询只保存在内存中，不默认输出快照文件；历史命令写入 `BepInEx/cache/AutomaticDSP/data/history.sqlite`。

## 背景

AutomaticDSP 是一个用于《戴森球计划》的自动化控制 Mod。目标是让外部 AI Agent 能够程序化读取游戏状态，并向游戏提交顺序执行的任务，从而逐步完成生产线建设、物流建设和戴森球建设。

第一阶段聚焦“行星内生产线建立”。Mod 不负责高级规划本身，而是提供可靠、可查询、可验证的游戏内状态与指令执行能力。

## 目标

第一阶段需要做到：

1. 外部程序可以通过 GraphQL 字段选择 DSL 一次性查询同一游戏查询 tick 内的多个游戏状态。
2. 外部程序可以提交任务到唯一顺序任务队列。
3. 游戏内按顺序执行任务中的命令。
4. 外部程序可以把 `task` 作为实体查询状态，也可以取消任务。
5. 行星内生产线建设命令遵守科技、背包、地形、建造距离和游戏内建造流程。

## 非目标

第一阶段不做：

- 跨星际物流自动化。
- 戴森球节点、框架、壳层自动规划。
- 完整 AI 规划器。
- 多队列并行执行。
- 多人模式兼容保证。
- 默认绕过科技、材料、距离或地形限制。
- 直接修改存档来伪造建造成果。

## 核心概念

### 状态查询

`POST /state` 使用 GraphQL 查询语法作为字段选择 DSL。它不是完整 GraphQL 服务，不提供 schema、自省、resolver 框架或 mutation。

一次 `/state` 请求会被放入主线程查询队列。游戏主线程在查询 tick 内直接按该请求的字段、别名和分页参数生成最终 JSON，HTTP 线程只等待并返回结果，不再做二次字段投影。

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
任务不放在 `/state` 查询里。待执行或执行中的任务通过 `GET /tasks` 查询，历史任务通过 `GET /history` 查询。

`task_queue` 不是对外实体。唯一队列通过 `GET /tasks` 获得，返回结果中的每一项都是一个任务。

`command` 也不是对外实体。命令是 task 列表项中的嵌套对象，每一项都是一条命令。需要查看命令状态时，查询对应 task 的 `commands` 字段。

后续阶段可以增加 `logisticStation`、`star`、`dysonSphere` 等实体。

## GraphQL 查询

查询协议采用 GraphQL 查询语法作为字段选择 DSL，而不是 SQL 或自定义 JSON DSL。

选择 GraphQL 的原因：

- 一次查询可以同时获取玩家、背包、建筑、生产、电力等游戏状态。
- 字段投影天然适合 AI Agent 按需取数。
- 嵌套结构不需要摊平成独立表。
- 单个请求在同一个游戏查询 tick 内读取，减少前后状态不一致。

第一阶段查询必须支持：

- 多 root 字段组合查询。
- 嵌套字段投影。
- `limit` 和 `offset` 分页。
- 不存在或不可读字段返回 `null`。
- 复杂对象未选择子字段时返回 `{}`。

第一阶段不支持：

- 任意脚本执行。
- 查询时修改游戏状态。
- 无限制全量导出。
- GraphQL schema、自省、变量校验、业务字段校验。
- `where`、`orderBy`、空间过滤等高级参数。

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

队列不作为独立实体暴露。查询队列中的任务时使用 `GET /tasks`：

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

- `QUEUED`：已入队，尚未执行。
- `RUNNING`：正在执行。
- `SUCCEEDED`：全部命令执行成功。
- `FAILED`：执行失败。
- `CANCEL_REQUESTED`：收到取消请求，等待安全停止。
- `CANCELLED`：已取消。

取消规则：

- `QUEUED` 任务可以直接取消。
- `RUNNING` 任务只能在当前原子命令完成或到达安全中断点后取消。
- 已完成的任务不能取消，只能查询最终状态。

## 命令范围

第一阶段命令包括：

- `moveTo`：移动伊卡洛斯到指定位置。
- `craftInventory`：在背包中制造指定物品。
- `placeBuilding`：放置建筑。
- `placeBelt`：铺设传送带。
- `placeSorter`：放置分拣器。
- `setRecipe`：设置生产设施配方。
- `waitUntil`：等待条件满足。

命令不是 GraphQL 顶层实体，只存在于任务输入和 `task.commands` 输出中。

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
- `TECH_LOCKED`：科技未解锁。
- `RECIPE_LOCKED`：配方未解锁。
- `TERRAIN_BLOCKED`：地形不可建造。
- `COLLISION`：建筑碰撞。
- `OUT_OF_RANGE`：目标超出建造或交互范围。
- `PATH_UNREACHABLE`：伊卡洛斯无法到达目标。
- `TIMEOUT`：等待条件超时。
- `GRAPHQL_VALIDATION_ERROR`：GraphQL 请求不合法。
- `INVALID_COMMAND`：命令结构不合法。

## 验收标准

第一阶段完成时，应能验证：

1. `GET /state/game` 可以判断游戏是否处于可查询对局。
2. `POST /state` 可以在同一游戏查询 tick 内按需读取玩家、背包、当前行星、工厂、生产和供电状态。
3. `POST /state` 查询由主线程读取游戏对象并直接生成最终 JSON，HTTP 线程不做二次字段投影。
4. 大列表支持 `limit` 和 `offset`，不存在字段返回 `null`，复杂对象未展开时返回 `{}`。
5. `GET /tasks` 可以查询内存中的待执行或执行中任务；第一阶段可以为空列表。
6. `GET /history` 可以查询 SQLite 中的历史命令；第一阶段可以为空列表。
7. 外部程序后续提交任务后，任务进入唯一队列，队列按顺序执行，不并行执行多个任务。
8. 后续建造阶段中，一个简单铁块生产线任务可以在当前行星内完成。
9. 后续建造阶段中，缺少科技、材料或可建造地形时，任务失败并返回明确错误。
