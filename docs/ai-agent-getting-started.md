# AI Agent 开局流程

本文面向外部 AI Agent，说明如何用 AutomaticDSP 从新游戏开始建立第一条行星内基础生产线。

## 基本循环

Agent 应采用“观察状态 -> 提交顺序任务 -> 等待任务结束 -> 再观察状态”的闭环。

1. 调用 `GET /game`，确认 `ready = true`。开局太空舱流程可能处于 `status = prologue`。
2. 调用 `POST /game/state` 获取当前玩家、背包、工厂和提示状态。
3. 始终先检查当前行星是否存在 `factory.vegePool protoId = 9999` 的飞行仓；如果存在，必须先用普通 `mineTarget` 回收它，再补充机甲燃料和规划其他目标。
4. 调用 `POST /tasks` 提交顺序命令队列。
5. 轮询 `GET /tasks/{id}`，直到任务 `SUCCEEDED` 或 `FAILED`。
6. 再次查询状态，不要假设上一轮计划仍然成立。

任务命令按数组顺序执行。当前命令未完成前，后续命令不会开始。

## 每轮推荐观察

查询接口尽量与游戏对象对齐。Agent 不应等待专门的 `spaceCapsule` 状态对象；开局特殊物品需要通过游戏原生对象查询。无论新游戏是否跳过序幕，都应把飞行仓回收作为开局第一优先级，因为它提供燃料和基础材料，会影响后续采集、手搓和研究节奏。

```graphql
query ObserveForStarterLine {
  metadata { gameTick localPlanetId }
  game { status gameTick localPlanetId localPlanetName }
  player {
    planetId
    position { x y z }
    movementState
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
  factoryDetails {
    entityCount
    buildingSummary { itemId itemName count }
    statusSummary { status count }
    items {
      entityId
      protoId
      name
      position { x y z }
      component {
        type
        recipeId
        recipeName
        productId
        productName
        veinCount
        productCount
      }
      status
    }
  }
  research {
    currentTechId
    currentTechName
    hashUploaded
    hashNeeded
    queueLength
    queue { techId name }
    isStalled
  }
  warningSystem {
    hasCriticalWarning
    criticalWarningTexts
    activeWarnings {
      signalId
      objectId
      localPosition { x y z }
    }
    activeBroadcasts {
      vocal
      context
      astroId
      localPosition { x y z }
    }
  }
}
```

`factory.vegePool(where: { protoId: 9999 })` 用于感知开局太空舱。`warningSystem.activeBroadcasts` 可能包含 `SpaceCapsuleLanding` 等提示，但 warning/broadcast 只是辅助线索；可靠对象来源仍是 `vegePool`。

`inventory.items` 和 `mecha.reactorStorage.items` 是对 `grids` 的聚合视图，适合 Agent 快速判断背包和燃烧室中每种物品的总数；`grids` 仍保留原始槽位细节，用于需要过滤器、增产剂或槽位级状态时查询。回收飞行仓后应重新观察这些字段，因为飞行仓会直接给燃料和基础材料。

`factoryDetails` 是当前行星工厂的 Agent 摘要视图，用于快速确认建成实体、配方和状态；`factory` / `localFactory` 仍保留为游戏原始对象。建造或设置配方后，应重新查询 `factoryDetails.items`，检查目标实体是否出现、`component.recipeId` 是否符合预期，以及是否存在 `missingPower`、`materialShortage`、`outputBlocked` 或 `noRecipe`。

## 回收太空舱

太空舱是 `VegeData`，不是矿脉。它的 `protoId` 为 `9999`。开局和关键循环中如果发现该对象，Agent 应暂停其他目标，读取它的 `id`，再用普通 `mineTarget` 采集：

```json
{
  "commands": [
    {
      "id": "recycle-capsule",
      "type": "mineTarget",
      "targetType": "vege",
      "targetId": 123,
      "untilDepleted": true,
      "timeoutSeconds": 120
    }
  ]
}
```

这里的 `targetId` 必须来自上一轮 `factory.vegePool(where: { protoId: 9999 })` 查询结果。命令会发出游戏原生采集订单，并等待对象消失后成功。提交前如果查不到太空舱，通常表示太空舱已回收，或玩家不在对应行星工厂中；提交后如果对象已消失，`mineTarget` 会返回 `target_not_found`。只有确认飞行仓不存在或已经回收后，Agent 才应继续后续采矿、手搓、研究和建造计划。

## 补充机甲燃料

回收太空舱后，Agent 应重新查询背包和 `mecha.reactorStorage`；如果背包里获得了燃料物品，可以调用游戏原生自动补燃料逻辑：

```json
{
  "commands": [
    {
      "id": "fill-mecha-reactor",
      "type": "autoReplenishMechaFuel",
      "timeoutSeconds": 10
    }
  ]
}
```

该命令调用 `Mecha.AutoReplenishFuelAll()`。它只处理机甲燃烧室 `Mecha.reactorStorage`，不处理建筑发电机的 `PowerGeneratorComponent` 燃料槽。命令结果中的 `movedItems` 表示本次实际进入燃烧室的物品。

## 背包优先规则

执行采矿前，Agent 应先根据 `inventory.items`、`items` 和 `recipes` 判断背包已有资源能否满足下一步手搓或研究。能直接完成的目标不要先采矿；例如回收飞行仓后通常已有 10 个磁铁和 10 个铜块，足够手搓 10 个磁线圈并研究 `1001 电磁学`。只有背包资源不足，或 `craftInventory` 返回 `missing_item` 并给出缺口后，才选择附近矿脉采集缺失原料。

## 快速填充建筑实体

建筑实体使用游戏原生 `PlanetFactory.EntityFastFillIn`。例如先放置发电机，再引用该命令结果快速填充：

```json
{
  "commands": [
    {
      "id": "place-generator",
      "type": "placeBuilding",
      "itemId": 2201,
      "position": { "x": 120.0, "y": 35.0, "z": -80.0 },
      "timeoutSeconds": 120
    },
    {
      "id": "fill-generator",
      "type": "entityFastFillIn",
      "target": { "commandId": "place-generator" },
      "fromPackage": true,
      "timeoutSeconds": 30
    }
  ]
}
```

`entityFastFillIn` 不要求 Agent 指定燃料物品。它会把目标实体、背包和游戏当前规则交给 `PlanetFactory.EntityFastFillIn`，实际转移物品通过结果里的 `movedItems` 返回。没有转移任何物品时命令失败，错误码为 `no_item_transferred`。

储物仓、传送带、矿机、熔炉、制造台和研究站等实体中已有物品时，优先使用 `entityFastTakeOut` 通过游戏原生 `PlanetFactory.EntityFastTakeOut` 取到背包，再决定是否需要额外采矿或手搓：

```json
{
  "id": "take-from-storage",
  "type": "entityFastTakeOut",
  "entityId": 19,
  "toPackage": true,
  "timeoutSeconds": 30
}
```

## 基础生产线前置动作

太空舱处理完成后，第一阶段 Agent 可以继续使用：

- `mineTarget`：采集矿脉或普通地面资源。
- `autoReplenishMechaFuel`：把背包中的可用燃料补入机甲燃烧室。
- `entityFastFillIn`：对建筑实体执行游戏原生快速填充。
- `entityFastTakeOut`：从储物仓、传送带或生产实体中执行游戏原生快速取出。
- `dismantleEntity`：拆除已建成实体，并通过游戏原生逻辑回收建筑和内部物品。
- `researchTech`：把科技加入正常研究队列，等待当前局研究系统消耗材料并上传 hash。
- `buyoutTech`：可选加速命令，显式使用跨存档元数据买断科技；正常游玩流程不要默认使用。
- `craftInventory`：按目标 `itemId` 和产物数量手搓物品。
- `placeBuilding` / `placeBelt` / `placeSorter`：建立生产线并等待游戏内建造完成。
- `setRecipe`：设置熔炉、制造台、实验室等设施配方。
- `setLabResearchMode`：把矩阵研究站切换到游戏原生研究模式。
- `waitUntil`：等待背包、生产统计或时间条件满足。

采集、填充、拆除和建造类命令没有移动策略参数。目标在内部允许的下发半径内时，命令可以自动靠近并继续调用游戏原生校验；超过下发半径时命令返回 `out_of_range`，Agent 必须先显式提交 `moveTo`，再重新下发交互或建造命令。

## 产线选址规则

第一阶段产线不需要一次选点完美。游戏内建筑拆除后会返还物品，通常可以把错误位置用 `dismantleEntity` 拆掉重新放置，因此 Agent 应优先形成可验证的短闭环，再根据原生错误和产出状态迭代优化。

矿脉选择不要只看距离。手动采集可以优先最近目标；自动化产线应综合矿脉数量、与玩家/现有建筑的距离、附近可建造空间和传送带长度。距离越远，通常越消耗传送带，也更容易增加电力覆盖和分拣器连接问题。

采矿机位置应优先覆盖尽量多的矿脉点，而不是只贴最近的单个矿点。覆盖更多矿脉可以提升采矿速度，也能避免单个矿脉很快枯竭导致产线停摆。

发电机优先放在采矿机、熔炉等用电建筑附近。风力涡轮机本身能提供供电覆盖时，尽量直接覆盖建筑，减少额外电力感应塔；只有覆盖不到关键建筑时再补电塔。

端到端示例见 `docs/examples/opening-to-iron-line.md`。该示例把飞行仓回收、正常科技推进、采矿数量、铁块产线建造和原生建造失败后的重试策略串在一起。
