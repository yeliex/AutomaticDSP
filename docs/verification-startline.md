# 开局闭环验证

本文记录第一阶段开局闭环的手动验证流程。它用于验证 AI Agent 能在正常游戏会话中完成：

1. 新建不跳过序幕的游戏。
2. 在 `prologue` 状态下查询太空舱 `VegeData`。
3. 回收太空舱。
4. 把背包燃料补入机甲燃烧室。
5. 继续查询背包、矿脉和任务结果。

## 前置条件

- 通过 Steam 正常启动《戴森球计划》，不要直接运行 `DSPGAME.exe`。直接运行可能导致 `SteamAPI_Init() failed`，游戏会自行退出。
- AutomaticDSP 已部署到 `BepInEx/plugins/AutomaticDSP`。
- `GET http://127.0.0.1:39270/game` 能返回 JSON。

## 1. 创建不跳过序幕的新游戏

```powershell
$base = "http://127.0.0.1:39270"
Invoke-RestMethod "$base/game"

$newGame = @{
  skipPrologue = $false
  isCombatMode = $false
  isPeaceMode = $true
  galaxySeed = 58546236
} | ConvertTo-Json

Invoke-RestMethod "$base/game" -Method Post -ContentType "application/json" -Body $newGame
```

等待 `GET /game` 返回 `ready = true`。此时 `status` 可以是 `prologue`。

## 2. 查询开局对象

```powershell
$query = @'
query ObserveStarter {
  game { status gameTick localPlanetId localPlanetName }
  player {
    planetId
    position { x y z }
    interactionRange
  }
  mecha {
    reactorEnergy
    reactorItemId
    reactorStorage {
      items {
        itemId
        name
        count
      }
      grids(where: { itemId_gt: 0 }, limit: 12) {
        itemId
        count
        inc
      }
    }
  }
  inventory {
    items {
      itemId
      name
      count
      inc
      slots
    }
    summary {
      totalItemCount
      distinctItemCount
    }
    grids(where: { itemId_gt: 0 }, limit: 80) {
      itemId
      count
      inc
      filter
      stackSize
    }
  }
  factory {
    vegePool(where: { id_gt: 0 protoId: 9999 }, limit: 1) {
      id
      protoId
      pos { x y z }
      scl { x y z }
    }
    veinPool(where: { id_gt: 0 amount_gt: 0 }, limit: 32) {
      id
      type
      productId
      amount
      pos { x y z }
    }
  }
  warningSystem {
    activeWarnings {
      signalId
      objectId
      localPosition { x y z }
    }
    activeBroadcasts {
      vocal
      context
      localPosition { x y z }
    }
  }
}
'@

$observe = Invoke-RestMethod "$base/game/state" -Method Post -ContentType "application/json" -Body (@{ query = $query } | ConvertTo-Json -Depth 20)
$observe | ConvertTo-Json -Depth 20
```

预期：

- `game.status` 是 `prologue` 或 `running`。
- 新游戏开局应先查询到一条 `protoId = 9999` 的飞行仓记录；后续目标必须等它回收后再执行。
- `warningSystem.activeBroadcasts` 可能包含 `SpaceCapsuleLanding`，但这只是辅助提示。
- `inventory.items` / `mecha.reactorStorage.items` 返回按物品聚合的数量，`grids` 返回原始槽位细节。

## 3. 回收太空舱并补机甲燃料

```powershell
$capsule = $observe.data.factory.vegePool | Select-Object -First 1
if ($null -eq $capsule) {
  throw "未查询到开局飞行仓；本验证应停止并先确认对局是否已经回收或状态查询是否缺失 vegePool protoId=9999。"
}

$commands = @(
  @{
    id = "recycle-capsule"
    type = "mineTarget"
    targetType = "vege"
    targetId = [int]$capsule.id
    untilDepleted = $true
    timeoutSeconds = 180
  },
  @{
    id = "fill-mecha-reactor"
    type = "autoReplenishMechaFuel"
    timeoutSeconds = 30
  }
)

$task = @{
  commands = $commands
} | ConvertTo-Json -Depth 20

$created = Invoke-RestMethod "$base/tasks" -Method Post -ContentType "application/json" -Body $task
$taskId = $created.task.id

do {
  Start-Sleep -Seconds 1
  $state = Invoke-RestMethod "$base/tasks/$taskId"
  $state.task.status
} while ($state.task.status -in @("QUEUED", "RUNNING", "CANCEL_REQUESTED"))

$state | ConvertTo-Json -Depth 20
```

预期：

- `recycle-capsule` 成功时结果包含 `depleted = true` 和采集得到的 `items`。
- `fill-mecha-reactor` 成功时结果包含 `movedItems` 和 `reactorItems`。
- 若提交前没有查到太空舱，本验证直接停止。若提交后对象已不存在，`mineTarget` 会返回 `target_not_found`，此时应重新查询 `inventory` 和 `mecha.reactorStorage` 判断是否已经回收。

## 4. 验证后续基础生产能力

继续使用 `mineTarget` 采矿、`craftInventory` 手搓、`placeBuilding` / `placeBelt` / `placeSorter` / `setRecipe` 建线。铁块产线与后续蓝糖闭环的完成依据见 [行星内生产闭环验收](verification-production-line.md)。

采矿验证应显式传入期望数量，例如 `itemId = 1001`、`count = 50`。`mineTarget` 也接受 `itemCount` 作为明确字段；`count` 是面向 Agent 的常用别名。任务结果中的 `items[].gained` 应等于本次新增数量，`remaining = 0` 表示已经达到目标数量。
