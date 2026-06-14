# 开局到铁块产线闭环示例

本文面向外部 AI Agent，记录一条已经实机验证过的第一阶段闭环：从新游戏开局开始，回收飞行仓、补充机甲燃料、正常研究基础科技，最后建立一条当前行星内铁块产线并等待产出。

## 验证目标

1. 先查询并回收 `factory.vegePool protoId = 9999` 的飞行仓。
2. 使用 `autoReplenishMechaFuel` 把可用燃料放入机甲燃烧室。
3. 使用 `craftInventory` 和 `researchTech` 正常解锁科技，不使用 `buyoutTech`。
4. 使用 `mineTarget` 采集指定数量的铁矿和铜矿。
5. 使用 `placeBuilding` / `placeBelt` / `placeSorter` / `setRecipe` 建立铁块产线。
6. 使用 `waitUntil(factoryProductDeltaAtLeast)` 确认当前行星工厂产出铁块。

## 已验证结果

最近一次实机验证使用新档 `23397062-64-A10`，和平模式，跳过序幕。

- 飞行仓 `protoId = 9999` 被普通 `mineTarget(targetType = "vege")` 回收。
- 回收获得 `3` 个液氢燃料棒、`10` 铁块、`10` 磁铁和 `10` 铜块。
- `autoReplenishMechaFuel` 成功把液氢燃料棒放入机甲燃烧室。
- `researchTech` 正常解锁 `1001 电磁学`、`1401 自动化冶金` 和 `1601 基础物流系统`，结果均为 `usesMetadata = false`。
- `mineTarget` 使用 `count = 50` 和 `count = 25` 分别采到 `50` 铁矿和 `25` 铜矿。
- 风力涡轮机、电力感应塔、采矿机、传送带、熔炉和分拣器均由任务命令建造完成。
- `waitUntil` 等到 `itemId = 1101` 的工厂产出增量达到 `1`。

## 观察查询

开局第一轮观察应同时查询飞行仓、玩家位置、背包聚合、燃烧室聚合和附近矿脉。手动采集目标优先按 `distanceToPlayer` 选最近目标。自动化产线目标不能只按距离排序，还应考虑矿脉数量、附近空间和传送带长度。

```graphql
query OpeningObserve {
  game { status gameTick localPlanetId localPlanetName }
  player { planetId position { x y z } }
  inventory {
    items { itemId name count }
    summary { totalItemCount distinctItemCount }
  }
  mecha {
    reactorStorage {
      items { itemId name count }
    }
  }
  factory {
    capsule: vegePool(where: { id_gt: 0 protoId: 9999 }, limit: 1) {
      id
      protoId
      distanceToPlayer
      pos { x y z }
    }
    iron: veinPool(where: { id_gt: 0 amount_gt: 0 productId: 1001 }, limit: 16) {
      id
      productId
      amount
      distanceToPlayer
      pos { x y z }
    }
    copper: veinPool(where: { id_gt: 0 amount_gt: 0 productId: 1002 }, limit: 16) {
      id
      productId
      amount
      distanceToPlayer
      pos { x y z }
    }
  }
}
```

如果查询到飞行仓，必须先回收它。只有确认飞行仓不存在或已经回收后，Agent 才继续后续采矿、手搓、研究和建造计划。

## 科技推进任务

下面的命令结构展示了开局科技推进顺序。`targetId` 必须来自本局查询结果，不能复用示例 ID。回收飞行仓后应先重新观察 `inventory.items`；如果背包已有材料足够完成下一步，就直接手搓和研究，不要先采矿。飞行仓给的磁铁和铜块足够手搓 10 个磁线圈，因此 `1001 电磁学` 可以先完成，再根据基础物流和产线建造的缺口采矿。

```json
{
  "clientRequestId": "opening-tech-flow",
  "stopOnFailure": true,
  "commands": [
    {
      "id": "recycle-capsule",
      "type": "mineTarget",
      "targetType": "vege",
      "targetId": 4084,
      "untilDepleted": true,
      "timeoutSeconds": 180
    },
    {
      "id": "fill-mecha-reactor",
      "type": "autoReplenishMechaFuel",
      "timeoutSeconds": 30
    },
    {
      "id": "craft-coils-for-electromagnetism",
      "type": "craftInventory",
      "itemId": 1202,
      "count": 10,
      "timeoutSeconds": 180
    },
    {
      "id": "research-electromagnetism",
      "type": "researchTech",
      "techId": 1001,
      "waitForUnlock": true,
      "timeoutSeconds": 180
    },
    {
      "id": "mine-nearby-iron",
      "type": "mineTarget",
      "targetType": "vein",
      "targetId": 3,
      "itemId": 1001,
      "count": 50,
      "timeoutSeconds": 240
    },
    {
      "id": "mine-nearby-copper",
      "type": "mineTarget",
      "targetType": "vein",
      "targetId": 11,
      "itemId": 1002,
      "count": 25,
      "timeoutSeconds": 240
    },
    {
      "id": "craft-coils-for-logistics",
      "type": "craftInventory",
      "itemId": 1202,
      "count": 10,
      "timeoutSeconds": 240
    },
    {
      "id": "craft-circuits",
      "type": "craftInventory",
      "itemId": 1301,
      "count": 20,
      "timeoutSeconds": 240
    },
    {
      "id": "craft-gears",
      "type": "craftInventory",
      "itemId": 1201,
      "count": 10,
      "timeoutSeconds": 240
    },
    {
      "id": "research-automatic-metallurgy",
      "type": "researchTech",
      "techId": 1401,
      "waitForUnlock": true,
      "timeoutSeconds": 240
    },
    {
      "id": "research-basic-logistics",
      "type": "researchTech",
      "techId": 1601,
      "waitForUnlock": true,
      "timeoutSeconds": 240
    }
  ]
}
```

`mineTarget.count` 是面向 Agent 的常用数量字段，`itemCount` 是同义的明确字段。命令成功时检查 `result.items[].gained` 和 `result.remaining`。

## 建线策略

科技完成后，重新查询背包、科技状态和铁矿簇。科技奖励会提供第一条基础产线所需的采矿机、熔炉、传送带、分拣器和基础电力建筑。

建线坐标应由 Agent 根据当前行星局部坐标生成，不能复用示例种子的坐标。推荐顺序是：

1. 在铁矿簇附近 `moveTo`。
2. 放置风力涡轮机和电力感应塔。
3. 放置采矿机。
4. 从采矿机端口铺设传送带。
5. 放置熔炉并设置铁块配方 `recipeId = 1`。
6. 查询实际传送带实体，选择离熔炉较近的 belt 段。
7. 放置分拣器，从 belt 输入到熔炉输出。
8. 等待 `factoryProductDeltaAtLeast itemId = 1101 count = 1`。

## 选址规则

选择矿簇时按以下因素综合排序：

1. 矿脉数量：优先选择矿点密集、总储量较高的矿簇。
2. 距离：离玩家和已有建筑越近，移动时间和传送带消耗越低。
3. 附近空间：矿簇周围要能放下采矿机、传送带、熔炉和电力建筑。
4. 连接成本：传送带越短越好，分拣器连接距离要留在游戏规则允许范围内。

放置采矿机时，应优先覆盖尽量多的矿脉点，而不是只贴最近的一个矿脉。更多覆盖点意味着更高采矿速度，也能降低单个矿脉枯竭导致产线中断的风险。

发电机优先放在采矿机、熔炉等用电建筑附近。风力涡轮机如果能直接覆盖关键建筑，就不必额外拉一个电力感应塔；只有覆盖范围不足或后续扩展需要时再补电塔。

建筑放错位置时，规划上可以拆掉重放。游戏内拆除建筑会返还物品，通常没有材料损失，所以 Agent 不需要因为一次 `TooFar` 或 `Collide` 就放弃当前矿簇；更合理的做法是重新查询实体和地面对象，用 `dismantleEntity` 拆除错误建筑或换点后继续。

```json
{
  "commands": [
    {
      "id": "remove-bad-smelter",
      "type": "dismantleEntity",
      "entityId": 17,
      "timeoutSeconds": 60
    }
  ]
}
```

如果要拆除同一任务中刚铺出的某段传送带，可以引用 `placeBelt` 的 `entityIds` 结果：

```json
{
  "commands": [
    {
      "id": "remove-first-belt",
      "type": "dismantleEntity",
      "target": { "commandId": "belt-from-miner", "entityIndex": 0 },
      "timeoutSeconds": 60
    }
  ]
}
```

建造失败时不要绕过规则。第一版建造命令会调用游戏原生校验，常见失败包括：

- `out_of_range` / `TooFar`：实体距离或分拣器连接距离过远。应移动、调整熔炉位置，或选择更近的 belt 段。
- `collision` / `Collide`：建造位置与已有实体、矿物、地形或网格冲突。应重新查询实体和地面对象后换点。
- `item_not_available` 或科技相关错误：重新查询 `inventory.items` 和 `techs`，不要假设上一步计划仍成立。

这些失败是 Agent 规划循环的一部分。正确做法是观察状态、调整坐标或连接对象，再提交下一段顺序任务。
