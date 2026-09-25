# AutomaticDSP API 文档

本文描述 AutomaticDSP HTTP API。当前实现包含 `/game`、`/game/state`、存档控制、任务队列接口和 `GET /history` 查询。默认监听地址：

```text
http://127.0.0.1:39270
```

配置项：

- `HTTP.Host`：默认 `127.0.0.1`，需要外部机器访问时可改为 `0.0.0.0`。
- `HTTP.Port`：默认 `39270`。

所有响应默认带 CORS 头，任意路径都支持 `OPTIONS` preflight。

## 通用约定

JSON 响应使用：

```http
Content-Type: application/json; charset=utf-8
```

错误响应格式：

```json
{
  "error": {
    "code": "bad_request",
    "message": "Request body must include a non-empty query string."
  }
}
```

常见状态码：

- `200`：请求成功。
- `204`：`OPTIONS` preflight 成功。
- `400`：请求体或 GraphQL 查询语法错误。
- `404`：路径匹配失败。
- `405`：HTTP 方法匹配失败。
- `409`：游戏处于对局就绪前状态，或任务状态不允许当前操作。
- `504`：等待下一次游戏查询 tick 或控制 tick 超时。

## GET /game

轻量游戏状态探针。这个接口直接返回主线程维护的运行状态，可用于判断是否可以调用 `POST /game/state`。

对局外状态：

```json
{
  "ready": false,
  "status": "menu",
  "controls": {
    "canCreateNewGame": true,
    "canLoadSave": true,
    "canSave": false,
    "canSkipPrologue": false
  },
  "newGameDefaults": {
    "galaxyAlgo": 20200403,
    "galaxySeed": 12345678,
    "galaxySeedText": "12345678",
    "starCount": 64,
    "playerProto": 1,
    "resourceMultiplier": 1,
    "mode": "combat",
    "isPeaceMode": false,
    "isCombatMode": true,
    "isSandboxMode": false,
    "skipPrologue": true,
    "goalLevel": "Full",
    "combatSettings": {}
  },
  "newGameParameters": {
    "setForNewGame": ["galaxyAlgo", "galaxySeed", "starCount", "playerProto", "resourceMultiplier"],
    "mode": ["combat", "peace"],
    "booleans": ["isCombatMode", "combatMode", "isPeaceMode", "peaceMode", "isSandboxMode", "sandbox", "skipPrologue"],
    "goalLevel": ["None", "Off", "Key", "Full"],
    "combatSettings": [
      "aggressiveness",
      "initialLevel",
      "initialGrowth",
      "initialColonize",
      "maxDensity",
      "growthSpeedFactor",
      "powerThreatFactor",
      "battleThreatFactor",
      "battleExpFactor"
    ]
  }
}
```

可查询对局状态：

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

`ready = true` 表示可以调用 `POST /game/state`。当前包括 `status = running`、`paused` 和 `prologue`；`prologue` 下用于让 AI Agent 感知并回收开局太空舱等序幕对象。

`status` 当前取值：

- `loading`
- `running`
- `paused`
- `ended`
- `prologue`
- `cutscene`
- `error`
- `menu`
- `unknown`

`newGameDefaults.galaxySeed` 每次响应会生成一个新的 8 位随机整数，可直接用于 `POST /game`。`galaxySeedText` 是补零后的显示形式。

## POST /game

创建新游戏。只能在 `GET /game` 返回 `status: "menu"` 时调用。请求体可以为空，默认会创建战斗模式、非沙盒、跳过序幕的新游戏。

默认值：

- `galaxyAlgo = UniverseGen.algoVersion`
- `galaxySeed = 8 位随机整数`
- `starCount = 64`
- `playerProto = 1`
- `resourceMultiplier = 1`
- `mode = "combat"`
- `isSandboxMode = false`
- `skipPrologue = true`
- `goalLevel = "Full"`
- `combatSettings = CombatSettings.SetDefault()`

完整请求示例：

```json
{
  "galaxyAlgo": 20200403,
  "galaxySeed": 12345678,
  "starCount": 64,
  "playerProto": 1,
  "resourceMultiplier": 1,
  "mode": "combat",
  "isSandboxMode": false,
  "skipPrologue": true,
  "goalLevel": "Full",
  "combatSettings": {
    "aggressiveness": 1,
    "initialLevel": 0,
    "initialGrowth": 1,
    "initialColonize": 1,
    "maxDensity": 1,
    "growthSpeedFactor": 1,
    "powerThreatFactor": 1,
    "battleThreatFactor": 1,
    "battleExpFactor": 1
  }
}
```

和平和战斗是互斥模式，可以使用任意一种写法：

```json
{ "mode": "peace" }
```

```json
{ "isPeaceMode": true }
```

```json
{ "isCombatMode": true }
```

沙盒可与和平/战斗模式组合：

```json
{
  "mode": "combat",
  "isSandboxMode": true
}
```

成功响应：

```json
{
  "started": true,
  "status": "loading",
  "desc": {
    "galaxyAlgo": 20200403,
    "galaxySeed": 12345678,
    "galaxySeedText": "12345678",
    "starCount": 64,
    "playerProto": 1,
    "resourceMultiplier": 1,
    "isPeaceMode": false,
    "isCombatMode": true,
    "isSandboxMode": false,
    "goalLevel": "Full"
  },
  "skipPrologue": true
}
```

## POST /game/state

按需查询游戏状态。请求会进入主线程查询队列，由游戏主线程在查询 tick 读取 DSP 对象并生成最终 JSON。

请求体：

```json
{
  "query": "query Observe { game { gameName gameTick } }",
  "operationName": "Observe"
}
```

`operationName` 可省略。`query` 使用 GraphQL query operation。

成功响应：

```json
{
  "data": {
    "game": {
      "gameName": "Save Name",
      "gameTick": 123456
    }
  }
}
```

对局就绪前：

```json
{
  "error": {
    "code": "game_not_ready",
    "message": "Game state is not ready for query.",
    "status": "menu"
  }
}
```

查询语法见 [查询语法文档](query-syntax.md)。

### 原型与科技查询

`POST /game/state` 可查询 `techs`、`recipes` 和 `items` 原型摘要，用于让 Agent 根据游戏数据选择科技、配方和建造物，而不是硬编码 ID。

`items` 支持热值、燃料参数、`raw` 与 `prefabDesc`，`recipes` 支持基础周期与 `raw`；`cargo` 返回原生增产倍率表。`factory.objectConnections(entityId: ...)` 返回实际模型端口与原生连接记录。字段单位、完整分页、示例和限制见 [原型属性与输送连接](../skills/automatic-dsp/references/interface/prototypes-and-connections.md)。

每次成功状态响应固定包含 `{"data":{...},"notifications":{"notices":[...],"goals":[...]}}`。未确认的教程、科研完成和顾问信息持续返回，由 LLM 使用 `dismissNotice` 确认并关闭；查询本身不确认，原生自行收起也不确认。当前目标随游戏原生状态更新，不通过提示确认来完成或忽略目标。详情及 `localPlanet.surface(x,y,z)` 海岸地形采样见 [提示与地形查询](../skills/automatic-dsp/references/interface/game-state.md#提示与地形查询)。

```graphql
query FindBasicBuildTechs {
  research {
    currentTechId
    currentTechName
    queueLength
    queue { techId name }
  }
  techs(where: { unlockRecipes_contains: 84 }, limit: 8) {
    id
    name
    unlocked
    canEnqueue
    hashUploaded
    hashNeeded
    preTechs
    preItems
    items { itemId itemName points }
    metadataBuyoutCost { itemId itemName required available }
    unlockRecipes
    addItems { itemId itemName count }
  }
  items(where: { id_in: [2001, 2011, 2203, 2301, 2302] }, limit: 8) {
    id
    name
    unlocked
    canBuild
    handcraftRecipeId
  }
}
```

`techs.unlockRecipes` 是科技解锁的配方 ID 列表。例如传送带配方是 `84`、分拣器是 `85`、采矿机是 `48`、电弧熔炉是 `56`、风力涡轮机是 `7`。Agent 可以先查询这些配方对应的科技，再用 `researchTech` 推进。

### 太空舱与特殊地面物感知

查询接口尽量暴露与游戏对象对齐的对象。出生点太空舱不是矿脉，而是 `PlanetFactory.vegePool` 中的特殊 `VegeData`，其 `protoId` 为 `9999`。AI Agent 不应等待一个独立的 `spaceCapsule` 状态对象，而应在常规状态查询中附带一次地面特殊物检查；无论新游戏是否跳过序幕，都应先查询并优先回收飞行仓。

```graphql
query ObserveStartObjects {
  metadata { gameTick localPlanetId }
  player {
    position { x y z }
  }
  factory {
    vegePool(where: { id_gt: 0 protoId: 9999 }, limit: 1) {
      id
      protoId
      pos { x y z }
      scl { x y z }
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
      astroId
      localPosition { x y z }
    }
  }
}
```

`warningSystem.activeBroadcasts` 可能出现 `SpaceCapsuleLanding` 等屏幕/语音提示，但太空舱回收的可靠数据源仍是 `factory.vegePool`。如果查询到 `protoId = 9999` 的对象，应把返回的 `id` 作为 `mineTarget.targetId`，并使用 `targetType = "vege"` 优先采集；回收后再执行补充机甲燃料、采矿、手搓和研究等开局目标。如果没有查到，说明当前对局可能已经回收，或该对象不在当前行星工厂数据中。

`veinPool`、`vegePool` 等带有本地坐标的对象可请求派生字段 `distanceToPlayer`。手动采集或开局回收应优先选择距离最近的目标；自动化产线选址再综合矿脉规模、地形和附近资源。

### 工厂实体摘要查询

`factory` / `localFactory` 仍然返回当前行星的游戏原始 `PlanetFactory` 对象，适合查询 `entityPool`、`veinPool`、`vegePool` 等与游戏结构对齐的数据。Agent 需要快速判断已建实体、配方、缺电、缺料和输出堵塞时，可以查询 `factoryDetails` / `localFactoryDetails`：

```graphql
query ObserveFactoryEntities {
  factoryDetails {
    planetId
    planetName
    entityCount
    buildingSummary { itemId itemName count }
    statusSummary { status count }
    items {
      entityId
      protoId
      name
      position { x y z }
      powerNodeId
      assemblerId
      minerId
      inserterId
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
}
```

这个摘要根不替代原始 `factory` 查询；它只是把常用实体组件状态整理成 Agent 易消费的结构。建造、配方设置或拆除后，应重新查询 `factoryDetails.items` 来确认实体是否建成、配方是否写入，以及是否出现 `materialShortage`、`outputBlocked` 或 `noRecipe` 等状态。供电状态应由外部 Agent 查询 `factory.powerSystem.consumerPool`、`nodePool` 和 `networkId` 等游戏原始字段判断。

### 背包与燃烧室聚合查询

`inventory` / `package` 和 `mecha.reactorStorage` 默认对齐游戏原始存储对象。需要槽位细节时查询 `grids`；需要快速判断物品总量时，可以查询派生字段 `items` 或 `summary`：

```graphql
query ObserveStorage {
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
  mecha {
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
}
```

`items` 按 `itemId` 聚合所有非空槽位，返回物品名、总数量、增产剂总量和占用槽位数；`summary` 返回总物品数和不同物品种类数。开局回收飞行仓后，Agent 应重新查询这些字段，再决定是否补燃料、继续采集或手搓。

## GET /game/saves

获取存档列表和基本信息。该接口会读取游戏存档目录下的 `.dsv` 文件，并尽量解析 header、`GameDesc` 和元数据属性。

```json
{
  "saveFolder": "C:/Users/name/Documents/Dyson Sphere Program/Save/",
  "count": 1,
  "items": [
    {
      "saveName": "auto-test",
      "fileName": "auto-test.dsv",
      "path": "C:/Users/name/Documents/Dyson Sphere Program/Save/auto-test.dsv",
      "isUserSave": true,
      "fileSize": 123456,
      "lastWriteTimeUtc": "2026-06-13T12:00:00Z",
      "header": {
        "headerVersion": 7,
        "lastSaveVersion": "0.10.34.0",
        "gameTick": 123456,
        "saveTime": "2026-06-13T12:00:00Z"
      },
      "desc": {
        "galaxySeed": 12345678,
        "galaxySeedText": "12345678",
        "starCount": 64,
        "resourceMultiplier": 1,
        "isPeaceMode": false,
        "isCombatMode": true,
        "isSandboxMode": false
      }
    }
  ]
}
```

## POST /game/save

保存当前游戏。调用状态为已加载对局。

请求体：

```json
{
  "saveName": "auto-test"
}
```

成功响应：

```json
{
  "saved": true,
  "saveName": "auto-test",
  "path": "C:/Users/name/Documents/Dyson Sphere Program/Save/auto-test.dsv",
  "status": "running"
}
```

## POST /game/load

加载存档。调用状态为 `menu`。

请求体：

```json
{
  "saveName": "auto-test"
}
```

成功响应：

```json
{
  "started": true,
  "saveName": "auto-test",
  "status": "loading"
}
```

菜单以外状态返回 `409 invalid_game_status`。

## POST /game/prologue/skip

跳过当前序幕。`status: "prologue"` 时执行跳过；其他状态返回 `skipped: false`。

```json
{
  "skipped": true,
  "status": "running"
}
```

## POST /tasks

默认按科研、手搓、机甲指令、建造四类通道分流。移动等机甲指令互斥；预建下达后后台等待落成，允许继续移动。垃圾操作即时执行。跨通道完成依赖用 `dependsOn` 表达，实体引用自动等待来源落成。任务接口不使用 GraphQL 写操作。

任务状态随游戏主循环推进。自动分流无需 `immediate:true`；该字段保留即时白名单校验，也支持垃圾操作。完整通道、依赖、取消与超时语义见 [游戏控制契约](../skills/automatic-dsp/references/interface/game-control.md)。

请求体：

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
      "id": "set-iron-recipe",
      "type": "setRecipe",
      "target": { "commandId": "place-smelter" },
      "recipeId": 1
    }
  ]
}
```

成功响应：

```json
{
  "task": {
    "id": "task:42",
    "clientRequestId": "build-iron-line-001",
    "status": "QUEUED",
    "queueIndex": 0,
    "currentCommandIndex": 0,
    "stopOnFailure": true,
    "commands": [
      {
        "id": "move-to-ore",
        "type": "moveTo",
        "status": "PENDING",
        "phase": null,
        "startedAt": null,
        "completedAt": null,
        "errorCode": null,
        "errorMessage": null,
        "result": null
      }
    ]
  }
}
```

任务状态：

- `QUEUED`
- `RUNNING`
- `SUCCEEDED`
- `FAILED`
- `CANCEL_REQUESTED`
- `CANCELLED`

命令状态：

- `PENDING`
- `RUNNING`
- `SUCCEEDED`
- `FAILED`
- `SKIPPED`
- `CANCELLED`

第一版命令类型：

- `moveTo`
- `mineTarget`
- `autoReplenishMechaFuel`
- `entityFastFillIn`
- `entityFastTakeOut`
- `dismantleEntity`
- `reverseBelt`
- `setStorageLimit`
- `setSorterFilter`
- `setSplitterPriority`
- `craftInventory`
- `removeForgeTask`：当前制造队列 `index` 和 `recipeId`，调用原生取消及材料退款。
- `cancelPrebuild`：当前行星正整数 `prebuildId`，在建造范围内原生拆除未建成的预建；走普通队列。
- `dismissNotice`：按 `noticeId` 确认信息提示，并关闭仍可见的对应窗口。
- `researchTech`
- `buyoutTech`
- `removeTechInQueue`
- `placeBuilding`
- `placeBelt`
- `placeSorter`
- `setRecipe`
- `setLabResearchMode`
- `waitUntil`

建造命令默认等待游戏内建造完成后才算成功。采集、填充、拆除和建造类命令没有移动策略参数：目标在内部允许的命令下发半径内时，命令可以自动靠近并再次调用游戏原生校验；目标超过下发半径时返回 `out_of_range`，外部 Agent 必须先用 `moveTo` 靠近，再重新下发交互或建造命令。

自动靠近不扩大游戏规则允许的交互、碰撞、地形、物品数量或科技限制；最终仍以游戏原生采集订单、`BuildTool.CheckBuildConditions()`、`PlanetFactory.EntityFastFillIn()` 和拆除逻辑为准。

太空舱不提供专用任务命令。它是当前行星 `factory.vegePool` 中 `protoId = 9999` 的 `VegeData`，Agent 应先通过状态查询获取它的 `id`，再用普通 `mineTarget` 发出游戏原生采集订单。开局阶段应始终优先回收飞行仓，因为它会提供燃料和基础材料；在飞行仓存在时，不应先执行采矿、手搓、研究或建造目标。

```json
{
  "id": "recycle-capsule",
  "type": "mineTarget",
  "targetType": "vege",
  "targetId": 123,
  "untilDepleted": true,
  "timeoutSeconds": 120
}
```

如果任务执行时该 `VegeData` 已不存在，`mineTarget` 会返回 `target_not_found`。这通常表示太空舱已经回收，或当前玩家不在包含该对象的行星工厂中。成功结果包含 `depleted` 和本次采集进入背包的 `items`。

普通矿脉或地面资源采集可以传入期望获得的物品数量。`itemCount` 是明确字段，`count` 是兼容别名；两者都没有传入时默认采集 1 个目标物品。采集命令仍然使用游戏原生采集订单，达到目标数量或目标枯竭后结束：

```json
{
  "id": "mine-iron-ore",
  "type": "mineTarget",
  "targetType": "vein",
  "targetId": 4,
  "itemId": 1001,
  "count": 50,
  "timeoutSeconds": 240
}
```

`autoReplenishMechaFuel` 用于把背包中的可用燃料补充到机甲燃烧室。它调用游戏原生 `Mecha.AutoReplenishFuelAll()`，因此哪些物品可以放入燃烧室由游戏内部逻辑决定，外部 Agent 不需要传入燃料配方或燃料类型。

```json
{
  "id": "fill-mecha-reactor",
  "type": "autoReplenishMechaFuel",
  "timeoutSeconds": 10
}
```

命令成功结果包含 `method`、`movedCount`、`movedItems`、`reactorItems`、`reactorEnergyBefore` 和 `reactorEnergyAfter`。如果没有任何燃料移动，且机甲燃烧室仍为空，命令返回 `FAILED`，`errorCode` 为 `no_fuel_moved`。

机甲燃烧室与建筑发电机燃料槽不是同一套数据结构：机甲使用 `Mecha.reactorStorage` / `reactorEnergy`，建筑发电机使用 `PowerGeneratorComponent.fuelId` / `fuelCount` / `fuelMask`。本命令只处理机甲燃烧室，不用于给火力发电机、热电站等建筑填燃料。

`entityFastFillIn` 对齐游戏原生 `PlanetFactory.EntityFastFillIn(entityId, fromPackage, out itemBundle)`。它用于对当前行星实体执行游戏内“快速填充”，包括但不限于建筑发电机燃料槽、制造设施输入、弹药等；具体能填入什么由游戏原生方法和实体当前状态决定。

```json
{
  "id": "fast-fill-generator",
  "type": "entityFastFillIn",
  "target": { "commandId": "place-generator" },
  "fromPackage": true,
  "timeoutSeconds": 30
}
```

目标实体支持 `entityId`、`target.entityId` 或 `target.commandId`。`target.commandId` 可以引用同一任务中之前成功命令返回的 `entityId`；如果引用 `placeBelt` 等返回 `entityIds` 的命令，可同时传 `target.entityIndex` 选择某个实体。默认 `fromPackage = true`，即从背包快速填充；目标超过命令下发半径时返回 `out_of_range`。成功结果包含 `method`、`entityId`、`movedItems` 和实体摘要；如果没有任何物品被原生方法转移，返回 `FAILED`，`errorCode` 为 `no_item_transferred`。

`entityFastTakeOut` 对齐游戏原生 `PlanetFactory.EntityFastTakeOut(entityId, toPackage, out itemBundle, out full)`。它用于对当前行星实体执行游戏内“快速取出”，包括储物仓、传送带、采矿机、分拣器、制造设施和研究站等实体中原生允许取出的物品；具体能取出什么由游戏原生方法和实体当前状态决定。

```json
{
  "id": "take-from-iron-storage",
  "type": "entityFastTakeOut",
  "entityId": 19,
  "toPackage": true,
  "timeoutSeconds": 30
}
```

目标实体支持 `entityId`、`target.entityId` 或 `target.commandId`，引用规则与 `entityFastFillIn` 相同。默认 `toPackage = true`，即取到背包；目标超过命令下发半径时返回 `out_of_range`。成功结果包含 `method`、`entityId`、`full`、`movedItems` 和实体摘要；如果没有任何物品被原生方法转移，返回 `FAILED`，`errorCode` 为 `no_item_transferred`。

`dismantleEntity` 用于拆除当前行星实体，并通过游戏原生 `PlayerAction_Build.DoDismantleObject(entityId)` 回收建筑物品和实体内部物品。它支持 `entityId`、`target.entityId` 或 `target.commandId`；如果引用 `placeBelt` 等返回 `entityIds` 的命令，可同时传 `target.entityIndex` 选择某个实体。命令只处理已经建成的正实体 ID，不处理预建 ID：

```json
{
  "id": "remove-bad-smelter",
  "type": "dismantleEntity",
  "entityId": 17,
  "timeoutSeconds": 60
}
```

```json
{
  "id": "remove-first-belt",
  "type": "dismantleEntity",
  "target": { "commandId": "belt-from-miner", "entityIndex": 0 },
  "timeoutSeconds": 60
}
```

成功结果包含 `entityId`、`protoId`、`protoName`、`position`、`returnedItems` 和 `returnedCount`。如果实体不存在返回 `target_not_found`；目标超出命令下发半径时返回 `out_of_range`。

`craftInventory` 推荐由外部传入目标物品，而不是配方：

```json
{
  "id": "craft-iron-ingot",
  "type": "craftInventory",
  "itemId": 1101,
  "count": 10,
  "timeoutSeconds": 120
}
```

`itemId` 表示期望获得的物品，`count` 表示期望产物数量。Mod 会使用游戏内部 `ItemProto.handcraft` 解析手搓配方，并通过 `MechaForge.TryAddTask/AddTask` 走游戏原生递归材料判定和入队逻辑。需要兼容旧调用或强制指定配方时可以传 `recipeId`；如果只传 `recipeId`，`count` 表示配方执行次数。

材料不足时命令返回 `FAILED`，`errorCode` 为 `missing_item`，`result` 会包含 `ingredients`、`products` 和递归汇总后的 `missing` 列表。配方未解锁时返回 `recipe_locked`。`waitForCompletion` 默认 false；true 时后台等待本次原生制造任务完成，false 时原生入队后即返回 `SUCCEEDED`，结果为 `action: enqueued, completed: false`。LLM 应优先入队并并行处理无依赖工作，仅在需要产物时等待。原生任务未完成就被移除时返回 `craft_interrupted`。

`researchTech` 用于按当前存档的正常流程推进科技。它只对齐游戏原生 `GameHistoryData.EnqueueTech()`：可入队时加入科技队列，并按 `waitForUnlock` 决定是否等待游戏内机甲实验室或研究站上传 hash 后解锁。它不会调用 `BuyoutTech()`，也不会使用跨存档结转的 `PropertySystem` 元数据。

```json
{
  "id": "research-basic-logistics",
  "type": "researchTech",
  "techId": 1001,
  "waitForUnlock": true,
  "timeoutSeconds": 120
}
```

成功结果包含 `techId`、`techName`、`usesMetadata`、`unlocked`、`inQueue`、`currentTechId`、`queueLength`、`hashUploaded`、`hashNeeded`、正常研究物品摘要和 `metadataBuyoutCost`。`usesMetadata` 对 `researchTech` 固定为 `false`。

`buyoutTech` 用于显式使用游戏原生 `GameHistoryData.BuyoutTech()`，消耗的是跨存档结转的 `PropertySystem` 元数据，而不是当前背包物品。它只应该用于测试加速或玩家明确希望跳过当前局研究进度的场景；正常 Agent 游玩流程应使用 `researchTech`。

```json
{
  "id": "buyout-basic-logistics",
  "type": "buyoutTech",
  "techId": 1601,
  "timeoutSeconds": 10
}
```

`buyoutTech` 成功结果中 `usesMetadata = true`，`metadataBuyoutCost` 会返回本次科技按当前 hash 进度估算的元数据物品需求和 `PropertySystem.GetItemAvaliableProperty()` 可用量。买断失败返回 `tech_buyout_failed`。

`removeTechInQueue` 用于对齐游戏原生 `GameHistoryData.RemoveTechInQueue(index)`，移除研究队列中的指定索引，并由游戏原生 `VerifyTechQueue()` 重新整理队列。它不负责决定科技路线；外部 Agent 应先查询 `research` / `techs`，移除不合适的队列项后，再用 `researchTech` 按游戏原生入队规则补满队列。

```json
{
  "id": "remove-late-tech",
  "type": "removeTechInQueue",
  "index": 7
}
```

成功结果包含 `action`、`currentTechId`、`queueLength` 和整理后的 `techQueue`。

`setLabResearchMode` 用于把矩阵研究站切换到游戏原生研究模式。它支持 `entityId`、`target.entityId` 或 `target.commandId`；默认使用当前研究队列的 `GameHistoryData.currentTech`，也可以显式传入 `techId`。目标必须是矩阵研究站实体，命令会调用 `LabComponent.SetFunction(_researchMode: true, 0, techId, entitySignPool)`，并同步相邻研究站函数：

```json
{
  "id": "set-lab-research",
  "type": "setLabResearchMode",
  "entityId": 23,
  "techId": 1101
}
```

生产矩阵仍然使用 `setRecipe` 设置研究站的矩阵配方；`setLabResearchMode` 只用于研究模式。

`placeBelt` 支持两种路径输入。`points` 可包含两个或多个行星局部坐标点；实现会按相邻点调用游戏网格吸附并生成传送带预览。

```json
{
  "id": "belt-iron-ore",
  "type": "placeBelt",
  "itemId": 2001,
  "points": [
    { "x": 120.0, "y": 35.0, "z": -80.0 },
    { "x": 126.0, "y": 35.0, "z": -78.0 }
  ],
  "timeoutSeconds": 120
}
```

也可以使用 `startPosition` 和 `endPosition`：

```json
{
  "type": "placeBelt",
  "itemId": 2001,
  "startPosition": { "x": 120.0, "y": 35.0, "z": -80.0 },
  "endPosition": { "x": 126.0, "y": 35.0, "z": -78.0 }
}
```

也可以使用端点对象连接建筑端口。端点对象支持 `entityId`、`commandId`、`entityIndex`、`slot` 和可选 `position`；`commandId` 引用同一任务内之前成功命令的结果，`entityIndex` 用于选择 `entityIds` 中的某个实体。

```json
{
  "id": "belt-from-miner",
  "type": "placeBelt",
  "itemId": 2001,
  "start": { "commandId": "place-miner", "slot": 0 },
  "end": { "x": 126.0, "y": 35.0, "z": -78.0 },
  "timeoutSeconds": 120
}
```

`placeSorter` 使用输入/输出实体建立连接。`input` 与 `output` 可直接传实体 ID，也可传对象；对象支持 `entityId`、`commandId`、`entityIndex`、`slot` 和可选 `position`。连接到传送带时可传入 belt 实体 ID；如果引用 `placeBelt` 的结果，用 `entityIndex` 选择要连接的传送带段。可选 `filterItemId` 在原生预建中设置筛选，省略或 0 表示不筛选，正数必须是有效物品 ID；多产物建筑应在建造时设置筛选，避免落成后误送其他产物。

建筑端点按指定 `slot` 的原生姿态定位，传送带端点按带段姿态定位。可选 `position` 仅用于核对坐标（误差不超过 0.01），不能覆盖插槽位置；不匹配或插槽越界返回 `invalid_command`。建筑插槽已有连接时返回 `slot_occupied`，不会替换旧连接。外部 Agent 应通过 `factory.objectConnections` 读取插槽姿态与占用后显式选择插槽。

建筑端点省略 `slot` 时默认 0，不自动选空槽。成功结果包含 `itemId`、`entityId` 和单元素 `entityIds`；分拣器的完成匹配会核对两端连接对象及显式插槽，区分同一起点的不同实体。更新后的校验不会自动修复旧存档已有的断链，仍需按实际连接执行拆建。

```json
{
  "id": "sorter-smelter-in",
  "type": "placeSorter",
  "itemId": 2011,
  "input": { "commandId": "belt-from-miner", "entityIndex": 3 },
  "output": { "commandId": "place-smelter", "slot": 0 },
  "timeoutSeconds": 120
}
```

`placeBelt` 成功结果包含 `entityIds`；只有单个实体的命令会额外返回 `entityId`。`setRecipe` 可通过 `target.commandId` 引用同一任务内之前成功命令返回的 `entityId`。

`waitUntil` 支持以下第一版条件：

- `always` / `never`：测试用条件。
- `elapsedSeconds`：等待指定秒数，字段为 `seconds`。
- `elapsedTicks`：等待指定游戏 tick 数，字段为 `ticks`。
- `gameTickAtLeast`：等待游戏 tick 达到指定值，字段为 `gameTick`。
- `inventoryAtLeast`：背包中指定物品数量达到 `count`。
- `inventoryDeltaAtLeast`：从该命令开始等待后，背包中指定物品增加至少 `count`。
- `techUnlocked`：指定 `techId` 的科技已解锁。
- `recipeUnlocked`：指定 `recipeId` 的配方已解锁。
- `itemUnlocked`：指定 `itemId` 的物品已解锁。
- `factoryProductDeltaAtLeast` / `itemProduced` / `nearbyItemProduced`：从该命令开始等待后，当前行星工厂统计中指定物品产出增加至少 `count`。
- `factoryConsumeDeltaAtLeast` / `itemConsumed`：从该命令开始等待后，当前行星工厂统计中指定物品消耗增加至少 `count`。

```json
{
  "id": "wait-iron-output",
  "type": "waitUntil",
  "condition": { "type": "factoryProductDeltaAtLeast", "itemId": 1101, "count": 1 },
  "timeoutSeconds": 120
}
```

`nearbyItemProduced` 当前第一版按当前行星工厂产出统计判断，不做半径内空间归因；需要空间归因时应先通过 `/game/state` 查询局部实体状态。

完整行星内铁块生产线任务示例见 `docs/examples/planetary-iron-line.md`。

## POST /tasks/{id}/cancel

请求取消任务。

```http
POST /tasks/task:42/cancel
```

成功响应：

```json
{
  "task": {
    "id": "task:42",
    "status": "CANCEL_REQUESTED"
  }
}
```

取消语义：

- `QUEUED` 任务可以直接转为 `CANCELLED`。
- `RUNNING` 任务在当前命令完成或到达安全中断点后停止。
- 取消或超时时，移动、采集和自动靠近类建造命令会清理伊卡洛斯当前玩家订单。
- 已经创建的游戏预建不回滚，取消只停止后续命令。

## GET /tasks

查询当前进程内待执行或执行中的任务。已完成、失败或取消的任务不再通过该接口返回；命令历史通过 `GET /history` 查询。

```json
{
  "tasks": [
    {
      "id": "task:42",
      "clientRequestId": "build-iron-line-001",
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
          "errorMessage": null,
          "result": null
        }
      ]
    }
  ]
}
```

同通道下达保持顺序，原生制造、科研、施工与机甲行动可并行。任务等待所有命令终结，不能把下达成功当作落成。

## GET /tasks/{id}

查询当前进程内保留的单个任务完整快照，包括已结束任务的命令 `result`。这个接口用于在任务完成后继续读取 `entityId`、`entityIds`、错误信息和命令阶段；跨进程或长期历史仍使用 `GET /history`。

```http
GET /tasks/task:42
```

成功响应结构与 `POST /tasks` 的 `task` 字段一致。任务不存在时返回 `404 task_not_found`。

任务错误响应示例：

```json
{
  "error": {
    "code": "invalid_command",
    "message": "Command 2 is missing required field: itemId."
  }
}
```

常见任务错误码：

- `invalid_command`
- `game_not_ready`
- `missing_item`
- `tech_locked`
- `recipe_locked`
- `terrain_blocked`
- `collision`
- `out_of_range`
- `path_unreachable`
- `timeout`
- `command_cancelled`

## GET /history

查询 SQLite 中已经完成、失败、跳过或取消的历史命令。当前实现固定返回最近 100 条。

```json
{
  "available": true,
  "unavailableReason": null,
  "items": [
    {
      "id": 1,
      "taskId": "task-id",
      "commandId": "command-id",
      "commandType": "moveTo",
      "status": "SUCCEEDED",
      "startedAt": "2026-06-13T12:00:00",
      "completedAt": "2026-06-13T12:00:01",
      "errorCode": null,
      "errorMessage": null,
      "gameTick": 123456,
      "result": {
        "entityId": 12,
        "itemId": 2301
      }
    }
  ]
}
```

历史数据库路径：

```text
BepInEx/cache/AutomaticDSP/data/history.sqlite
```

## 调用示例

PowerShell 查询轻量状态：

```powershell
Invoke-RestMethod -Uri http://127.0.0.1:39270/game
```

PowerShell 创建默认新游戏：

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri http://127.0.0.1:39270/game `
  -ContentType 'application/json' `
  -Body '{}'
```

PowerShell 查询游戏状态：

```powershell
$body = @{
  query = 'query Observe { game { gameName gameTick status } inventory { grids(limit: 10) { itemId count } } }'
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Post `
  -Uri http://127.0.0.1:39270/game/state `
  -ContentType 'application/json' `
  -Body $body
```

### 原生垂直堆叠

`placeBuilding` 可传 `stackOnEntityId` 替代 `position`，指定当前行星上方槽位 15 空闲的已建实体。物品需与下层原生堆叠类型兼容；位置来自下层 `lapJoint`，不自动选择顶层。层数、碰撞、距离、物资和建造完成状态仍由原生流程验证。示例：`{"type":"placeBuilding","itemId":2901,"stackOnEntityId":104}`。建成后查询模式，按需调用 `setLabResearchMode` 或 `setRecipe`。

### 原地升级

`upgradeEntity` 接收实体目标与目标等级的 `itemId`，例如 `{"type":"upgradeEntity","entityId":175,"itemId":2012}`。仅允许同一原生升级系列中的更高等级，检查科技、机甲建造范围和升级物品后调用原生升级，保留连接；不能用于改变建筑朝向。成功结果包含 `entityId`、`previousItemId`、`itemId` 和 `nativeError`。升级后仍应检查实际连接和吞吐。

`reverseBelt` 反转选中带段所属的整条原生 `CargoPath`，不是仅旋转单个带段，也不是反转整个相连网络。例如 `{"type":"reverseBelt","entityId":273}`；也支持 `target.entityId` 或 `target.commandId` / `target.entityIndex`。命令进入机甲指令队列，所选已建传送带必须位于当前行星、机甲建造范围内。沿用原生按钮限制：路径包含 2–1023 个带段，否则返回 `invalid_belt_path`。

反转调用原生 `UIBeltWindow.OnReverseButtonClick`，由游戏处理带上货物、分拣器偏移和直接连接建筑的端口。无法重新插入路径的货物按原生规则返还背包，背包溢出时可能形成垃圾。分支处可能断开连接，应重新查询 `objectConnections` 和运行状态。返回 `entityId`、受影响的 `entityIds`、`previousPathId` 和 `pathId`；路径 ID 可能变化。反转不是幂等操作，再执行一次会再次反向；请求结果不确定时先查询原任务和实际连接，不要盲目重试；`clientRequestId` 仅用于关联，不提供去重。

### 垃圾与退出

状态响应顶层 `trash` 包含垃圾统计及前 64 个有效条目，截断时 `truncated: true`。完整列表使用查询根 `trash.entries` 分页；`trashSystem` 可读原生垃圾池。`landPlanetId` 区分落地星球，0 为漂浮；`nearPlanetId` 不代表归属。`withinPickupRange` 仅表达距离条件，不保证拾取可执行或背包可容纳。

即时操作命令：`discardInventoryItem {itemId,count}` 从背包原生抛出；`pickupTrash {trashId,itemId}` 启动原生吸取，返回 `phase: pickupStarted`，应继续检查入包和溢出；`clearTrash {scope:"all"}` 永久清理全局垃圾，不返还物品。详情见 Skill 游戏控制接口。

`POST /game/exit` 请求体可省略或传 `{}`，只退出，不自动保存。需要保留进度时，由调用方先调用 `/game/save` 并确认 `saved: true`；同名 `saveName` 会覆盖已有存档。退出返回接受状态后延迟调用原生 `DSPGame.ExitProgram()`，客户端需再确认进程退出。加载期间拒绝请求。存档、加载和退出优先使用 API。

## 基础物流设置

以下命令均支持 `entityId` 或 `target` 实体引用，要求当前行星已建实体位于机甲建造范围内，进入机甲指令队列。完成时返回实际配置；不移动或重建实体。

- `{"type":"setStorageLimit","entityId":169,"maxSlots":3}`：调用 `StorageComponent.SetBans(size - maxSlots)`；maxSlots 是自动化可用格数，不是物品数量。0 禁用所有自动化格位，size 恢复全容量；超范围值拒绝，不静默截断。已有库存不删除，手动放入仍遵循原生规则。
- `{"type":"setSorterFilter","entityId":277,"itemId":6001}`：与原生过滤器 UI 一致设置过滤物品并同步实体图标；itemId 为 0 时清除。正在搬运的物品不被删除或替换，需等待后续搬运确认实际效果。
- `{"type":"setSplitterPriority","entityId":278,"slot":0,"priority":true}`：端口 slot 为 0–3，必须已接已建传送带；按原生 `SetPriority` 设置该方向的优先端口。输入与输出各有一组优先级。false 取消该端口的优先级（若它是当前优先端口）；取消输出优先级时会同时清除原生输出过滤器。启用时保留已有输出过滤设置。本命令不提供分流器过滤物品修改。

查询 `factory.factoryStorage.storagePool { size bans }`、`factory.factorySystem.inserterPool { filter }` 和 `factory.cargoTraffic.splitterPool { inPriority outPriority input0 output0 outFilter }` 验收。分流器返回的 inputBeltId/outputBeltId 为 beltPool 组件 ID，不是实体 ID；设置端口号通过 `objectConnections` 确认。
