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

## 戴森设计与蓝图

戴森云轨道、球层、节点／框架／壳面编辑，以及原生蓝图校验、导入、导出，见 [戴森球与蓝图建设](../guides/dyson-sphere.md) 和 [完整参数](../../../../docs/api.md#戴森云戴森球与原生蓝图)。设计编辑即时完成，不代表实际竣工；工厂 `applyFactoryBlueprint` 属于建造通道，等待全部实体落成。优先查询原始戴森对象和科技，不把布局策略交给 Mod。

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

当前命令覆盖行星内基础操作、飞行导航、运输站配置、物流配送器、物流背包、地形植被、生产设置、原生蓝图应用和戴森球设计编辑。战斗专用控制接口待支持。戴森球编辑参数见下文，蓝图使用边界见 [蓝图施工](../guides/dyson-sphere.md#蓝图施工)。

下列命令对象放入 `POST /tasks` 的 `commands` 数组。可加 `id` 和 `timeoutSeconds`。示例数字仅用于说明格式，执行前必须替换为当前数据。

## 移动与采集

| 命令 | 参数 | 执行与结果 |
| --- | --- | --- |
| `moveTo` | `position: {x,y,z}`；可选 `planetId: "current"` 或当前行星 ID，`tolerance` 默认 2 | 当前行星局部坐标；等待位置与目标距离在容差内 |
| `mineTarget` | `targetType: "vein"/"vege"`、`targetId`；可选 `itemId`、`itemCount`（兼容 `count`，默认 1）、`untilDepleted` | 原生采集；达到数量或目标耗尽结束，检查 `depleted` 和实际 `items` |
| `autoReplenishMechaFuel` | 无必需附加参数 | 调用机甲原生自动补燃料；结果含 `movedItems`、`reactorItems`、补充前后能量 |
| `autoReplenishMechaWarper` | 无必需附加参数；需解锁机甲曲速飞行 | 调用原生 `Mecha.AutoReplenishWarper()`，从普通背包向翘曲仓补充最多 20 个真实翘曲器；结果含实际 `movedCount`、`warperCount`、`packageCount`。已有翘曲器但未转移也可成功；两处均为空返回 `missing_warper`。不改变自动补充开关。 |

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
| `transferStorageItem` | 目标储物仓实体（不含储液罐）、`itemId`、正整数 `count`；`direction: "fromStorage"/"toStorage"`，默认前者 | 指定物品转移；结果为 `requestedCount`、`movedCount`、`inventoryCount`、`storageCount` |
| `craftInventory` | 推荐 `itemId`、`count`；或 `recipeId`、`count` | 按物品调用时 count 为产物数量；仅按配方时 count 为配方执行次数 |
| `removeForgeTask` | 当前 `forge.tasks` 的 `index` 与 `recipeId` | 原生取消制造及关联父/子任务并返还材料；索引或配方变化返回 `queue_changed` |

快速取放由原生逻辑决定物品与数量；储物实体的指定物品取放使用 `transferStorageItem`。该命令移动数量大于零即可成功，实际数量读取 `movedCount`；没有转移时返回 `no_item_transferred`。

`fromPackage` 只选择物品来源，不代表一次填满。手持与背包填充都可能只转入少量物品，按每次 `movedItems` 和输入槽、库存变化核对数量，不假定手持只允许填入一件。

`entityFastFillIn` 和 `entityFastTakeOut` 没有 `count` 参数，不能给它们附加数量并假定生效。需要限制生产建筑的投料数量时，先确认手中为空，用 `transferInventoryItem {from:"package",to:"hand",itemId,count}` 拆出最多所需数量，再在同一有序任务中调用 `entityFastFillIn {entityId,fromPackage:false}`。实际填入可能少于手中数量，查询 `movedItems` 和手中余量，按需用 `transferInventoryItem` 将余量退回背包。此方式限制投料上限，不保证设备接收足量。

当前原生 `PlanetFactory.EntityFastFillIn` 的燃料发电站分支，每次手持填充最多 1 件、背包填充最多 2 件；手中或背包有更多燃料也不会在一次调用中全部投入。按 `movedCount`、手中／背包余量和各机组实际燃料缓存确认补给，定量均衡多台机组时逐批执行，不能将拆出的数量当作已填入数量。这是该原生快捷方法的批次限制，不代表所有建筑或其他游戏投料操作都采用相同批次。

储物仓和运输站货槽分别通过 `transferStorageItem`、`transferStationItem` 指定数量取回；生产建筑成品暂没有直接指定数量取回接口，`entityFastTakeOut(toPackage:false)` 也不表示限量。不要将储物仓接口套用到制造设施，或为了取少量成品而切换配方清空缓存。

普通运输站的 `entityFastFillIn` 会补充运输工具，不能视为指定货槽投料。传送带可用 `fromPackage:false` 放入手中真实物品，原生单次投入数量读取 `movedCount`。输入站端口的 `storageIdx` 可能随接收物品更新，它是原生货槽缓存，不是用户设置的输入过滤。

`craftInventory` 使用原生递归材料判定，缺料返回 `missing_item` 和 `missing` 列表，未解锁返回 `recipe_locked`。`waitForCompletion` 默认 false，成功仅表示原生入队，结果为 `action: enqueued, completed: false`。设为 true 时后台跟踪本次原生制造任务剩余次数，制造完成后成功；不以背包净增判断，避免产物被并行操作消耗导致误报。等待结果含 `remainingCount`、`forgeTotalTime`；原生任务在未完成时被移除返回 `craft_interrupted`。

`craftInventory` 只要求玩家的原生机甲制造器可用，不要求本地行星；可在太空下达，也可让 `waitForCompletion:true` 的完成跟踪跨越起飞、航行和着陆。仍由原生制造器检查配方、材料并推进制造，需预留机甲能源和递归中间产物的背包空间。

`removeForgeTask` 按执行时的队列校验索引和配方；即使配方相同也应在移除前刷新数量、父子关系和进度。取消子任务可能连带取消父任务及后续缺料任务，退款遵守原生背包容量及掉落规则。移除后重读整个 `forge.tasks`，不能继续沿用旧索引。

## 飞行与导航

飞行命令占用机甲指令通道，科研与手搓仍可并行。Agent 选择目的地并准备能源；`navigateTo` 自动控制航向、加减速和着陆，使用 `flightInput` 时则需自行计算输入方向和持续时间。

| 命令 | 参数 | 完成依据 |
| --- | --- | --- |
| `takeOff` | 可选 durationTicks，默认 600 | 原生双跳输入后进入 Fly |
| `navigateTo` | 三选一：planetId（可附局部 position）、当前星球 position、太空双精度 uPosition；可选布尔值 useWarp（默认 false）、tolerance（星球 0.5–10 m，默认 3；太空 0.5–100 m，默认 20）、timeoutSeconds（默认 900） | 航行闭环；固态星实际接地或水面稳定漂浮（Drift、相对表面高度不超过 3 米、局部速度不超过 2 m/s）、气态星在指定表面位置上方稳定悬停、太空进入距离容差即记录到达（含逐 tick 轨迹穿越），退出曲速后成功，不要求停车；Sail 自动解锁光标，人工操作期间临时让出输入 |
| `land` | 可选 durationTicks，默认 600；仅接受 Fly 状态 | 原生下降后进入 Walk 且接地；气态行星拒绝 |
| `flightInput` | 必填 mode 为 fly/sail、direction 向量、durationTicks（1–3600）；thrust 默认 0，范围 -1..1；lift 默认 0，范围 -1..1；boost 默认 false | 输入持续指定游戏 tick，或原生移动模式发生转换；成功不代表抵达目的地 |
| `warp` | 可选 durationTicks，默认 600 | 原生曲速按键入口后 warpCommand 和 warping 均为真 |
| `exitWarp` | 可选 durationTicks，默认 600 | 原生退出后 warpCommand 和 warping 均为假 |

`navigateTo.useWarp=true` 允许在航向、科技、能量和翘曲器满足原生条件且尚未到达时启动曲速，接近目标时按原生退出尾程提前退出；原生启动没有目标距离下限，不可用时继续普通航行。低能退出曲速后，远程航行在完全退出至少 60 游戏 tick 且核心恢复至容量 90% 后重新检查原生曲速条件；接近目标时改用普通航行，不反复启动。普通巡航在航向对准且保有制动能量时优先加速到当前航速上限，不固定扣留 30% 核心容量。结果 `useWarp` 表示请求选项，`warpUsed` / `warpStatus` 表示实际使用情况；直接 `warp` 命令不满足前置条件时会失败。

原生 `PlayerMove_Sail.GameTick` 的曲速启动校验也没有普通航速下限：Sail 状态、控制器可用且机甲存活、推进器等级至少 3、本地行星为空、核心能量严格大于 `warpStartPowerPerSpeed * maxWarpSpeed`，以及原生消费／自动补充翘曲器成功。`VFInput._warpKey` 只读取按键，不检查速度。校验嵌在原生按键处理内，没有独立的 `CanWarp` 方法；Mod 预检对应条件后仍生成按键，让原生方法最终判定。导航的航向及速度方向对齐属于引导要求，不是原生速度门槛。

导航独占机甲通道，后续移动、建造下达和其他导航等待释放。执行器周期检查输入包装，原生控制器重建动作数组时恢复包装；发现其他飞行输入占用时返回 `flight_input_conflict`，不抢占。运行结果每 60 游戏 tick 更新 `appliedTicks`、`distanceToTarget` 与真实飞行状态，`inputReleased:false` 表示仍持有输入；结束后为 true。`manual_override` 的 `overrideReason` 指出移动、加速、曲速、停止、光标锁定或原生订单等接管来源。

导航规划发现燃烧室各格为空且剩余燃料能量低于 10000 J 时，会调用原生 `Mecha.AutoReplenishFuelAll` 从背包补燃料，不更改自动补充设置。导航可能因此消耗原计划给建筑启动的燃料；需要保留的少量投料可先用 `transferInventoryItem` 拆到手中，抵达后用 `entityFastFillIn(fromPackage:false)`，按实际转移量核对。不要把采集成功时的背包数量当成导航后仍可投料的数量。

`navigateTo` 在科研等全屏界面、文本输入、截图模式或星图打开期间继续逐 tick 驱动原生移动动作，界面按键不视为移动接管；原超时继续生效，不反复重建任务。游戏暂停时不会推进物理运动。正常游戏视图中的移动、停止、Tab，以及显式建造或手动导航订单使导航临时让出输入，阶段为 `manualOverride`，任务和原超时保留；输入释放、原生订单结束后，下一次判断从实际位置重新规划并继续。需要永久停止时取消任务。低层固定时长飞行输入仍返回 `manual_override`；会话变化、控制器不可用及超时仍终止。

`arrivalDistance` 记录太空点首次判定到达时的最近距离，尚未到达或目标为行星时为 null。`distanceToTarget` 是终态实际距离，可能因退出曲速的惯性超过 tolerance。

fly 的 direction 是本行星局部方向，水平输入投影到当前位置切平面，lift 为升降输入；sail 的 direction 是宇宙方向单位向量，thrust=1 转向该方向，-1 执行原生制动，0 松开推进键（中间值仍受原生输入阈值控制），boost 对应航行加速键。它们都不是目标位置。fly 持续上升并水平移动可以在原生推进器等级满足时进入 Sail；sail 接近地表后的模式转换仍由原生处理。进入 Fly 后再调用 land。

固态星 `navigateTo` 检查实际 Physics 碰撞体：建筑占用落点时，在周围 36 米内寻找可落地位置；近地航向与速度每 6 游戏 tick 更新，帧间维持已有原生输入；每 30 游戏 tick 复核前方短航段，保留仍安全的停靠点和可达绕行点，仅在受阻或抵达绕行点时重新寻路。到达容差不叠加为碰撞体积；进入容差并减速后锁定安全落地位置。通过结果 `localRouteChecks`、`localRouteSearches`、`maxLocalRouteMilliseconds` 核对复核次数、求路次数及最大单次耗时（毫秒）。结果 `landingPosition` 表示实际停靠点，`distanceToTarget` 对该点计算；到达要求请求容差内实际接地，或接近水面的稳定 Drift；结果保留真实 grounded 和 movementState，不把漂浮记为接地。没有空地或局部通路时返回 `landing_area_blocked` 或 `local_route_blocked`，应根据现场更换落点，不能反复重试相同任务。

`moveTo` 使用本行星局部坐标。宇宙位置与速度查询 `player.uPosition` / `player.uVelocity`，坐标分量保持双精度；目标行星的 `uPosition` 随游戏时间变化。按目标类型核对上表中的完成条件。

取消、超时或命令结束仅撤销本命令输入，不停速、不强制降落、不自动退出曲速。终态 result 返回真实 movementState、planetId、局部与宇宙位置、宇宙速度、warpCommand、warpState、coreEnergy 及 appliedTicks。低层固定时长飞行输入在键盘移动、曲速/加速键、原生移动订单、导航或建造接管时会中止并返回 manual_override；navigateTo 临时让出后恢复。默认输入租期为 120 秒，显式 timeoutSeconds 可覆盖。

加载存档或地形改造后，若导航以 `manual_override` 且 `appliedTicks: 0` 立即结束，先检查原生订单及界面是否仍处于地表改造或建造模式。通过 Computer Use 确认并退出残留工具与建造模式，再验证导航；退出可能需要先取消工具，再退出建造模式。

科技不足为 tech_locked；能源不足为 insufficient_energy；曲速缺翘曲器为 missing_warper。曲速入口由原生耗能和消费方法执行，自动补充遵守原有设置。拒绝前置条件不会主动消耗翘曲器。正常运行中的产线、手搓、燃烧室仍可能改变能量与库存。

## 戴森云与戴森球设计

这些命令属于机甲指令通道，需有效整数 `starId`。云轨道需要 `history.dysonSphereSystemUnlocked`，球层及结构需要 `history.dysonSphereLayerPanelUnlocked`。Mod 使用原生初始化和设计编辑方法，不选择恒星或生成布局。

| 命令 | 参数 |
| --- | --- |
| `createDysonOrbit` | `radius`、`inclination`、`longitude` |
| `editDysonOrbit` | `orbitId`、`radius`、`inclination`、`longitude` |
| `setDysonOrbitEnabled` | `orbitId`、布尔 `enabled` |
| `removeDysonOrbit` | `orbitId` |
| `createDysonLayer` | `radius`、`inclination`、`longitude` |
| `editDysonLayer` | `layerId`、`inclination`、`longitude`；不能修改半径 |
| `removeDysonLayer` | `layerId`；原生连带拆除层内结构 |
| `createDysonNode` | `layerId`、`position:[x,y,z]`（层局部非零方向，归一化到层半径）；可选 `protoId` |
| `createDysonFrame` | `layerId`、`nodeAId`、`nodeBId`；可选布尔 `euler`（默认 false，测地线）、`protoId` |
| `createDysonShell` | `layerId`、`nodeIds`（至少三个不同节点，按实际框架闭环顺序排列，不重复首节点）；可选 `protoId` |
| `removeDysonNode` / `removeDysonFrame` / `removeDysonShell` | `layerId` 与对应的 `nodeId` / `frameId` / `shellId`；删节点或框架会连带移除关联结构 |

轨道 ID 为 1–20，球层 ID 为 1–10；倾角为 0–180 度，升交点经度为 0–360 度，数值必须有限且不能是数字字符串。半径由原生恒星范围、行星避让和层间距校验。默认云轨道 1 不能停用或删除，有帆轨道须先停用并等待帆寿命结束，再删除。

结构 `protoId` 默认 0，合法样式由原生编辑器确定。保留应力纬度、节点间距、框架长度与交叉、壳面闭环和内部节点校验；拒绝结果含 `nativeCondition`，按现场状态修改设计，不绕过原生限制。

球层和结构命令成功仅表示设计完成，结果 `completion:"design"`，不表示火箭或太阳帆已施工。按 [戴森球与蓝图建设](../guides/dyson-sphere.md) 查询节点、框架、壳面实际完成点数及功率，供料和发射遵守游戏原生规则。

## 生产模式与发射轨道

以下命令接受 `entityId` 或 `target` 实体引用，以及可选 `planetId`；进入机甲指令通道，要求实体在当前行星及机甲建造范围内，超范围返回 `out_of_range`。

| 命令 | 参数 | 原生约束与结果 |
| --- | --- | --- |
| `setRayReceiverMode` | `mode: power / photon` | 射线接收站发电／光子模式；光子需解锁原型指定的产物，否则 `item_locked`。转回发电返还整数缓存产物，清空产物缓存；背包溢出按原生机制抛出垃圾。返回 `mode`、`productId`。 |
| `setEjectorOrbit` | 非负整数 `orbitId`；可选布尔 `autoOrbit` | 电磁轨道弹射器调用原生 `SetOrbit`；0 为不指定轨道，正值须对应已启用的现有轨道，否则 `invalid_orbit`。未提供 autoOrbit 时保留当前设置；自动换轨开启时游戏之后可改变目标。返回实际 `orbitId`、`autoOrbit`。 |
| `setProliferatorMode` | `mode: extra / speed` | 需解锁增产剂科技（1151），否则 `tech_locked`。制造组件需当前配方支持额外产出；研究站需矩阵生产模式，否则 `invalid_recipe`。同步堆叠研究站的模式。返回 `mode`、`forceAccMode`；不补充增产剂或喷涂点。 |

设置结果通过原生 `factory.powerSystem.genPool` 的 `productId`、`factory.factorySystem.ejectorPool` 的 `orbitId/autoOrbit`、`assemblerPool/labPool` 的 `forceAccMode` 核对。射线接收产量、发射可达性及喷涂效果仍取决于原生运行条件。垂直发射井的节点选择由原生系统负责，此接口不指定节点或创建戴森球结构。

## 研究

| 命令 | 参数 | 执行与结果 |
| --- | --- | --- |
| `researchTech` | `techId`；`waitForUnlock` 默认 false | 原生入队并可等待解锁；不能入队返回 `tech_not_available` |
| `removeTechInQueue` | `index`，当前队列索引；推荐同时传 `techId` | 原生移除并整理队列；指定 techId 时校验目标，变化返回 `queue_changed` |
| `buyoutTech` | `techId` | 使用跨存档元数据，需用户明确要求；与正常研究材料不同 |
| `setLabResearchMode` | 实体目标；可选 `techId`，默认当前研究科技 | 设置矩阵研究站研究模式，并同步相邻研究站函数 |
| `setRecipe` | 实体目标、正整数 `recipeId` | 对支持配方的制造组件或研究站设配方；研究站用于矩阵生产模式。不能用 0 清空或暂停配方；调整供料时使用原生物流操作，并记录恢复连接所需信息。 |

`waitForUnlock: false` 成功表示已入队或已经解锁，读取 `unlocked`、`inQueue` 区分。`true` 在后台等待原生解锁，也不占用机甲指令通道；只有显式依赖该命令的操作等待研究完成。

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
| `setStationStorage` | 运输站实体目标、零起始 `storageIndex`、`itemId`（普通站可用 0 清空）；可选 `max`（默认科技允许容量）、`localLogic` / `remoteLogic`（None / Supply / Demand，默认 None）；采集槽限制见下文 |
| `setStationVehicles` | 普通运输站实体目标、`itemId`（5001 无人机 / 5002 运输船）、目标总数 `count`；计入正在工作的工具，实际从背包放入或将闲置工具取回 |
| `setStationChargePower` | 普通运输站实体目标、整数 `powerMW`，按原生 3 MW 刻度与建筑原型允许范围设置充电上限；不改变储能和电网实际供电 |
| `setSplitterPriority` | 四向分流器实体目标、端口 `slot`（0–3）、布尔值 `priority` |
| `upgradeEntity` | 实体目标、目标等级物品 `itemId`；在机甲建造范围内原地升级 |
| `cancelPrebuild` | 正整数 `prebuildId`；可选当前 `planetId` | 在建造范围内调用原生预建拆除和退款；需先取消等待该预建的 Mod 任务，刷新 `factory.prebuildPool` 后提交 |

传送带和分拣器分别使用专用命令。建筑位置按原生网格或矿机规则调整，旋转参数为球面朝向角；建成后读取实际位置。

喷涂机等 `prefabDesc.addonType: Belt` 建筑仍用 `placeBuilding`，执行原生附属建筑吸附、传送带连接和碰撞校验。喷涂机建成后读取 `factory.cargoTraffic.spraycoaterPool` 的 `cargoBeltId`、`incBeltId`，分别确认物料带和增产剂带；两条带的高度和方向按原型 `addonAreaPoses` 规划。原生吸附可能改变实际位置、朝向和高度，须用建成实体的 `pos`、`rot` 同时换算连接姿态的位置与方向；只让增产剂带末端落在入口坐标、却沿错误方向接近，仍可能返回碰撞。建成不代表已喷涂，还需检查电力、增产剂供应与货物 `inc`。

`placeBuilding` 可用 `stackOnEntityId` 替代 `position`，明确指定原生支持堆叠的下层已建实体；位置和朝向由原生搭接点确定。该实体上方槽位 15 必须空闲，不自动寻找顶层；检查物品兼容性后仍执行原生层数、碰撞、物资、范围校验及无人机建造。研究站完成后查询并按需设置整叠生产或研究模式。

`upgradeEntity` 只接受同一原生升级系列的更高等级物品，检查科技、目标物资和建造范围后调用 `PlayerAction_Build.DoUpgradeObject`，并核对实际物品 ID。原生流程消耗新设备并返还旧设备，不通过拆除重建实现；完成后重新查询配方、连接与物流运行状态。返回 `previousItemId`、`itemId`、`entityId` 和 `nativeError`。

`reverseBelt` 示例：`{"type":"reverseBelt","entityId":273}`。先查询该带段的 `segPathId` 及运输路径，确认影响范围；反转作用于原生路径中的全部带段（2–1023 个），不是单个实体或整张物流网络。游戏原生逻辑保留货物并调整连接，无法放回的货物返还背包或形成垃圾；分支连接可能断开。返回受影响 `entityIds` 及前后路径 ID，完成后检查端口、分拣器和实际输送。命令属于机甲指令队列。重复执行会再次反向，请求结果不确定时先查询原任务和实际连接，不要盲目重试；`clientRequestId` 不提供去重。

端点对象可用 `entityId` 或同任务前序 `commandId`，可加 `entityIndex`、`slot` 及 `position`。位置对象使用当前行星坐标，slot 从实际端口信息取得。原始姿态或端口读取方式见 [查询](game-state.md)。

`placeBelt` 返回的 `entityIds` 不保证与 `points` 同序，已有端点也可能不在返回数组中。`entityIndex` 是结果数组索引，不能直接套用规划点的下标；需要选择拐角、末段或汇入段时，先查询返回实体的实际 `pos` 与连接再引用。

`points` 可同时提供 `start`、`end` 实体端点，用显式点列表达中间路径。两个端点都是已有带段时，接口移除与端点重合的预览；必须保留实际新点，否则返回 `invalid_command`（没有新建点）。短连接先按原生最小间距选择合法中间点，不能把只有两个已有端点的请求当作单独连线命令。

显式 `points` 与 `start`／`end` 同用时，实体端点只建立连接关系，不会自动补齐端口处的实体带段。矿机输出带应包含真实端口锚点，再沿端口方向接出直线段；不能把首段留在端口外数米，仅凭能出矿判断接头完整。原生 `BuildTool_Path` 的建带流程也会加入端口锚点。用实际 `objectPose` 和 `beltPorts.localPose` 换算，保持原生 `Vector3`／`Quaternion` 的单精度运算；端口附近的浮点偏差可能被原生转角校验视为回折，出现 `TooBend` 时先核对锚点与朝向，不一律排除端口。验收检查连接、锚点实际落点和画面中的接头。升降带在两端预留连续水平带段，接塔前完成坡度过渡；`JointCannotLift` 时按失败段及实际端口方向调整，不反复提交相同路径。

曲线带段连接分拣器时，在取放接点前后保留与建筑插槽配对的直线段；曲线终点的理论切线不代表建成后的带段朝向。按实际姿态核对 `endpointAngle`，超过原生容许角度时修正相邻带段，不反复提交同一分拣器。

长带线创建预建后虽释放机甲通道，无人机仍需实际落成。离开施工区前查询原任务和全局 `construction`；必要时按未完成预建的实际位置移动，并等待清零。任务超时或取消不会撤销已下达的预建，不能重复铺设来恢复；先检查现有预建和实际连接。

`placeSorter` 的建筑端点必须使用未占用的原生插槽（占用返回 `slot_occupied`）；省略 `slot` 时默认 0，不自动选槽。可选 `position` 必须与该插槽换算后的行星局部坐标或传送带段位置一致（误差不超过 0.01），不能覆盖端点坐标；不匹配或插槽越界返回 `invalid_command`。通常只传实体和插槽即可。

缩短分拣器距离时，从已建实体的实际 `pos`、`rot` 和插槽姿态换算接料点，不能沿用建造请求中未经原生网格吸附的中心坐标。当前原生 `BuildTool_Inserter.CheckBuildConditions` 对端点间距小于 1 米先返回 `Failure`，后续还检查网格距离与朝向；最短可用距离仍须通过原生校验，不能将带段贴到插槽上。普通分拣器可用 `factorySystem.inserterPool.stt` 与对应等级的 `items.prefabDesc.inserterSTT` 比值复核原生行程；比较不同等级时先归一化，不能仅凭 `stt` 大小判断距离。

原生分拣器的 `pickOffset/insertOffset` 是相对于所接带段 `segIndex + segPivotOffset` 的偏移，零表示带段中心，不表示整条路径起点。带上有货但分拣器不取货时，同时核对所接带段附近的实际货物、过滤、朝向与插槽；首段或弯道可能需要改用附近平直段，不能仅凭偏移为零判断接口漏传参数。

```json
{
  "commands": [
    {"id": "矿机", "type": "placeBuilding", "itemId": 2301, "veinId": 4, "position": {"x": 120, "y": 35, "z": -80}, "rotation": 90, "timeoutSeconds": 120},
    {"id": "出矿带", "type": "placeBelt", "itemId": 2001, "start": {"commandId": "矿机", "slot": 0}, "end": {"x": 126, "y": 35, "z": -78}, "timeoutSeconds": 120}
  ]
}
```

Agent 提供路径；`startPosition` / `endPosition` 使用原生线段吸附，`points` 使用显式点列。下发后检查实际 `entityIds` 及连接。

显式点列引用建筑端点时同样检查该实体实际开放的输送端口；无端口或插槽越界返回 `invalid_command`，不能用原型端口或显式端点位置越过堆叠后的端口限制。

使用 `points` 时，点列直接作为新带段预览，不自动插值或吸附；`start` / `end` 可同时指定已有实体连接。建筑直连端口的锚点须由点列提供，按上文的端口姿态与单精度规则换算；不要连续重复同一个点，也不要为避免 `TooBend` 一律将首末点移到端口外侧。未提供 `points` 的直线模式则从解析后的实际端点生成原生吸附线段。两种模式均经过原生建造校验。

传送带原生建造校验失败时，结果包含 `condition`、零起始 `previewIndex` 和失败段实际 `position`；没有具体失败预览时后两者为 null。索引对应实际预览列表，已有端点可能被移除，不保证等于请求点列索引。根据失败位置检查连接和碰撞，不整条路径盲目重试。连接已有对象且需要升降时，坡道两端各保留至少两个同高度的新预览带段；已有实体端点可能从预览列表移除，不能把它算作所需的水平新带段。坡道与拐角之间也应保留水平直线段；`JointCannotLift` 表示端点升降，`TooBendToLift` 表示转弯处同时升降，应先分开端点、坡道和转弯，再按原生校验复核。

分拣器 `input` 是取物来源，`output` 是放物去向，例如来源传送带、去向熔炉。引用带段时用 `entityIndex` 选择连接段。

分拣器端点需通过原生预览配对的朝向／高度容差，无法匹配时返回 `invalid_connection`，结果含端点角度与径向高度差；之后仍执行原生距离和碰撞校验。架高带须先回到建筑插槽高度，不能靠指定实体 ID 越过端点配对规则。传送带高度由实际路径坐标表达，与 `stackOnEntityId` 无关。

`placeSorter` 成功结果包含 `itemId`、`entityId` 和单元素 `entityIds`。完成匹配同时核对两端连接及显式建筑插槽，避免把同起点的旧分拣器误认为新实体。后续操作使用返回 ID，并通过 [连接查询](prototypes-and-connections.md#实体端口和连接) 核对现场。

建造命令以预建转为实际实体后成功。拆除结果含回收物品与位置，操作后检查背包和相邻连接。取消语义见 [取消与错误](#取消与错误)。

建造成功、失败、超时或取消后，退出该命令进入的建造模式；尚未建成的预建不自动删除。同组命令失败导致建造命令标为 `CANCELLED` 时，预建或实体也可能已经生成或落成；重试前核对实体和预建池，不能只凭任务状态重复施工。取消 Mod 任务、移除原生制造/科研、取消预建是不同操作：`POST /tasks/{id}/cancel` 不撤销已入队的制造/科研或已创建的预建。等待中的科研被移除后返回 `research_interrupted`。预建已转为实体时不会按旧预建 ID 拆除实体，需重新观察。

普通 `placeBuilding` 的原生建造校验失败返回 `condition`、`itemId`、`modelIndex`、实际预览 `position` 和四元数 `rotation`。这些字段用于核对吸附、模型与碰撞体，不提供推荐位置；附加建筑走独立校验流程。

### 传送带高度与转弯诊断

传送带架高是路径高度与坡道问题，与建筑 `multiLevel` 垂直堆叠不同。交叉、避让和多路输送时，比较局部抬升跨越与平面绕行，合理使用不同高度减少地面占用；计入坡道长度、转弯空间和可用净空，避免把长绕路简单搬到空中。通过路径点的球面径向高度表达架高，遵守原生高度、坡度和空间碰撞限制；不能用全局 `y` 判断同一水平面。分拣器取放段应与所接建筑层的实际插槽同高，并留出平直段；相邻坡道可能使等径向高度的带段姿态仍倾斜，须核对实际 `objectPose.rotation` 与 `tilt`，不能只用端点高度判断水平。连接底层时下降到其插槽高度，连接上层时按上层实际姿态计算，不能用斜跨高度的分拣器补救架高带。接口还会校验原生端点朝向容差，少量球面／模型偏差不等于允许跨架高层。规划坡道时核对当前科技是否已解除线路坡度限制；线路首尾仍保留水平接头，原生 `JointCannotLift` 时先检查端点相邻带段，不能用坡度科技绕过接头规则。架高与下降使用逐点缓坡，坡道与转弯之间保留平直段；原生 `TooSteep` 时核对相邻点的径向高度差和水平间距，`TooBendToLift` 时分开升降与转弯，不能只延长整条线路却保留同一处急坡或坡底转弯。

原生传送带避碰校验可能将部分预览沿径向调整约 0.667 米；因此请求点列同高，实际校验中的相邻段仍可能产生坡度。若平直拐角报 `TooBendToLift`，检查附近已有传送带的实际高度与净空，避免让拐角或相邻段触发避碰抬升／下降；调整跨越高度或横向位置后再验证，不只重复增加同高点。

当前原生校验在带段两侧夹角小于 2.5 弧度（约 143°）且任一侧球面坡度绝对值超过 0.1 时拒绝坡底转弯。直角两侧应保持实际平直，升降从转角之外开始；附近带段避碰导致的高度变化同样计入，不能只按请求点列检查。

## 地形改造、还原与植被移植

这些命令在 `instruction` 通道一次执行完成，与移动、采集及建造下达互斥，不创建预建或等待无人机。需要存活的机甲位于已加载的固态行星，且不在航行或序幕中；可选 `planetId` 限定当前星球。目标超过 `mecha.buildArea` 时返回 `out_of_range`，先移动再提交。

| 命令 | 参数 |
| --- | --- |
| `reformTerrain` | 必填 `position`；`mode: "flatten" / "restore"`，默认 flatten；`brushSize` 1–10，默认 1；`brushType` 1–7，默认 1；`brushColor` 0–31，默认 0；`buryVeins` 默认 false |
| `collectVegetation` | 必填现场植被 `vegeId`，来自 `factory.vegePool` 的有效 id |
| `plantVegetation` | 必填收藏中的 `protoId` 和 `position`；可选球面朝向角 `rotation`，默认 0 |

`reformTerrain` 要求解锁地基（物品 1131）。位置按原生改造网格吸附；距离按吸附后的中心检查。改造使用原生点列、增产点数、沙土及地基结算，包含矿脉和植被副作用、基地禁改区域裁剪与基地坑的扩展费用。类型 7 是无装饰地基，不是地形还原；`restore` 才调用原生还原逻辑，并保留实体等对象的受保护区域。restore 不使用 brushType/brushColor，但传入时仍须在合法范围。

还原地基进入手持物品，空手时会选中空的地基手持槽。手持非地基物品返回 `hand_item_conflict`，不会强行清空背包或丢弃物品。沙土不足返回 `insufficient_sand`，地基不足返回 `missing_item`。沙土收支可能不对称，以游戏原生计算为准；还原不是事务回滚。

结果含吸附后的 `position`、`foundationDelta`、`sandDelta`（正数增加，负数消耗）、`heightBefore/heightAfter`、`changedCells {index,before,after}` 及 `nativeAreaMode`（0 普通、1 禁改区域裁剪、2 基地坑扩展）。网格字节高 3 位是改造类型，低 5 位是颜色。重复操作或受保护区域可能没有变化；不能用 `SUCCEEDED` 代替实际范围验证。掩埋、露矿仍遵循原生遮挡规则。后续建造显式依赖改造命令。

植被收取进入 `player.vegetableCollection.playerVegeDict`，不产出手挖材料；种植调用原生碰撞校验，并从收藏扣除一个对应原型。不会移植矿脉、飞行仓或特效对象。普通模式缺少收藏返回 `missing_vegetation`；沙盒仅遵循游戏已启用的原生沙盒规则，不由命令开启。成功结果含现场 `vegeId`、`protoId`、实际 `position`、`collectionCount` 和 `collectionDelta`；种植还返回 `nativeCondition`。碰撞失败返回 `collision` 和原生条件。收取后原对象 ID 不再有效，种植返回新对象 ID，可能复用原空槽。

```json
{"commands":[{"id":"整平","type":"reformTerrain","position":{"x":155.2258,"y":-8.439054,"z":128.382874},"brushSize":1},{"type":"reformTerrain","mode":"restore","dependsOn":["整平"],"position":{"x":155.2258,"y":-8.439054,"z":128.382874},"brushSize":1}]}
```

坐标仅是测试档示例，实际目标需重新查询。植被目录与收藏查询见 [游戏状态](game-state.md#植被目录与收藏)。区域规划与植被品种选择由外部 Agent 决定。

## 信息提示

`dismissNotice` 需要 `notifications.notices` 或 `ui.notices` 中条目的 `noticeId`。确认该记录；仍可见且身份未变化时，在主线程调用对应原生关闭方法，已自行收起则仅确认记录。同一记录重复确认成功；记录不存在或已切换对局返回 `notice_not_found`。支持即时通道。读取状态不会确认提示，未确认消息每次查询都会返回，由 LLM 理解后及时关闭。不会自动确认错误、存档或其他决策对话框，也不会把游戏目标标记为完成或忽略。详情见 [提示与地形查询](game-state.md#提示与地形查询)。

## 物流设置

基础物流设置使用 `setStorageLimit`、`setSorterFilter`、`setSplitterPriority`，都在机甲指令队列执行并要求选中实体在建造范围内。仓储 `maxSlots` 限制自动化格位数量，0 禁止自动化存入、size 解除限制，不会清除超限库存；不要把格数当作物品数。分拣器 `itemId:0` 清除过滤，修改不会丢弃正在搬运的货物。分流器 `slot` 为 objectConnections 的 0–3 端口，必须已连接已建传送带；`priority:true/false` 设置或取消该端口优先级，输入和输出独立。取消输出优先级也会按原生规则清除输出过滤。设置后查询 size/bans、inserter.filter 或 splitter.inPriority/outPriority/input0/output0/outFilter，再观察实际供料；不要仅凭命令成功认定堵料已经解决。

`setStationStorage` 支持 `planetId` 指定已建立工厂的远程星球，省略或 current 使用机甲所在星球；太空中须指定正整数 planetId。复用原生货槽设置，禁止重复物品，普通行星站不支持星际供需。远程替换非空货槽时，旧货物按原生总控规则留在来源行星成为垃圾；本地替换按原生规则退回背包，放不下时形成垃圾。同物品修改容量与供需不清空库存。max 是每个货槽的物流需求上限，本地和星际共用；省略时按科技容量默认，因此只改供需时也应显式传入要保留的 max。在途到货可以造成超限库存。

`setStationVehicles` 和直接取放货物仍要求当前星球与建造范围；充电与其他远程设置见下文。`setStationVehicles` 不召回忙碌中的工具，缺少物品时失败；取回按原生顺序进入普通背包、已配置物流格、手中，结果 count 是扣除全部返还后的站内总数，inventoryCount/deliveryCount/handCount 分别反映去向。原生返还可能抛出原先手中物品，应先处理手中库存并核对垃圾。轨道采集器固定货槽不接受普通运输站设置命令。

原生总控允许远程开启运输机/船自动补充，原生方法从机甲普通背包扣除工具；Mod 已通过 setStationSetting 的 droneAutoReplenish / shipAutoReplenish 提供该开关及原生调用。翘曲器可通过货槽物流需求到货，再由原生运输 tick 补入专用仓。运输船货槽库存不等于已安装运力，不得直接增加计数。其他远程设置与航路命令见 [API 文档](../../../../docs/api.md)。

## 配送器、物流背包与指定转移

以下设置和转移走 instruction 通道，不等待手搓或科研；建筑目标仍须在当前星球和建造范围内。物流背包开关只控制自动配送，关闭后仍可手动转移。

配置物流格前查询 `player.deliveryPackage { activeCount _pos2index(limit:100) _index2pos(limit:100) }`：`index` 是 `grids` 的原始索引，不是界面可见格序号。使用 `_pos2index` 中非负的值，或核对 `_index2pos[index] >= 0`；不能假定解锁后第 0 格可用。Mod 按原生 `IsGridActive(index)` 校验。

| 命令 | 参数与语义 |
| --- | --- |
| `setDeliveryEnabled` | 必填布尔值 enabled；要求物流背包已解锁 |
| `setDeliverySlot` | 必填原生零起始 index、itemId（0 清空）；可选 requireCount、recycleCount。同物品省略阈值时保留，更换时默认 0/2147483647；换物品前必须取空，不允许重复物品 |
| `transferInventoryItem` | from/to 为 package、delivery、hand 中不同两项；itemId、正整数 count。只向指定库存转移，不自动改投其他背包；目标未接收部分及增产点退回来源 |
| `setDispenser` | 配送器实体目标；可选 itemId、playerMode（None/Supply/Recycle/Both）、storageMode（None/Supply/Demand），省略保留。itemId=-1 表示全部回收，仅允许 playerMode=Recycle、storageMode=None |
| `setDispenserCouriers` | 配送器实体目标、目标机器人总数 count；含工作中机器人，不能取回工作中的部分。投入来自普通背包，取回使用原生背包/物流格/手中返还 |
| `transferStationItem` | 普通运输站或采集器实体目标、已配置 itemId、正整数 count；direction=fromStation/toStation（默认前者），inventory=package/delivery/hand（默认 package）。仅当前星球建造范围内；手动存入容量按科技允许容量，货槽 max 是物流阈值 |

转移结果读取 movedCount：有部分转移即可成功，零转移为 no_item_transferred；数量与增产点均保留。目标手中有其他物品时不会覆盖。普通背包、物流背包、手中分别对应真实库存，手中物品还可能被后续原生输入处理，需要连续操作时放在同一有序任务中。

物流格并非从 0 连续开放；查询已解锁格位后使用其 index。阈值是原生半堆步长（奇数堆为整堆），有限值最多 30 堆，2147483647 表示无限，requireCount 不得大于 recycleCount。替换非空格返回 storage_not_empty，重复物品返回 duplicate_item。机甲补货/回收按原生库存统计和阈值执行。

配送器使用 `placeBuilding {itemId:2107,stackOnEntityId:<顶层箱子>}`；不传猜测高度。普通箱子端口 15 和配送器端口 13 需符合原生连接规则。已有配送器的箱子再叠箱，原生会抬升配送器并重新连接新顶层；随后查询 dispenser.storageId 确认。物品供需匹配、供货箱选择、范围限制及机器人调度均由游戏处理；设置成功不等于已完成运输，需检查电力、pairCount、工作机器人及实际库存变化。

查询原生 `factory.transport.dispenserPool` 时，物品字段为 `filter`，例如 `where:{filter:1109}`；不要把设置命令的 `itemId` 当作组件字段，否则空列表不能证明没有对应配送器。

从运输站输出的 `placeBelt` 必须在同一命令提供 `filterItemId`，选择已配置货槽中的物品；0 表示不选择输出物品。星际站已解锁运输船曲速时也可选 1210。该字段在创建预建前写入原生输出端口，带段建成前过滤已经生效；其他类型的连接不接受此字段。输入运输站的传送带由原生货槽需求接收，不支持独立物品过滤。通过 start / end 的实体与端口指定输入输出方向。

查询运输站原生 `slots.storageIdx` 时，0 表示没有货槽映射，普通货槽的映射值对应 `storage[storageIdx - 1]`；星际站的特殊值 6 表示翘曲器专用缓冲，不对应普通五槽数组中的 `storage[5]`。先区分专用缓冲与普通货槽，再核对数组范围；该字段与 `setStationStorage.storageIndex` 的零起始编号不同。核对输出物品时同时读取 `slots`、`storage` 和实际连接，不直接用 `storageIdx` 作为数组索引。

## 运输站远程设置与航路

`setStationStorage`、`setStationChargePower`、`setStationSetting` 使用 `planetId` + `entityId`（或既有实体引用）。目标必须有已建立工厂；省略 planetId 使用当前行星，太空中必须指定。直接取放货物、`setStationVehicles` 仍要求本地和建造范围。

`setStationStorage` 也支持原生采集槽：大矿机只允许 `localLogic: None/Supply`、`remoteLogic: None`；轨道采集器只允许 `remoteLogic: None/Supply`，`localLogic` 必须保留原值。两者必须传现有槽位的原 `itemId`，不能清空、换物品或设为 Demand；容量使用 `prefabDesc.stationMaxItemCount + history.localStationExtraStorage`，轨道采集器也不使用星际塔容量科技。降低 max 不删除已有库存。应先查原槽的物品、容量和模式，再只改目标值。`transferStationItem` 支持在本地范围内手动取放采集槽的原物品，保留增产点数，并使用上述容量限制；普通站的充电、飞船等设置仍不支持采集器。

`setStationSetting` 每条只修改一个 `setting`，`value` 类型严格检查；失败不修改该设置。例：

```json
{"commands":[{"type":"setStationSetting","planetId":104,"entityId":143,"setting":"shipAutoReplenish","value":true}]}
```

| setting | value 与原生含义 |
| --- | --- |
| droneRangeDegrees | 整数角度，范围取当前游戏原生滑块；内部存储余弦 tripRangeDrones |
| shipRangeLightYears | 整数 1..20、22..60 偶数，或 10000（原生无限远）；内部乘 2400000 |
| warpDistanceAU | 0.5..3 的半整数、4..12 整数、14..20 偶数，或 60；内部乘 40000 |
| deliveryDrones / deliveryShips | 整数 1 或 10..100 的 10 倍数，表示最低起送百分比 |
| warperNecessary / includeOrbitCollector | boolean，翘曲器必备／包含轨道采集器 |
| pilerCount | 整数 0..当前科技允许值；0 跟随科技，1 不堆叠，上限由 3801..3803 的原生解锁值计算 |
| droneAutoReplenish / shipAutoReplenish | boolean；调用原生 StationAutoReplenishIfNeeded，从机甲普通背包实际扣除 5001/5002，补至工具容量。结果 consumedCount、inventoryCount、vehicleCount 反映实际结果；空背包可以成功开启但补充数为零 |
| remoteGroupMask | 非负整数位掩码，第 n 位对应原生第 n 个分组按钮；拒绝超出原生按钮数量的位 |
| routePriority | 原生枚举字符串 Ignore / Prioritize / Only / Designated |

船、曲速、采集器、远程分组和优先级仅适用于星际站。上述接口不修改船速、载量、能源或科技。自动补充不接收目标数量，货槽中的 5001/5002 不计为运力。1210 货槽到货由原生运输 tick 自动补专用翘曲器仓。

`setLogisticsRoute` 使用 `kind`、整数 `fromId` / `toId`、布尔 `present`。它表达外部已选定的双向关系，不做选址、供需规划或寻路：

- `kind: "station"`：fromId/toId 是不同星球的星际站 **gid**；present=true 添加点对点关系，false 删除。
- `kind: "astro"`：fromId/toId 为原生天体 ID（恒星为 starId × 100，行星为 planetId）；另须 `itemId`，添加或删除该物品的星际航路。
- `kind: "ban"`：同上，添加或删除禁运关系。`itemId: 0` 表示原生全部物流物品，使用前明确评估影响。

重复添加／删除保持幂等；原生增删方法负责刷新交通。分组和优先级修改也刷新交通，不召回已经起飞的船。航路条目、禁运与配对从 [原生物流查询契约](game-state.md#原生物流订单与配对) 读取。

## 配送器远程设置与大矿机速度

`setDispenser`（过滤物品、机甲和仓储配送模式）及 `setDispenserSetting` 支持 `planetId` 指定远程已建立工厂，实体目标沿用 entityId/target。直接 `setDispenserCouriers` 仍要求本地与建造范围。

`setDispenserSetting` 使用 `setting` + `value`，每次只修改一项：

- `courierAutoReplenish`：boolean，调用原生 `EntityAutoReplenishIfNeeded`。从普通背包扣除真实物品 5003，按原生工具容量补充；返回 consumedCount、inventoryCount、courierCount。空背包可以开启但不增加机器人；不从货箱转换运力。
- `chargePowerKW`：整数千瓦，须在目标配送器原型的原生范围内且为 300 的倍数。只设置消费者 workEnergyPerTick，不增加储能或供电。

`setVeinCollectorSpeed` 使用同样的远程实体目标，参数 `speedPercent` 是原生滑块范围内、以 10 为步长的整数百分比。只接受大矿机及其有效采矿组件；普通矿机拒绝。结果包含 minSpeedPercent/maxSpeedPercent 与实际原生 speed（百分比 × 100）。原生 tick 继续计算耗电、采矿产出及矿量消耗，命令不解锁科技或创建矿机。

```json
{"commands":[{"type":"setDispenserSetting","planetId":102,"entityId":1423,"setting":"courierAutoReplenish","value":true},{"type":"setDispenserSetting","planetId":102,"entityId":1423,"setting":"chargePowerKW","value":900}]}
```

示例实体须替换为当前存档查询结果。读取 `factories { transport { dispenserPool { courierAutoReplenish idleCourierCount workCourierCount pcId } } factorySystem { minerPool { id speed } } powerSystem { consumerPool { id workEnergyPerTick } } }` 核对；各池按有效 ID 过滤分页。
