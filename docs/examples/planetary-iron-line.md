# 行星内铁块生产线任务示例

这个示例展示第一版任务队列如何表达一条基础铁块生产线：

如果需要从新游戏开始验证完整开局流程，请先看 `opening-to-iron-line.md`。本文只展示已经具备科技、材料和建造物时，单条铁块产线任务如何表达。

- 放置风力涡轮机提供基础供电。
- 放置采矿机。
- 从采矿机端口铺设传送带。
- 放置电弧熔炉。
- 设置铁块配方。
- 放置分拣器，将传送带上的铁矿送入熔炉。
- 等待当前行星工厂统计中铁块产出增加。

坐标必须由外部 Agent 先通过 `/game/state` 查询当前行星地形、矿脉、玩家位置和可建造区域后替换。示例中的 `position` 只是结构示意。选择矿簇时应综合矿脉数量、距离、附近空间和传送带长度；采矿机应尽量覆盖更多矿脉点，发电机优先靠近用电建筑以减少额外电塔。

```json
{
  "clientRequestId": "planetary-iron-line-example",
  "stopOnFailure": true,
  "commands": [
    {
      "id": "move-near-site",
      "type": "moveTo",
      "planetId": "current",
      "position": { "x": 120.0, "y": 35.0, "z": -80.0 },
      "tolerance": 5,
      "timeoutSeconds": 60
    },
    {
      "id": "place-wind-turbine",
      "type": "placeBuilding",
      "itemId": 2203,
      "position": { "x": 118.0, "y": 35.0, "z": -83.0 },
      "rotation": 0,
      "timeoutSeconds": 120
    },
    {
      "id": "place-miner",
      "type": "placeBuilding",
      "itemId": 2301,
      "position": { "x": 122.0, "y": 35.0, "z": -80.0 },
      "rotation": 90,
      "timeoutSeconds": 180
    },
    {
      "id": "belt-from-miner",
      "type": "placeBelt",
      "itemId": 2001,
      "start": { "commandId": "place-miner", "slot": 0 },
      "end": { "x": 130.0, "y": 35.0, "z": -80.0 },
      "timeoutSeconds": 180
    },
    {
      "id": "place-smelter",
      "type": "placeBuilding",
      "itemId": 2302,
      "position": { "x": 132.0, "y": 35.0, "z": -78.5 },
      "rotation": 90,
      "timeoutSeconds": 180
    },
    {
      "id": "set-smelter-iron",
      "type": "setRecipe",
      "target": { "commandId": "place-smelter" },
      "recipeId": 1,
      "timeoutSeconds": 30
    },
    {
      "id": "sorter-belt-to-smelter",
      "type": "placeSorter",
      "itemId": 2011,
      "input": { "commandId": "belt-from-miner", "entityIndex": 3 },
      "output": { "commandId": "place-smelter", "slot": 0 },
      "timeoutSeconds": 180
    },
    {
      "id": "wait-iron-ingot",
      "type": "waitUntil",
      "condition": { "type": "factoryProductDeltaAtLeast", "itemId": 1101, "count": 1 },
      "timeoutSeconds": 240
    }
  ]
}
```

调用建议：

1. 用 `/game/state` 查询当前背包、解锁状态、矿脉和附近实体。
2. 把示例坐标替换为当前行星局部坐标。
3. `POST /tasks` 提交任务。
4. 轮询 `GET /tasks/{id}`，读取每条命令的 `status`、`phase`、`errorCode` 和 `result`。
5. 任务结束后用 `GET /history` 复盘命令历史。
