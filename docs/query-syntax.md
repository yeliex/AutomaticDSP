# AutomaticDSP 查询语法文档

`POST /game/state` 使用 GraphQL 查询语法作为字段选择 DSL。它解析 query AST 并生成状态字段选择计划。

当前支持：

- query operation。
- 多 root 字段。
- 嵌套字段选择。
- 字段别名。
- fragment 和 inline fragment。
- 列表参数 `limit`、`offset`、`where`。
- `_schema` 查询根。
- 任意对象上的 `_fields` 字段发现。

当前支持范围：

- query operation。
- 字段选择、别名、fragment 和 inline fragment。
- 列表分页与 `where` 过滤。
- `_schema` 查询根和对象 `_fields` 字段发现。
- 游戏状态读取。

## 请求格式

```json
{
  "query": "query Observe { game { gameName gameTick } }",
  "operationName": "Observe"
}
```

`operationName` 可省略。如果一个请求中包含多个 operation，应传入 `operationName`。

## 返回规则

- 查询成功时返回 `{ "data": ... }`。
- 字段读取失败以 `null` 表示。
- 复杂对象默认返回一层可序列化字段。
- Unity 对象、委托、渲染对象和其他运行时对象通过安全 JSON 形状输出。
- 列表默认最多返回 256 项，单次 `limit` 上限为 2048。
- 对局就绪前返回 `409 game_not_ready`。

复杂对象默认返回一层字段，目的是帮助探索对象结构。例如：

```graphql
query Explore {
  localPlanet
}
```

如果只想知道某个对象有哪些可查询字段，优先使用 `_fields`。

## 查询根

第一阶段常用查询根：

- `_schema`
- `metadata`
- `game`
- `gameMain`
- `data`
- `player` / `mainPlayer`
- `mecha`
- `inventory` / `package`
- `forge` / `replicator`
- `localPlanet` / `currentPlanet`
- `localStar`
- `factory` / `localFactory`
- `factoryDetails` / `localFactoryDetails`
- `factories`
- `production`
- `power`
- `research` / `technology`
- `techs`
- `recipes`
- `items`
- `warningSystem` / `warnings`

扩展根字段会按 `GameMain` 静态成员、`GameMain.instance` 成员、`GameMain.data` 成员依次尝试读取。

`factory` / `localFactory` 返回当前行星的游戏原始 `PlanetFactory` 对象。`factoryDetails` / `localFactoryDetails` 返回面向 Agent 的实体摘要，包含 `items`、`buildingSummary`、`statusSummary` 和常用组件状态，适合在建造、设置配方或拆除后确认结果。

## 字段发现

查询根发现：

```graphql
query Discover {
  _schema {
    roots {
      name
      kind
      description
    }
  }
}
```

对象字段发现：

```graphql
query DiscoverLocalPlanet {
  localPlanet {
    _fields {
      name
      kind
      type
    }
  }
}
```

`_fields` 返回当前对象上一层可查询字段，并聚焦可 JSON 输出的字段。

## 字段选择

基础示例：

```graphql
query Observe {
  metadata {
    gameTick
    localPlanetId
  }
  game {
    gameName
    status
    gameTick
  }
  player {
    planetId
    uPosition {
      x
      y
      z
    }
  }
}
```

operation 名称只用于选择要执行的 operation。下面两个查询在字段选择上没有区别：

```graphql
query ObserveFactory {
  localPlanet { id displayName }
}
```

```graphql
query Explore {
  localPlanet { id displayName }
}
```

## 别名

同一个字段需要使用多组参数读取时，需要使用别名：

```graphql
query PagedFactory {
  firstPage: factory {
    entityPool(limit: 100, offset: 0) {
      id
      protoId
    }
  }
  secondPage: factory {
    entityPool(limit: 100, offset: 100) {
      id
      protoId
    }
  }
}
```

返回字段名使用别名：

```json
{
  "data": {
    "firstPage": {},
    "secondPage": {}
  }
}
```

## 分页

列表字段支持：

- `limit`：返回数量，默认 256，上限 2048。
- `offset`：起始偏移，默认 0。

示例：

```graphql
query InventoryPage {
  inventory {
    items {
      itemId
      name
      count
      slots
    }
    summary {
      totalItemCount
      distinctItemCount
    }
    grids(limit: 20, offset: 0) {
      itemId
      count
    }
  }
}
```

`inventory.items` 是对 `grids` 的按物品聚合视图，适合快速判断背包总量；`inventory.grids` 保留游戏原始槽位细节。`mecha.reactorStorage` 等带有 `grids` 的存储对象也支持同样的 `items` / `summary` 派生字段。

## 过滤

列表字段支持 `where`。过滤在 `offset` 和 `limit` 前执行。

```graphql
query FilterFactory {
  factory {
    entityPool(
      where: {
        id_gt: 0
        protoId_in: [2301, 2302]
        pos__x_gte: 0
      }
      limit: 20
    ) {
      id
      protoId
      pos { x y z }
    }
  }
}
```

所有 `where` 条件按 AND 组合。条件字段读取失败时视为匹配失败。

### 操作符

字段名无后缀表示等于：

```graphql
where: { protoId: 2301 }
```

已支持后缀：

- `_ne`：差异匹配。
- `_gt`：大于。
- `_gte`：大于等于。
- `_lt`：小于。
- `_lte`：小于等于。
- `_contains`：文本包含。
- `_startsWith`：文本前缀匹配。
- `_endsWith`：文本后缀匹配。
- `_in`：在列表中。

示例：

```graphql
where: {
  id_gt: 0
  protoId_in: [2301, 2302]
  displayName_contains: "铁"
}
```

### 嵌套路径

嵌套字段路径用 `__` 分隔：

```graphql
where: {
  pos__x_gte: 0
  pos__z_lt: 100
}
```

## 推荐探索流程

1. 调用 `GET /game`，确认 `ready` 为 `true`。
2. 查询 `_schema`，查看可用查询根。
3. 对目标对象查询 `_fields`，查看可查询字段。
4. 对大列表先使用小 `limit` 采样。
5. 确认字段形状后增加 `where` 和分页。

示例：

```graphql
query DiscoverThenSample {
  factory {
    _fields {
      name
      kind
      type
    }
    entityPool(limit: 5) {
      id
      protoId
      pos { x y z }
    }
  }
}
```
