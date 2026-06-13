# AutomaticDSP API 文档

本文描述当前 M1 已实现的 HTTP API。默认监听地址：

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
- `409`：游戏处于对局就绪前状态。
- `504`：等待下一次游戏查询 tick 超时。

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

## GET /tasks

查询当前进程内待执行或执行中的任务。M1 当前返回空列表。

```json
{
  "tasks": []
}
```

任务提交、取消和命令状态通过独立任务接口处理。

## GET /history

查询 SQLite 中已经完成、失败或取消的历史命令。当前实现固定返回最近 100 条。

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
      "gameTick": 123456
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
