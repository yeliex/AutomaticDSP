# 游戏内状态查询接口

连接地址、配置及请求格式见 [Mod 使用说明](../mod-usage.md)。

## POST /game/state

查询当前对局中的游戏对象与状态，采用 GraphQL 风格的只读字段选择 DSL。

### 调用条件

`GET /game` 返回 `ready: true`；当前包括运行、暂停和序幕状态。请求在游戏主线程的查询 tick 执行；响应记录的游戏时间用于对齐观测。

### 请求体

| 字段 | 必需 | 含义 |
| --- | --- | --- |
| `query` | 是，string | 非空查询字符串 |
| `operationName` | 否，string | 选择要执行的命名查询 |

```http
POST /game/state HTTP/1.1
Host: <游戏主机:端口>
Content-Type: application/json; charset=utf-8

{"query":"query Observe { metadata { gameTick localPlanetId } }","operationName":"Observe"}
```

### 响应

HTTP 200 返回 `data`，结构对应请求的字段或别名。示例值仅示意：

```json
{"data":{"metadata":{"gameTick":123456,"localPlanetId":101}}}
```

字段读取失败可能为 `null`，与请求失败分开处理。

### 错误

响应格式为 `{"error":{"code":"...","message":"..."}}`，未就绪错误另含 `status`。

| HTTP | code | 含义 |
| --- | --- | --- |
| 400 | `bad_json` | 请求体不是有效 JSON |
| 400 | `bad_request` | query 缺失或为空 |
| 400 | `graphql_parse_error` | 查询语法或操作选择不合法 |
| 409 | `game_not_ready` | 当前对局状态无法查询 |
| 504 | `query_timeout` | 等待游戏查询 tick 超时 |

## 请求及字段发现

向 `POST /game/state` 发送 `{"query":"query Discover { _schema { roots { name kind description } } }"}`。使用 GraphQL 风格的只读字段选择 DSL；写操作通过任务端点执行。

先探索结构，再查询和筛选必要字段：

```graphql
query Discover {
  _schema { roots { name kind description } }
  factory { _fields { name kind type } }
  production { _fields { name kind type } }
  power { _fields { name kind type } }
}
```

支持 query、别名、fragment、inline fragment 和列表参数 `where`、`offset`、`limit`。列表默认最多 256 项，单次上限 2048；完整候选集通过分页获取。过滤先于分页，多个条件按 AND 组合；空结果时先确认筛选字段可读：字段读取失败也会导致不匹配。

支持比较后缀 `_ne`、`_gt`、`_gte`、`_lt`、`_lte`、`_contains`、`_startsWith`、`_endsWith`、`_in`；无后缀表示相等，嵌套字段用 `__` 分隔。排序由 Agent 在查询结果上完成。

字段读取失败可能返回 `null`，按未知值处理。请求由游戏主线程查询 tick 处理，记录 `metadata.gameTick` 和行星以关联各次观测。

## 常用查询根的实际形状

| 根 | 当前返回 |
| --- | --- |
| `metadata` | `gameTick`、`queriedAt`、`localPlanetId`、`localStarId`、`schemaVersion` |
| `game` | 游戏状态摘要 |
| `player` / `mainPlayer` | 原始 Player；位置用行星局部 `position`，宇宙坐标另有 `uPosition` |
| `mecha`、`inventory` / `package`、`forge` / `replicator` | 原始机甲、背包和制造对象 |
| `factory` / `localFactory` | 原始 PlanetFactory，如 `entityPool`、`veinPool`、`vegePool` |
| `factoryDetails` | 已建实体与组件的常用摘要 |
| `localPlanet`、`localStar`、`factories`、`data` | 对应原始对象或数组 |
| `production` | 原始 `GameMain.statistics.production` |
| `power` | 当前行星原始 `powerSystem` |
| `research`、`techs`、`recipes`、`items` | 显式构造的摘要，字段见下文 |
| `warningSystem` | 警告摘要 |

其他根会尝试 GameMain 静态成员、实例和 GameData 成员。戴森球沿 `data.dysonSpheres` 探索可读字段。

`inventory.items` 和带 `grids` 的存储对象的 `items` 是物品聚合视图；`grids` 保留槽位、数量和增产等细节。大数组先小范围探索再分页。

## 最小观察示例

```graphql
query Observe {
  metadata { gameTick localPlanetId }
  player { planetId position { x y z } }
  inventory { items { itemId name count } }
  mecha { reactorEnergy reactorStorage { items { itemId name count } } }
  research { currentTechId currentTechName hashUploaded hashNeeded queueLength isStalled }
  factoryDetails { entityCount buildingSummary { itemId itemName count } }
}
```

按需追加局部矿脉和实体，示例参数只是读取条件：

```graphql
query SampleObjects {
  factory {
    vegePool(where: { id_gt: 0 protoId: 9999 }, limit: 1) { id protoId pos { x y z } }
    veinPool(where: { id_gt: 0 amount_gt: 0 }, limit: 32) {
      id type productId amount pos { x y z }
    }
    entityPool(where: { id_gt: 0 }, limit: 32) {
      id protoId pos { x y z } rot { x y z w }
    }
  }
}
```

此例按池中顺序采样，距离与布局由 Agent 计算。开局飞行仓是 `vegePool` 中 `protoId = 9999` 的对象，采集目标使用查询所得 `id`，`protoId` 用于识别对象种类。`warningSystem.activeBroadcasts` 中的 `SpaceCapsuleLanding` 可作为落地线索，回收目标仍以当前行星的 `vegePool` 为准。

## 当前原型摘要

- `items`：`id`、`name`、`type`、`stackSize`、`isEntity`、`canBuild`、`unlocked`、`handcraftRecipeId`、`handcraftProductCount`、`isBelt`、`isInserter`、`isAssembler`、`isPowerGen`。
- `recipes`：`id`、`name`、`type`、`handcraft`、`explicit`、`unlocked`、`items`、`results`；材料和产物条目使用 `itemId`、`itemName`、`count`。
- `techs`：`id`、`name`、`isHidden`、`isLabTech`、`unlocked`、`inQueue`、`canEnqueue`、`preTechs`、`preItems`、`items`、`unlockRecipes`、`unlockFunctions`、`addItems`、`hashUploaded`、`hashNeeded`、`metadataBuyoutCost`。
- 科技 `items` 条目为 `itemId`、`itemName`、`points`，材料需求需结合剩余 hash 与原生规则换算。重复升级另外核实当前等级。
- `research` 的队列条目用 `techId`、`name`；还有当前科技、hash 进度和停滞状态。

```graphql
query FindPrototypes {
  items(where: { name_contains: "矩阵" }, limit: 32) {
    id name unlocked handcraftRecipeId
  }
  recipes(where: { name_contains: "矩阵" }, limit: 32) {
    id name type unlocked items { itemId itemName count } results { itemId itemName count }
  }
  techs(where: { unlocked: false }, limit: 32) {
    id name canEnqueue preTechs preItems unlockRecipes hashUploaded hashNeeded
    items { itemId itemName points }
  }
}
```

按当前游戏语言用名称发现原型，再以 ID 关联配方和科技，并核实解锁与库存。

### 产线计算数据接入

[产线计算器](../guides/production-calculation.md)读取调用者准备的 JSON，不连接游戏或提交任务。当前 `recipes` 摘要可提供单轮投入与产出；配方时长、设备速度/功率、配方可用增产模式及喷涂参数需要从已核实的原始字段或注明版本的资料补齐。

后续完整物品、配方和建筑接口接入时，将游戏 ID、原型和实例加成转换成计算器输入，核实 tick/秒、内部速度倍率、能量/功率的换算，并区分基础设备速度与增产加速。配方支持性、建筑适配和解锁仍在查询侧核实。

## 原型信息范围

当前 `items` 摘要尚无热值，`recipes` 摘要尚无时长。完整信息及属性筛选接口待支持。

## 产量和功率计量

在 `production.factoryStatPool` 中用行星 `factoryIndex` 定位工厂，再通过物品索引映射读取对应统计。

`waitUntil` 的工厂增量实现通过 `productIndices[itemId]` 定位 `productPool`，用 `total[6]` 与 `total[13]` 分别作生产、消耗基线。连续计量还需实测字段含义、重置条件、时间单位和覆盖范围，再用累计量差除以游戏时间差。

`productRegister` 和功率 register 是原始统计字段，先核实采样周期、能量单位及每 tick 到每秒的转换，再用于产速或功率计算。

验收时保持同一存档和明确的统计范围，暂停不计为生产时间；时间回退、重载、计数归零后重新建立基线。当前行星统计用于行星级验收；全存档需汇总相关工厂，指定产线需结合实体运行证据。
