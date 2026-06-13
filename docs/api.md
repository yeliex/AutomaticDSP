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
- `404`：未知路径。
- `405`：HTTP 方法不支持。
- `409`：游戏未进入可查询对局。
- `504`：等待下一次游戏查询 tick 超时。

## GET /game

轻量游戏状态探针。这个接口不进入状态查询队列，可用于判断是否可以调用 `POST /game/state`。

未进入可查询对局时：

```json
{
  "ready": false,
  "status": "menu"
}
```

进入可查询对局时：

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

## POST /game/state

按需查询游戏状态。请求会进入主线程查询队列，由游戏主线程在查询 tick 读取 DSP 对象并生成最终 JSON。

请求体：

```json
{
  "query": "query Observe { game { gameName gameTick } }",
  "operationName": "Observe"
}
```

`operationName` 可省略。`query` 必须是 GraphQL query operation，不支持 mutation。

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

未进入可查询对局时：

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

## GET /tasks

查询当前进程内待执行或执行中的任务。M1 还未实现任务提交和执行，因此当前通常返回空列表。

```json
{
  "tasks": []
}
```

任务不放在 `/game/state` 查询中；后续任务提交、取消和命令状态会继续走独立任务接口。

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
      "snapshotGameTick": 123456
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
