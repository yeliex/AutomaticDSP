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

当前命令覆盖行星内基础操作、飞行导航、运输站配置、物流配送器、物流背包、地形植被和生产设置。蓝图应用、戴森球编辑与战斗专用控制接口待支持。规划涉及这些操作时，可先完成材料与产线准备，并记录待执行部分。

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

`entityFastFillIn` 和 `entityFastTakeOut` 没有 `count` 参数，不能给它们附加数量并假定生效。需要限制生产建筑的投料数量时，先确认手中为空，用 `transferInventoryItem {from:"package",to:"hand",itemId,count}` 拆出最多所需数量，再在同一有序任务中调用 `entityFastFillIn {entityId,fromPackage:false}`。实际填入可能少于手中数量，查询 `movedItems` 和手中余量，按需用 `transferInventoryItem` 将余量退回背包。此方式限制投料上限，不保证设备接收足量。

储物仓和运输站货槽分别通过 `transferStorageItem`、`transferStationItem` 指定数量取回；生产建筑成品暂没有直接指定数量取回接口，`entityFastTakeOut(toPackage:false)` 也不表示限量。不要将储物仓接口套用到制造设施，或为了取少量成品而切换配方清空缓存。

普通运输站的 `entityFastFillIn` 会补充运输工具，不能视为指定货槽投料。传送带可用 `fromPackage:false` 放入手中真实物品，原生单次投入数量读取 `movedCount`。输入站端口的 `storageIdx` 可能随接收物品更新，它是原生货槽缓存，不是用户设置的输入过滤。

`craftInventory` 使用原生递归材料判定，缺料返回 `missing_item` 和 `missing` 列表，未解锁返回 `recipe_locked`。`waitForCompletion` 默认 false，成功仅表示原生入队，结果为 `action: enqueued, completed: false`。设为 true 时后台跟踪本次原生制造任务剩余次数，制造完成后成功；不以背包净增判断，避免产物被并行操作消耗导致误报。等待结果含 `remainingCount`、`forgeTotalTime`；原生任务在未完成时被移除返回 `craft_interrupted`。

`removeForgeTask` 按执行时的队列校验索引和配方；即使配方相同也应在移除前刷新数量、父子关系和进度。取消子任务可能连带取消父任务及后续缺料任务，退款遵守原生背包容量及掉落规则。移除后重读整个 `forge.tasks`，不能继续沿用旧索引。

## 飞行与导航

飞行命令占用机甲指令通道，科研与手搓仍可并行。Agent 选择目的地并准备能源；`navigateTo` 自动控制航向、加减速和着陆，使用 `flightInput` 时则需自行计算输入方向和持续时间。

| 命令 | 参数 | 完成依据 |
| --- | --- | --- |
| `takeOff` | 可选 durationTicks，默认 600 | 原生双跳输入后进入 Fly |
| `navigateTo` | 三选一：planetId（可附局部 position）、当前星球 position、太空双精度 uPosition；可选布尔值 useWarp（默认 false）、tolerance（星球 0.5–10 m，默认 3；太空 0.5–100 m，默认 20）、timeoutSeconds（默认 900） | 航行闭环；固态星实际接地、气态星在指定表面位置上方稳定悬停、太空进入距离容差即记录到达（含逐 tick 轨迹穿越），退出曲速后成功，不要求停车；Sail 自动解锁光标，Tab 可接管 |
| `land` | 可选 durationTicks，默认 600；仅接受 Fly 状态 | 原生下降后进入 Walk 且接地；气态行星拒绝 |
| `flightInput` | 必填 mode 为 fly/sail、direction 向量、durationTicks（1–3600）；thrust 默认 0，范围 -1..1；lift 默认 0，范围 -1..1；boost 默认 false | 输入持续指定游戏 tick，或原生移动模式发生转换；成功不代表抵达目的地 |
| `warp` | 可选 durationTicks，默认 600 | 原生曲速按键入口后 warpCommand 和 warping 均为真 |
| `exitWarp` | 可选 durationTicks，默认 600 | 原生退出后 warpCommand 和 warping 均为假 |

`navigateTo.useWarp=true` 允许在距离、航向、科技、能量和翘曲器满足条件时自动启动曲速，接近目标时退出；不可用时继续普通航行。结果 `useWarp` 表示请求选项，`warpUsed` / `warpStatus` 表示实际使用情况；直接 `warp` 命令不满足前置条件时会失败。

`navigateTo` 在科研等全屏界面、文本输入、截图模式或星图打开期间继续逐 tick 驱动原生移动动作，界面按键不视为移动接管；原超时继续生效，不反复重建任务。游戏暂停时不会推进物理运动。正常游戏视图中的移动、停止，以及显式建造或手动导航订单仍返回 `manual_override`。低层固定时长飞行输入保持原有界面中断行为；燃料不足、会话变化和真正的手动接管不自动重试。

`arrivalDistance` 记录太空点首次判定到达时的最近距离，尚未到达或目标为行星时为 null。`distanceToTarget` 是终态实际距离，可能因退出曲速的惯性超过 tolerance。

fly 的 direction 是本行星局部方向，水平输入投影到当前位置切平面，lift 为升降输入；sail 的 direction 是宇宙方向单位向量，thrust=1 转向该方向，-1 执行原生制动，0 松开推进键（中间值仍受原生输入阈值控制），boost 对应航行加速键。它们都不是目标位置。fly 持续上升并水平移动可以在原生推进器等级满足时进入 Sail；sail 接近地表后的模式转换仍由原生处理。进入 Fly 后再调用 land。

`moveTo` 使用本行星局部坐标。宇宙位置与速度查询 `player.uPosition` / `player.uVelocity`，坐标分量保持双精度；目标行星的 `uPosition` 随游戏时间变化。按目标类型核对上表中的完成条件。

取消、超时或命令结束仅撤销本命令输入，不停速、不强制降落、不自动退出曲速。终态 result 返回真实 movementState、planetId、局部与宇宙位置、宇宙速度、warpCommand、warpState、coreEnergy 及 appliedTicks。键盘移动、曲速/加速键、原生移动订单、导航或建造接管会中止输入并返回 manual_override。默认输入租期为 120 秒，显式 timeoutSeconds 可覆盖。

科技不足为 tech_locked；能源不足为 insufficient_energy；曲速缺翘曲器为 missing_warper。曲速入口由原生耗能和消费方法执行，自动补充遵守原有设置。拒绝前置条件不会主动消耗翘曲器。正常运行中的产线、手搓、燃烧室仍可能改变能量与库存。

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
| `setRecipe` | 实体目标、`recipeId` | 对支持配方的制造组件或研究站设配方；研究站用于矩阵生产模式 |

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
| `setStationStorage` | 普通运输站实体目标、零起始 `storageIndex`、`itemId`（0 清空）；可选 `max`（默认科技允许容量）、`localLogic` / `remoteLogic`（None / Supply / Demand，默认 None） |
| `setStationVehicles` | 普通运输站实体目标、`itemId`（5001 无人机 / 5002 运输船）、目标总数 `count`；计入正在工作的工具，实际从背包放入或将闲置工具取回 |
| `setStationChargePower` | 普通运输站实体目标、整数 `powerMW`，按原生 3 MW 刻度与建筑原型允许范围设置充电上限；不改变储能和电网实际供电 |
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

传送带原生建造校验失败时，结果包含 `condition`、零起始 `previewIndex` 和失败段实际 `position`；没有具体失败预览时后两者为 null。索引对应实际预览列表，已有端点可能被移除，不保证等于请求点列索引。根据失败位置检查连接和碰撞，不整条路径盲目重试。

分拣器 `input` 是取物来源，`output` 是放物去向，例如来源传送带、去向熔炉。引用带段时用 `entityIndex` 选择连接段。

分拣器端点需通过原生预览配对的朝向／高度容差，无法匹配时返回 `invalid_connection`，结果含端点角度与径向高度差；之后仍执行原生距离和碰撞校验。架高带须先回到建筑插槽高度，不能靠指定实体 ID 越过端点配对规则。传送带高度由实际路径坐标表达，与 `stackOnEntityId` 无关。

`placeSorter` 成功结果包含 `itemId`、`entityId` 和单元素 `entityIds`。完成匹配同时核对两端连接及显式建筑插槽，避免把同起点的旧分拣器误认为新实体。后续操作使用返回 ID，并通过 [连接查询](prototypes-and-connections.md#实体端口和连接) 核对现场。

建造命令以预建转为实际实体后成功。拆除结果含回收物品与位置，操作后检查背包和相邻连接。取消语义见 [取消与错误](#取消与错误)。

建造成功、失败、超时或取消后，退出该命令进入的建造模式；尚未建成的预建不自动删除。取消 Mod 任务、移除原生制造/科研、取消预建是不同操作：`POST /tasks/{id}/cancel` 不撤销已入队的制造/科研或已创建的预建。等待中的科研被移除后返回 `research_interrupted`。预建已转为实体时不会按旧预建 ID 拆除实体，需重新观察。

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

运输站设置同样要求当前星球与建造范围。`setStationStorage` 复用原生货槽设置：禁止重复物品，普通行星站不支持远程物流；替换货物按原生规则退回背包，放不下时形成垃圾。`setStationVehicles` 不召回忙碌中的工具，缺少物品时失败；取回按原生顺序进入普通背包、已配置物流格、手中，结果 count 是扣除全部返还后的站内总数，inventoryCount/deliveryCount/handCount 分别反映去向。原生返还可能抛出原先手中物品，应先处理手中库存并核对垃圾。轨道采集器固定货槽不接受普通运输站设置命令。

## 配送器、物流背包与指定转移

以下设置和转移走 instruction 通道，不等待手搓或科研；建筑目标仍须在当前星球和建造范围内。物流背包开关只控制自动配送，关闭后仍可手动转移。

| 命令 | 参数与语义 |
| --- | --- |
| `setDeliveryEnabled` | 必填布尔值 enabled；要求物流背包已解锁 |
| `setDeliverySlot` | 必填原生零起始 index、itemId（0 清空）；可选 requireCount、recycleCount。同物品省略阈值时保留，更换时默认 0/2147483647；换物品前必须取空，不允许重复物品 |
| `transferInventoryItem` | from/to 为 package、delivery、hand 中不同两项；itemId、正整数 count。只向指定库存转移，不自动改投其他背包；目标未接收部分及增产点退回来源 |
| `setDispenser` | 配送器实体目标；可选 itemId、playerMode（None/Supply/Recycle/Both）、storageMode（None/Supply/Demand），省略保留。itemId=-1 表示全部回收，仅允许 playerMode=Recycle、storageMode=None |
| `setDispenserCouriers` | 配送器实体目标、目标机器人总数 count；含工作中机器人，不能取回工作中的部分。投入来自普通背包，取回使用原生背包/物流格/手中返还 |
| `transferStationItem` | 普通运输站实体目标、已配置 itemId、正整数 count；direction=fromStation/toStation（默认前者），inventory=package/delivery/hand（默认 package）。手动存入容量按科技允许容量，货槽 max 是物流阈值 |

转移结果读取 movedCount：有部分转移即可成功，零转移为 no_item_transferred；数量与增产点均保留。目标手中有其他物品时不会覆盖。普通背包、物流背包、手中分别对应真实库存，手中物品还可能被后续原生输入处理，需要连续操作时放在同一有序任务中。

物流格并非从 0 连续开放；查询已解锁格位后使用其 index。阈值是原生半堆步长（奇数堆为整堆），有限值最多 30 堆，2147483647 表示无限，requireCount 不得大于 recycleCount。替换非空格返回 storage_not_empty，重复物品返回 duplicate_item。机甲补货/回收按原生库存统计和阈值执行。

配送器使用 `placeBuilding {itemId:2107,stackOnEntityId:<顶层箱子>}`；不传猜测高度。普通箱子端口 15 和配送器端口 13 需符合原生连接规则。已有配送器的箱子再叠箱，原生会抬升配送器并重新连接新顶层；随后查询 dispenser.storageId 确认。物品供需匹配、供货箱选择、范围限制及机器人调度均由游戏处理；设置成功不等于已完成运输，需检查电力、pairCount、工作机器人及实际库存变化。

从运输站输出的 `placeBelt` 必须在同一命令提供 `filterItemId`，选择已配置货槽中的物品；0 表示不选择输出物品。星际站已解锁运输船曲速时也可选 1210。该字段在创建预建前写入原生输出端口，带段建成前过滤已经生效；其他类型的连接不接受此字段。输入运输站的传送带由原生货槽需求接收，不支持独立物品过滤。通过 start / end 的实体与端口指定输入输出方向。
