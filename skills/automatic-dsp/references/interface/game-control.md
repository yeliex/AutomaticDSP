# 游戏控制接口

连接地址、配置及请求格式见 [Mod 使用说明](../mod-usage.md)。

## POST /tasks：提交任务

请求通过校验后入队，操作在游戏主循环中顺序推进。执行前确认当前对局已就绪，目标来自当前存档的观察结果。

| 请求字段 | 类型 | 必需 / 默认 | 含义 |
| --- | --- | --- | --- |
| `commands` | object[] | 必需、非空 | 按数组顺序执行的操作 |
| `clientRequestId` | string | 可选 | 客户端关联标记 |
| `stopOnFailure` | boolean | 可选，true | 命令失败后停止后续操作 |
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

响应包含 `task`，保存其 `id` 用于后续查询。进程内只有一个顺序队列；前一条命令未结束，后一条不会开始。游戏中的工厂生产和已加入的研究可同时推进。

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

排队任务可直接取消；执行中的任务在主循环处理取消请求时停止后续执行。当前清理玩家订单的分支包括移动、采集、快速填充、拆除和建造；快速取出和指定仓储转移的移动副作用需重新观察。已创建的预建、已有制造和研究进度保留。

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

实体目标可使用 `entityId` 或 `target` 引用，详见 [命令结果引用](#命令结果引用)。

| 命令 | 参数 | 执行与结果 |
| --- | --- | --- |
| `entityFastFillIn` | 目标实体；`fromPackage` 默认 true | 原生快速填充；物品种类与数量由游戏决定，读取 `movedItems` |
| `entityFastTakeOut` | 目标实体；`toPackage` 默认 true | 原生快速取出；可能涉及多个物品，读取 `movedItems`、`full` |
| `transferStorageItem` | 目标储物实体、`itemId`、正整数 `count`；`direction: "fromStorage"/"toStorage"`，默认前者 | 指定物品转移；结果为 `requestedCount`、`movedCount`、`inventoryCount`、`storageCount` |
| `craftInventory` | 推荐 `itemId`、`count`；或 `recipeId`、`count` | 按物品调用时 count 为产物数量；仅按配方时 count 为配方执行次数 |

快速取放由原生逻辑决定物品与数量；储物实体的指定物品取放使用 `transferStorageItem`。该命令移动数量大于零即可成功，实际数量读取 `movedCount`；没有转移时返回 `no_item_transferred`。

`craftInventory` 使用原生递归材料判定，缺料返回 `missing_item` 和 `missing` 列表，未解锁返回 `recipe_locked`。成功等待背包目标产物达到本次制造目标；已有制造队列和其他物品流动仍需核对。

## 研究

| 命令 | 参数 | 执行与结果 |
| --- | --- | --- |
| `researchTech` | `techId`；`waitForUnlock` 默认 true | 原生入队并可等待解锁；不能入队返回 `tech_not_available` |
| `removeTechInQueue` | `index`，当前队列索引 | 原生移除并整理队列；操作前刷新队列 |
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
| `placeSorter` | `itemId`、`input` 来源端点、`output` 去向端点 |
| `dismantleEntity` | 实体目标；使用正的已建实体 ID |

传送带和分拣器分别使用专用命令。建筑位置按原生网格或矿机规则调整，旋转参数为球面朝向角；建成后读取实际位置。

端点对象可用 `entityId` 或同任务前序 `commandId`，可加 `entityIndex`、`slot` 及 `position`。位置对象使用当前行星坐标，slot 从实际端口信息取得。原始姿态或端口读取方式见 [查询](game-state.md)。

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

建造命令以预建转为实际实体后成功。拆除结果含回收物品与位置，操作后检查背包和相邻连接。取消语义见 [取消与错误](#取消与错误)。

## 等待与调试

`waitUntil` 的全部条件及作用范围见 [等待](#等待)。`noop` 用于调试。
