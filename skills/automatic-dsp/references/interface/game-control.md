# 游戏控制接口

连接地址、配置及请求格式见 [Mod 使用说明](../mod-usage.md)。

## POST /tasks：提交任务

请求通过校验后按执行通道分流，操作在游戏主线程推进。执行前确认当前对局已就绪，目标来自当前存档的观察结果。

| 请求字段 | 类型 | 必需 / 默认 | 含义 |
| --- | --- | --- | --- |
| `commands` | object[] | 必需、非空 | 按通道保持下达顺序的操作 |
| `clientRequestId` | string | 可选 | 客户端关联标记 |
| `stopOnFailure` | boolean | 可选，true | 命令失败后停止后续操作 |
| `immediate` | boolean | 可选，false | 兼容的即时白名单校验；自动分流不依赖此开关，不在 HTTP 请求线程执行 |
| `commands[].type` | string | 必需 | 下文操作表中的命令类型 |
| `commands[].id` | string | 可选，`command:{索引}` | 同任务内唯一，索引从 0 开始；用于结果引用 |
| `commands[].timeoutSeconds` | number | 可选，无默认上限 | 从开始执行计时的墙钟秒数 |
| 其他命令字段 | 按操作定义 | 见下文 | 坐标、物品、目标实体等，直接放在命令对象中 |

请求示例：

```http
POST /tasks HTTP/1.1
Host: <游戏主机:端口>
Content-Type: application/json; charset=utf-8

{
  "clientRequestId": "研究补给-001",
  "stopOnFailure": true,
  "commands": [
    {
      "id": "补充机甲燃料",
      "type": "autoReplenishMechaFuel",
      "timeoutSeconds": 10
    }
  ]
}
```

HTTP 200 响应示例（节选）：

```json
{"task":{"id":"task:1","clientRequestId":"研究补给-001","status":"QUEUED","commands":[{"id":"补充机甲燃料","type":"autoReplenishMechaFuel","status":"PENDING","errorCode":null,"errorMessage":null,"result":null}]}}
```

响应包含 `task`，保存其 `id` 用于后续查询。调度分为科研、手搓、机甲指令、建造四类；垃圾、队列移除与提示确认是即时操作，默认不等待机甲队列，无需设置 `immediate:true`。同一数组中的不同通道可独立推进；共享背包、能源和科技仍按原生规则检查。

移动、采集、取放、拆除等机甲指令保持互斥及下达顺序。建造的校验、靠近和预建下达阶段也占用机甲通道；预建创建后进入后台 `waitingBuilt`，退出建造模式并释放机甲通道，下一条移动可随施工跟进。建造命令只有实际落成才 `SUCCEEDED`。超出下发范围仍返回 `out_of_range`，由 Agent 先移动，不自动规划路径。

命令可传 `dependsOn: ["前序命令ID"]`，仅支持同一任务内前序命令；提交时拒绝未知或前向依赖。`target/start/end/input/output.commandId` 等实体引用自动建立落成依赖。其他材料、科技依赖须显式声明或通过状态条件等待；制造／科研若只确认入队，依赖也只等入队，需要等产物／解锁时给来源命令设置 `waitForCompletion:true`／`waitForUnlock:true`。不同通道之间不要仅靠数组相邻表达完成依赖。

`immediate:true` 仍可作为白名单校验，只允许不等待完成的制造／科研、队列移除、提示确认和垃圾操作。`queueIndex=-1` 表示当前不占机甲指令队列，不代表任务已经完成。命令状态含 `queue`（research/craft/construction/instruction/immediate）、`background`、`dependsOn`；任务在所有命令终结后才终结。`currentCommandIndex` 是首个未终结命令，不代表只有它正在执行。

取消任务或 `stopOnFailure:true` 触发停止时，停止该任务尚未完成的命令及其持有的机甲订单，不干扰其他任务的移动；已经提交的原生制造、科研、预建或吸取不撤销，需要另用对应取消命令。后台超时也不清除其他任务的移动订单。

```json
{"immediate":true,"commands":[{"type":"craftInventory","itemId":1202,"count":2,"waitForCompletion":false},{"type":"researchTech","techId":1001,"waitForUnlock":false}]}
```

上例允许先把研究加入游戏队列；材料实际制造并消耗后才解锁。所需科技前置仍由游戏检查。

- `stopOnFailure` 默认 `true`。失败任务不回滚此前完成的命令。
- 任务状态：`QUEUED`、`RUNNING`、`SUCCEEDED`、`FAILED`、`CANCEL_REQUESTED`、`CANCELLED`。
- 命令状态：`PENDING`、`RUNNING`、`SUCCEEDED`、`FAILED`、`SKIPPED`、`CANCELLED`。
- HTTP 200 表示请求成功；操作结果读取命令的状态、`phase`、`errorCode`、`errorMessage` 和 `result`。
- `timeoutSeconds` 可选；当前以命令开始后的墙钟时间判断，暂停也可能耗尽超时预算。省略时无默认超时上限。
- `clientRequestId` 用于关联请求，服务端不按此字段去重。网络超时后先核对任务和现场，再决定是否重发。
- 队列和任务 ID 属于当前进程；重启后结合已结束命令历史与当前世界状态恢复。

## GET /tasks：查询活动任务

无请求体。HTTP 200 返回 `{"tasks":[任务快照]}`，仅包含当前进程中排队或执行中的任务；已结束任务通过 ID 单独查询。

请求及空队列响应示例：

```http
GET /tasks HTTP/1.1
Host: <游戏主机:端口>
```

```json
{"tasks":[]}
```

## GET /tasks/{id}：查询单个任务

路径参数 `id` 为必填 string，来自提交响应的 `task.id`。HTTP 200 返回 `{"task":任务快照}`，当前进程内可查询已结束任务。

任务快照字段：`id`、`clientRequestId`、`status`、`queueIndex`、`currentCommandIndex`、`stopOnFailure`、`createdAt`、`startedAt`、`completedAt`、`commands`。命令快照包含 `id`、`type`、`status`、`phase`、起止时间、`errorCode`、`errorMessage` 和 `result`。

调用示例（路径中的 `%3A` 是冒号的 URL 编码）：

```http
GET /tasks/task%3A1 HTTP/1.1
Host: <游戏主机:端口>
```

响应示例（节选）：

```json
{"task":{"id":"task:1","status":"FAILED","commands":[{"id":"补充机甲燃料","type":"autoReplenishMechaFuel","status":"FAILED","errorCode":"no_fuel_moved","errorMessage":"No fuel was moved.","result":null}]}}
```

HTTP 成功与任务成功分别判断；错误消息以实际返回为准。

## POST /tasks/{id}/cancel：取消任务

路径参数 `id` 为必填 string，使用提交响应中的任务 ID，无需请求体。HTTP 200 返回 `{"task":任务快照}`；根据返回的任务状态确认取消进度。副作用与清理范围见 [取消与错误](#取消与错误)。

请求及执行中任务的响应示例（节选）：

```http
POST /tasks/task%3A1/cancel HTTP/1.1
Host: <游戏主机:端口>
Content-Length: 0
```

```json
{"task":{"id":"task:1","status":"CANCEL_REQUESTED"}}
```

排队任务可直接返回 `CANCELLED`；已结束任务返回原有状态。

## GET /history：查询命令历史

无请求体。HTTP 200 返回 `available`、`unavailableReason` 和 `items`，最多包含最近 100 条已结束命令。条目包括 `id`、`taskId`、`commandId`、`commandType`、`status`、`startedAt`、`completedAt`、`errorCode`、`errorMessage`、`gameTick` 和 `result`。

历史保存在 `BepInEx/cache/AutomaticDSP/data/history.sqlite`；活动任务保存在内存中。`available` 表示历史服务可用性，`unavailableReason` 提供不可用原因。

请求及空历史响应示例：

```http
GET /history HTTP/1.1
Host: <游戏主机:端口>
```

```json
{"available":true,"unavailableReason":null,"items":[]}
```

### 提交并查询的 PowerShell 示例

以下仅提交 `noop`，用于确认任务链路。轮询到终态后读取命令结果；终态为失败或取消时停止。

```powershell
$baseUrl = Read-Host "输入 Mod 使用说明中确认的服务地址"
$body = @{ commands = @(@{ id = "check"; type = "noop" }) } | ConvertTo-Json -Depth 5
$submitted = Invoke-RestMethod -Method Post -Uri "$baseUrl/tasks" -ContentType "application/json" -Body $body
$taskId = [Uri]::EscapeDataString($submitted.task.id)
do {
    $snapshot = Invoke-RestMethod -Uri "$baseUrl/tasks/$taskId"
    if ($snapshot.task.status -in @("SUCCEEDED", "FAILED", "CANCELLED")) { break }
    Start-Sleep -Seconds 1
} while ($true)
$snapshot.task
```

## 命令结果引用

有实体目标的命令通常支持 `entityId` 或 `target: {"commandId":"之前成功的命令"}`。引用只作用于同一任务中已经成功的命令。返回多个实体时加 `entityIndex`，索引从 0 开始。

`placeBuilding` 返回单个 `entityId`；`placeBelt` 返回带段数组 `entityIds`。传送带端点和分拣器使用自己的 `start` / `end`、`input` / `output` 对象，见 [建筑、传送带与分拣器](#建筑传送带与分拣器)。

## 等待

`waitUntil` 接受 `condition` 对象：

| `condition.type` | 其他字段与含义 |
| --- | --- |
| `elapsedSeconds` | `seconds`；墙钟时间 |
| `elapsedTicks` | `ticks`；游戏推进的 tick |
| `gameTickAtLeast` | `gameTick`；绝对游戏 tick |
| `inventoryAtLeast` | `itemId`、`count`；背包数量阈值 |
| `inventoryDeltaAtLeast` | 同上；从首次条件采样的基线计算净增加量 |
| `techUnlocked` | `techId` |
| `recipeUnlocked` | `recipeId` |
| `itemUnlocked` | `itemId` |
| `factoryProductDeltaAtLeast` | `itemId`、`count`；当前行星累计生产统计的增量 |
| `factoryConsumeDeltaAtLeast` | 同上；当前行星累计消耗统计的增量 |

生产条件别名为 `itemProduced`、`nearbyItemProduced`；消耗条件别名为 `itemConsumed`。`nearbyItemProduced` 同样统计整个当前行星。`always`、`never` 为调试条件。

每次等待使用一个条件，复杂验收由 Agent 观测判断。研究期间需要补给或建设时见 [研究](#研究)。

## 取消与错误

排队任务可直接取消；执行中的任务在主循环处理取消请求时停止后续执行。移动、采集、快速填充、快速取出、指定仓储转移、拆除和建造命令在取消或超时时清理玩家订单，停止自动靠近。已完成的物品转移、已创建的预建、已有制造和研究进度保留。

| 现象或错误 | 下一步依据 |
| --- | --- |
| HTTP 400 / `bad_json`、`bad_request`、`invalid_command` | 核对请求 JSON、非空命令数组、命令 type 和唯一 id |
| HTTP 404 / `task_not_found` | 核对 ID 是否属于当前进程 |
| HTTP 409 / `invalid_task_status` | 刷新任务状态 |
| HTTP 504 / 网络超时 | 先查任务、历史和现场，判断是否已经产生副作用 |
| `out_of_range` | 查看目标、行星与距离；当前只支持当前行星命令 |
| `missing_item` | 查询背包、可用仓储及缺料结果；选择取料、制造或采集 |
| `tech_locked` / `recipe_locked` | 查询原型的解锁状态和科技前置 |
| `collision` / `terrain_blocked` / 原生建造失败 | 读取原生校验、附近实体及地形，调整位置或连接 |
| `target_not_found` | 刷新当前行星、实体和资源对象 |
| `no_item_transferred` | 核对物品、容量、实体类型及原生取放范围 |
| `timeout` | 核对暂停、无人机材料及行动状态，按原因处理 |

HTTP 层错误常用 `{"error":{"code":"...","message":"..."}}`；执行失败可能出现在成功 HTTP 响应的任务内部。

## 可用操作

当前命令覆盖行星内基础操作。跨行星航行、物流站配置、蓝图应用、戴森球编辑，以及射线接收/发射目标/增产模式、战斗和地形改造的专用控制接口待支持。规划涉及这些操作时，可先完成材料与产线准备，并记录待执行部分。

下列命令对象放入 `POST /tasks` 的 `commands` 数组。可加 `id` 和 `timeoutSeconds`。示例数字仅用于说明格式，执行前必须替换为当前数据。

## 移动与采集

| 命令 | 参数 | 执行与结果 |
| --- | --- | --- |
| `moveTo` | `position: {x,y,z}`；可选 `planetId: "current"` 或当前行星 ID，`tolerance` 默认 2 | 当前行星局部坐标；等待位置与目标距离在容差内 |
| `mineTarget` | `targetType: "vein"/"vege"`、`targetId`；可选 `itemId`、`itemCount`（兼容 `count`，默认 1）、`untilDepleted` | 原生采集；达到数量或目标耗尽结束，检查 `depleted` 和实际 `items` |
| `autoReplenishMechaFuel` | 无必需附加参数 | 调用机甲原生自动补燃料；结果含 `movedItems`、`reactorItems`、补充前后能量 |

`moveTo` 使用当前行星的 `position` 坐标系。采集、建造及实体取放在允许的下发半径内可以自动靠近，超出则返回 `out_of_range`，需先单独移动。最终操作经过原生校验。

机甲补燃料操作机甲燃烧室；建筑补料使用 `entityFastFillIn`。没有燃料转移且燃烧室仍为空时返回 `no_fuel_moved`。

```json
{
  "commands": [
    {"id": "回收飞行仓", "type": "mineTarget", "targetType": "vege", "targetId": 123, "untilDepleted": true, "timeoutSeconds": 120},
    {"id": "补燃料", "type": "autoReplenishMechaFuel", "timeoutSeconds": 10}
  ]
}
```

## 背包、仓储与制造

垃圾操作自动走即时通道，不等待移动或施工：

| 命令 | 参数 | 语义 |
| --- | --- | --- |
| `discardInventoryItem` | `itemId`、正整数 `count` | 从背包扣除指定物品，保留增产点并由原生抛出垃圾；不是永久删除。单次不能超过原生 500 堆限制。 |
| `pickupTrash` | 当前 `trashId`、匹配的 `itemId` | 校验原生拾取范围、玩家状态及拾取筛选后启动吸取；结果 `phase: pickupStarted` 不代表已经入包。 |
| `clearTrash` | 必填 `scope: "all"` | 对应原生清理全部垃圾，永久删除全局垃圾，包括其他星球；不回收进背包。 |

垃圾 ID 会复用，操作前刷新状态。拾取后核对库存与垃圾变化，背包满时由游戏重新抛出溢出物品。取消 Mod 任务不撤销已经启动的原生吸取。当前不提供局部清理命令，不能将全局清理用于只想处理本地垃圾的请求。

实体目标可使用 `entityId` 或 `target` 引用，详见 [命令结果引用](#命令结果引用)。

| 命令 | 参数 | 执行与结果 |
| --- | --- | --- |
| `entityFastFillIn` | 目标实体；`fromPackage` 默认 true | 原生快速填充；物品种类与数量由游戏决定，读取 `movedItems` |
| `entityFastTakeOut` | 目标实体；`toPackage` 默认 true | 原生快速取出；可能涉及多个物品，读取 `movedItems`、`full` |
| `transferStorageItem` | 目标储物实体、`itemId`、正整数 `count`；`direction: "fromStorage"/"toStorage"`，默认前者 | 指定物品转移；结果为 `requestedCount`、`movedCount`、`inventoryCount`、`storageCount` |
| `craftInventory` | 推荐 `itemId`、`count`；或 `recipeId`、`count` | 按物品调用时 count 为产物数量；仅按配方时 count 为配方执行次数 |
| `removeForgeTask` | 当前 `forge.tasks` 的 `index` 与 `recipeId` | 原生取消制造及关联父/子任务并返还材料；索引或配方变化返回 `queue_changed` |

快速取放由原生逻辑决定物品与数量；储物实体的指定物品取放使用 `transferStorageItem`。该命令移动数量大于零即可成功，实际数量读取 `movedCount`；没有转移时返回 `no_item_transferred`。

`craftInventory` 使用原生递归材料判定，缺料返回 `missing_item` 和 `missing` 列表，未解锁返回 `recipe_locked`。`waitForCompletion` 默认 false，成功仅表示原生入队，结果为 `action: enqueued, completed: false`。设为 true 时后台跟踪本次原生制造任务剩余次数，制造完成后成功；不以背包净增判断，避免产物被并行操作消耗导致误报。等待结果含 `remainingCount`、`forgeTotalTime`；原生任务在未完成时被移除返回 `craft_interrupted`。

`removeForgeTask` 按执行时的队列校验索引和配方；即使配方相同也应在移除前刷新数量、父子关系和进度。取消子任务可能连带取消父任务及后续缺料任务，退款遵守原生背包容量及掉落规则。移除后重读整个 `forge.tasks`，不能继续沿用旧索引。

## 研究

| 命令 | 参数 | 执行与结果 |
| --- | --- | --- |
| `researchTech` | `techId`；`waitForUnlock` 默认 false | 原生入队并可等待解锁；不能入队返回 `tech_not_available` |
| `removeTechInQueue` | `index`，当前队列索引；推荐同时传 `techId` | 原生移除并整理队列；指定 techId 时校验目标，变化返回 `queue_changed` |
| `buyoutTech` | `techId` | 使用跨存档元数据，需用户明确要求；与正常研究材料不同 |
| `setLabResearchMode` | 实体目标；可选 `techId`，默认当前研究科技 | 设置矩阵研究站研究模式，并同步相邻研究站函数 |
| `setRecipe` | 实体目标、`recipeId` | 对支持配方的制造组件或研究站设配方；研究站用于矩阵生产模式 |

`waitForUnlock: false` 成功表示已入队或已经解锁，读取 `unlocked`、`inQueue` 区分。研究期间仍需执行补给或建设时，使用此选项让任务队列继续，之后单独观察解锁进度。

`researchTech` 使用当前存档的正常研究流程，返回 `usesMetadata: false`；`buyoutTech` 返回 true，其 `metadataBuyoutCost` 表示跨存档元数据需求。

`setRecipe` 检查解锁与目标类型，改变配方会取回内部物品。研究站设置会同步关联层，操作后核对背包和受影响组件。

## 建筑、传送带与分拣器

| 命令 | 必要参数及常用选项 |
| --- | --- |
| `placeBuilding` | `itemId`、`position: {x,y,z}`；可选 `rotation`（球面朝向的角度）、`veinId`（矿机目标），以及 `recipeId`、`filterId` |
| `placeBelt` | `itemId` 和 `points` 点列（至少两个点）；或 `startPosition` / `endPosition`；也可用 `start` / `end` 端点对象 |
| `placeSorter` | `itemId`、`input` 来源端点、`output` 去向端点；可选 `filterItemId` 在建造时筛选物品，0 或省略为不筛选 |
| `dismantleEntity` | 实体目标；使用正的已建实体 ID |
| `reverseBelt` | 实体目标；反转所属整条原生运输路径，所选带段须在建造范围内 |
| `setStorageLimit` | 储物仓实体目标、`maxSlots`（0 到实际 size），限制自动化可用格数 |
| `setSorterFilter` | 分拣器实体目标、物品 `itemId`；0 清除过滤 |
| `setSplitterPriority` | 四向分流器实体目标、端口 `slot`（0–3）、布尔值 `priority` |
| `upgradeEntity` | 实体目标、目标等级物品 `itemId`；在机甲建造范围内原地升级 |
| `cancelPrebuild` | 正整数 `prebuildId`；可选当前 `planetId` | 在建造范围内调用原生预建拆除和退款；需先取消等待该预建的 Mod 任务，刷新 `factory.prebuildPool` 后提交 |

传送带和分拣器分别使用专用命令。建筑位置按原生网格或矿机规则调整，旋转参数为球面朝向角；建成后读取实际位置。

喷涂机等 `prefabDesc.addonType: Belt` 建筑仍用 `placeBuilding`，执行原生附属建筑吸附、传送带连接和碰撞校验。喷涂机建成后读取 `factory.cargoTraffic.spraycoaterPool` 的 `cargoBeltId`、`incBeltId`，分别确认物料带和增产剂带；两条带的高度和方向按原型 `addonAreaPoses` 规划。建成不代表已喷涂，还需检查电力、增产剂供应与货物 `inc`。

`placeBuilding` 可用 `stackOnEntityId` 替代 `position`，明确指定原生支持堆叠的下层已建实体；位置和朝向由原生搭接点确定。该实体上方槽位 15 必须空闲，不自动寻找顶层；检查物品兼容性后仍执行原生层数、碰撞、物资、范围校验及无人机建造。研究站完成后查询并按需设置整叠生产或研究模式。

`upgradeEntity` 只接受同一原生升级系列的更高等级物品，检查科技、目标物资和建造范围后调用 `PlayerAction_Build.DoUpgradeObject`，并核对实际物品 ID。原生流程消耗新设备并返还旧设备，不通过拆除重建实现；完成后重新查询配方、连接与物流运行状态。返回 `previousItemId`、`itemId`、`entityId` 和 `nativeError`。

`reverseBelt` 示例：`{"type":"reverseBelt","entityId":273}`。先查询该带段的 `segPathId` 及运输路径，确认影响范围；反转作用于原生路径中的全部带段（2–1023 个），不是单个实体或整张物流网络。游戏原生逻辑保留货物并调整连接，无法放回的货物返还背包或形成垃圾；分支连接可能断开。返回受影响 `entityIds` 及前后路径 ID，完成后检查端口、分拣器和实际输送。命令属于机甲指令队列。重复执行会再次反向，请求结果不确定时先查询原任务和实际连接，不要盲目重试；`clientRequestId` 不提供去重。

端点对象可用 `entityId` 或同任务前序 `commandId`，可加 `entityIndex`、`slot` 及 `position`。位置对象使用当前行星坐标，slot 从实际端口信息取得。原始姿态或端口读取方式见 [查询](game-state.md)。

`placeSorter` 的建筑端点必须使用未占用的原生插槽（占用返回 `slot_occupied`）；省略 `slot` 时默认 0，不自动选槽。可选 `position` 必须与该插槽换算后的行星局部坐标或传送带段位置一致（误差不超过 0.01），不能覆盖端点坐标；不匹配或插槽越界返回 `invalid_command`。通常只传实体和插槽即可。

```json
{
  "commands": [
    {"id": "矿机", "type": "placeBuilding", "itemId": 2301, "veinId": 4, "position": {"x": 120, "y": 35, "z": -80}, "rotation": 90, "timeoutSeconds": 120},
    {"id": "出矿带", "type": "placeBelt", "itemId": 2001, "start": {"commandId": "矿机", "slot": 0}, "end": {"x": 126, "y": 35, "z": -78}, "timeoutSeconds": 120}
  ]
}
```

Agent 提供路径，Mod 按相邻点进行原生吸附和预览。下发后检查实际 `entityIds` 及连接。

分拣器 `input` 是取物来源，`output` 是放物去向，例如来源传送带、去向熔炉。引用带段时用 `entityIndex` 选择连接段。

分拣器端点需通过原生预览配对的朝向／高度容差，无法匹配时返回 `invalid_connection`，结果含端点角度与径向高度差；之后仍执行原生距离和碰撞校验。架高带须先回到建筑插槽高度，不能靠指定实体 ID 越过端点配对规则。传送带高度由实际路径坐标表达，与 `stackOnEntityId` 无关。

`placeSorter` 成功结果包含 `itemId`、`entityId` 和单元素 `entityIds`。完成匹配同时核对两端连接及显式建筑插槽，避免把同起点的旧分拣器误认为新实体。后续操作使用返回 ID，并通过 [连接查询](prototypes-and-connections.md#实体端口和连接) 核对现场。

建造命令以预建转为实际实体后成功。拆除结果含回收物品与位置，操作后检查背包和相邻连接。取消语义见 [取消与错误](#取消与错误)。

建造成功、失败、超时或取消后，退出该命令进入的建造模式；尚未建成的预建不自动删除。取消 Mod 任务、移除原生制造/科研、取消预建是不同操作：`POST /tasks/{id}/cancel` 不撤销已入队的制造/科研或已创建的预建。等待中的科研被移除后返回 `research_interrupted`。预建已转为实体时不会按旧预建 ID 拆除实体，需重新观察。

## 信息提示

`dismissNotice` 需要 `notifications.notices` 或 `ui.notices` 中条目的 `noticeId`。确认该记录；仍可见且身份未变化时，在主线程调用对应原生关闭方法，已自行收起则仅确认记录。同一记录重复确认成功；记录不存在或已切换对局返回 `notice_not_found`。支持即时通道。读取状态不会确认提示，未确认消息每次查询都会返回，由 LLM 理解后及时关闭。不会自动确认错误、存档或其他决策对话框，也不会把游戏目标标记为完成或忽略。详情见 [提示与地形查询](game-state.md#提示与地形查询)。

## 等待与调试

`waitUntil` 的全部条件及作用范围见 [等待](#等待)。`noop` 用于调试。

基础物流设置使用 `setStorageLimit`、`setSorterFilter`、`setSplitterPriority`，都在机甲指令队列执行并要求选中实体在建造范围内。仓储 `maxSlots` 限制自动化格位数量，0 禁止自动化存入、size 解除限制，不会清除超限库存；不要把格数当作物品数。分拣器 `itemId:0` 清除过滤，修改不会丢弃正在搬运的货物。分流器 `slot` 为 objectConnections 的 0–3 端口，必须已连接已建传送带；`priority:true/false` 设置或取消该端口优先级，输入和输出独立。取消输出优先级也会按原生规则清除输出过滤。设置后查询 size/bans、inserter.filter 或 splitter.inPriority/outPriority/input0/output0/outFilter，再观察实际供料；不要仅凭命令成功认定堵料已经解决。
