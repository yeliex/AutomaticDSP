# 游戏管理接口

存档、新建、加载和退出均优先使用 Mod 接口，不通过 Computer Use 操作菜单。启动游戏使用 Steam 命令，见初始化指南。

## POST /game/exit：退出程序

请求体可省略或提交 `{}`，只退出，不调用保存。需要保留进度时，调用方先独立调用 `/game/save`，确认 `saved: true` 后再退出。保存接口以同名 `saveName` 覆盖已有存档。返回 `accepted: true, status: exiting` 表示已接受，随后主线程调用原生退出程序流程；这不是进程已经关闭的证明，应检查进程退出。加载期间拒绝退出。更新 Mod 时也使用此接口，不通过杀进程替代正常退出。

连接地址、配置及请求格式见 [Mod 使用说明](../mod-usage.md)。

初始化流程见 [游戏初始化](../guides/initialization.md)。

响应示例展示关键字段，其他字段按实际响应读取。以下 HTTP 示例中的 Host 使用实际服务地址。

## GET /game：读取对局状态与控制选项

无查询参数、无请求体，可在菜单或对局中调用。

```http
GET /game HTTP/1.1
Host: <游戏主机:端口>
```

对局运行时响应示例（节选）：

```json
{"ready":true,"status":"running","gameName":"agent-session","gameTick":123456}
```

HTTP 200 返回状态对象：

| 字段 | 含义 |
| --- | --- |
| `ready`、`status` | 状态查询是否可用，以及菜单、加载、运行、暂停或序幕等状态 |
| `controls` | 当前状态下的游戏控制可用性 |
| `newGameDefaults` | 新游戏参数默认值，种子每次响应可重新生成 |
| `newGameParameters` | 新游戏参数说明与可用选项 |
| `gameName`、`gameTick`、`gameTime` 等 | 已加载对局的名称和时间 |
| 模式、资源倍率、恒星数量等 | 已加载对局的规则与配置 |

`ready: true` 表示可调用状态查询接口，暂停或序幕中也可能为 true；需要动作推进时，结合 `status` 和连续观测的 `gameTick` 判断。完整世界观测使用 [游戏状态查询](game-state.md)。

## POST /game：创建新游戏

调用条件：`status: menu`。请求体可为空；按任务需求选择参数，默认值与可用范围从 `GET /game` 读取。

所有请求字段均可省略，省略时使用 `newGameDefaults`。常用字段：

| 字段 | 类型 | 含义与默认值 |
| --- | --- | --- |
| `galaxySeed` | integer | 种子，默认随机；可从状态响应取值 |
| `starCount` | integer | 恒星数，默认 64 |
| `resourceMultiplier` | number | 资源倍率，默认 1 |
| `mode` | string | `peace` / `combat`，默认 `combat` |
| `isSandboxMode` | boolean | 默认 false |
| `skipPrologue` | boolean | 默认 true |
| `galaxyAlgo` | integer | 默认当前游戏的生成算法 |
| `playerProto` | integer | 默认 1 |
| `goalLevel` | string | 默认 `Full`，可选值从参数说明读取 |
| `combatSettings` | object | 默认游戏战斗设置；子字段及范围从 `newGameParameters` 读取 |

需要复现对局时，固定提交选定的 `galaxySeed`；读取 `newGameDefaults` 时生成的种子不会自动绑定到下一次创建请求。

模式也接受 `isPeaceMode` / `isCombatMode` 布尔字段；选用一种表达即可。

请求示例为和平、非沙盒对局：

```http
POST /game HTTP/1.1
Host: <游戏主机:端口>
Content-Type: application/json; charset=utf-8


{"galaxySeed":12345678,"starCount":64,"resourceMultiplier":1,"mode":"peace","isSandboxMode":false,"skipPrologue":true}
```

HTTP 200 返回 `started`、`status`、`desc` 与 `skipPrologue`。`started: true` 表示开始创建，继续查询 `GET /game` 确认进入对局。省略选项时当前默认战斗模式、非沙盒、跳过序幕。

响应示例（节选）：

```json
{"started":true,"status":"loading","desc":{"galaxySeed":12345678,"starCount":64,"isPeaceMode":true},"skipPrologue":true}
```

## GET /game/saves：列出存档

无请求体。HTTP 200 返回 `saveFolder`、`count` 和 `items`。每项包含 `saveName`、`fileName`、`path`、`isUserSave`、`fileSize`、`lastWriteTimeUtc`，并尽量提供解析出的 `header` 与 `desc`。使用返回的 `saveName` 加载存档。

请求与响应示例（节选）：

```http
GET /game/saves HTTP/1.1
Host: <游戏主机:端口>
```

```json
{"saveFolder":"C:/Users/player/Documents/Dyson Sphere Program/Save/","count":1,"items":[{"saveName":"agent-session","fileName":"agent-session.dsv"}]}
```

## POST /game/save：保存当前对局

调用条件：已加载对局。必填字段 `saveName` 为非空 string，使用存档名。

请求：

```http
POST /game/save HTTP/1.1
Host: <游戏主机:端口>
Content-Type: application/json; charset=utf-8

{"saveName":"agent-session"}
```

HTTP 200 返回 `saved`、`saveName`、`path`、`status`。存档名按用户要求选择，写入同名存档会替换其内容。

响应示例：

```json
{"saved":true,"saveName":"agent-session","path":"C:/Users/player/Documents/Dyson Sphere Program/Save/agent-session.dsv","status":"running"}
```

## POST /game/load：加载存档

调用条件：`status: menu`。必填字段 `saveName` 为非空 string，取自存档列表。

请求：

```http
POST /game/load HTTP/1.1
Host: <游戏主机:端口>
Content-Type: application/json; charset=utf-8

{"saveName":"agent-session"}
```

HTTP 200 返回 `started`、`saveName`、`status`。成功启动时 `status: loading`，等待 `GET /game` 就绪后再查询和操作。

响应示例：

```json
{"started":true,"saveName":"agent-session","status":"loading"}
```

## POST /game/prologue/skip：跳过序幕

无需请求体。HTTP 200 返回 `skipped` 与 `status`；序幕中跳过后进入运行状态，其他状态返回 `skipped: false`。

请求与响应示例：

```http
POST /game/prologue/skip HTTP/1.1
Host: <游戏主机:端口>
Content-Length: 0
```

```json
{"skipped":true,"status":"running"}
```

## 错误

错误响应为 `{"error":{"code":"...","message":"..."}}`，游戏控制错误还可包含 `status`。

| HTTP | code | 含义 |
| --- | --- | --- |
| 400 | `bad_json` | 请求 JSON 不合法 |
| 400 | `bad_request` | 参数或存档名不合法 |
| 404 | `save_not_found` | 存档不存在 |
| 409 | `invalid_game_status` | 当前状态不允许该操作 |
| 504 | `control_timeout` | 等待游戏控制 tick 超时 |

控制请求超时后，先查询当前对局及存档判断是否已生效，再决定是否重发。
