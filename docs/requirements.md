# AutomaticDSP 需求说明

## 背景

AutomaticDSP 是一个用于《戴森球计划》的自动化控制 Mod。目标是让外部 AI Agent 能够通过程序化接口读取游戏状态，并向游戏提交可顺序执行的任务，从而逐步完成生产线建设、物流建设和戴森球建设。

第一阶段聚焦“行星内生产线建立”。Mod 不负责高级规划本身，而是提供可靠、可查询、可验证的游戏内状态与指令执行能力。

## 目标

第一阶段需要做到：

1. 外部程序可以用 Query DSL 查询游戏内实体。
2. 外部程序可以提交唯一任务队列中的任务。
3. 游戏内按顺序执行任务中的命令。
4. 外部程序可以把任务作为实体查询状态，也可以取消任务。
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

### 实体

查询接口围绕“实体”设计。实体是游戏内或 Mod 运行时可被查询的对象。

第一阶段实体包括：

- `player`：伊卡洛斯位置、移动状态、建造范围、当前星球。
- `inventory`：背包物品、手搓队列、可制造物品。
- `technology`：科技解锁状态。
- `planet`：当前行星、地形和基础元数据。
- `vein`：矿脉点。
- `resource_patch`：矿区聚合。
- `building`：矿机、熔炉、制造台、电塔、仓储等已存在建筑。
- `belt`：传送带片段。
- `sorter`：分拣器。
- `power_network`：供电网络和供电状态。
- `recipe`：配方及解锁状态。
- `item`：物品原型。
- `task_queue`：唯一顺序任务队列。
- `task`：已提交任务。
- `command`：任务中的单条命令。

后续阶段可以增加 `logistic_station`、`star`、`dyson_sphere` 等实体。

### Query DSL

所有状态查询优先通过 Query DSL 完成，而不是为每种状态增加独立接口。DSL 的目标是减少数据传输、降低外部 Agent 的筛选成本，并允许按实体自由查询。

DSL 必须支持：

- 指定实体类型。
- 过滤条件。
- 字段投影。
- 排序。
- 分页或数量限制。
- 当前上下文别名，例如 `current_planet`、`player_position`。
- 空间查询，例如包围盒、半径范围、距离玩家最近。

DSL 第一阶段不支持：

- 任意脚本执行。
- 修改游戏状态。
- 跨实体复杂 join。
- 无限制全量导出。

示例：

```json
{
  "entity": "building",
  "filter": {
    "all": [
      { "field": "planetId", "op": "eq", "value": "current_planet" },
      { "field": "kind", "op": "in", "value": ["miner", "smelter"] },
      {
        "field": "position",
        "op": "within_radius",
        "value": { "center": "player_position", "radius": 80 }
      }
    ]
  },
  "select": ["id", "kind", "prototypeId", "position", "recipeId", "workState"],
  "orderBy": [{ "field": "distanceToPlayer", "direction": "asc" }],
  "limit": 100
}
```

查询任务状态：

```json
{
  "entity": "task",
  "filter": {
    "any": [
      { "field": "status", "op": "eq", "value": "running" },
      { "field": "status", "op": "eq", "value": "queued" }
    ]
  },
  "select": ["id", "status", "currentCommandId", "createdAt", "updatedAt", "error"],
  "limit": 20
}
```

### 任务队列

系统只允许一个任务队列，因为游戏内执行始终是顺序的。外部 Agent 可以提交多个任务，但任务只能追加到同一个队列中，队列按提交顺序执行。

任务状态：

- `queued`：已入队，尚未执行。
- `running`：正在执行。
- `succeeded`：全部命令执行成功。
- `failed`：执行失败。
- `cancel_requested`：收到取消请求，等待安全停止。
- `cancelled`：已取消。

取消规则：

- `queued` 任务可以直接取消。
- `running` 任务只能在当前原子命令完成或到达安全中断点后取消。
- 已完成的任务不能取消，只能查询最终状态。

## 命令范围

第一阶段命令包括：

- `move_to`：移动伊卡洛斯到指定位置。
- `craft_inventory`：在背包中制造指定物品。
- `place_building`：放置建筑。
- `place_belt`：铺设传送带。
- `place_sorter`：放置分拣器。
- `set_recipe`：设置生产设施配方。
- `wait_until`：等待条件满足。
- `cancel_task`：请求取消任务。该能力也可以通过独立取消接口触发。

任务示例：

```json
{
  "id": "build-iron-line-001",
  "commands": [
    {
      "id": "move-to-ore",
      "type": "move_to",
      "planetId": "current_planet",
      "position": { "x": 120.5, "y": 35.0, "z": -80.2 },
      "tolerance": 5
    },
    {
      "id": "place-miner",
      "type": "place_building",
      "itemId": 2301,
      "position": { "x": 123.0, "y": 35.0, "z": -78.5 },
      "rotation": 90
    },
    {
      "id": "place-smelter",
      "type": "place_building",
      "itemId": 2302,
      "position": { "x": 130.0, "y": 35.0, "z": -78.5 },
      "rotation": 90
    },
    {
      "id": "set-iron-recipe",
      "type": "set_recipe",
      "target": { "entity": "building", "id": "place-smelter" },
      "recipeId": 1
    },
    {
      "id": "wait-iron-output",
      "type": "wait_until",
      "condition": {
        "type": "nearby_item_produced",
        "itemId": 1101,
        "count": 1
      },
      "timeoutSeconds": 120
    }
  ]
}
```

## 校验与错误

每条命令执行前应做最小必要校验，并返回可被 Agent 理解的错误码。

常见错误码：

- `missing_item`：背包缺少物品。
- `tech_locked`：科技未解锁。
- `recipe_locked`：配方未解锁。
- `terrain_blocked`：地形不可建造。
- `collision`：建筑碰撞。
- `out_of_range`：目标超出建造或交互范围。
- `path_unreachable`：伊卡洛斯无法到达目标。
- `timeout`：等待条件超时。
- `invalid_query`：Query DSL 不合法。
- `invalid_command`：命令结构不合法。

## 验收标准

第一阶段完成时，应能验证：

1. `POST /query` 可以查询当前玩家、背包、科技、当前行星建筑和任务状态。
2. 外部程序提交任务后，任务进入唯一队列。
3. 队列按顺序执行，不并行执行多个任务。
4. 外部程序可以用 Query DSL 查询某个任务、当前运行任务和历史失败任务。
5. 外部程序可以取消 queued 或 running 任务。
6. 一个简单铁块生产线任务可以在当前行星内完成。
7. 缺少科技、材料或可建造地形时，任务失败并返回明确错误。
