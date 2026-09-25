# 原型属性与输送连接查询

所有示例均通过 `POST /game/state` 的 `query` 字符串提交，沿用对局就绪、分页、筛选和错误契约。Mod 提供事实，Agent 决定燃料、设备、端口和路径。

## 完整目录和字段发现

`items`、`recipes` 包含当前加载的有效原型，包括未解锁和其他 Mod 加入的条目，按 ID 升序。默认每页 256、最大 2048；按 `offset` 递增，读到不足一页后结束，整页时继续取下一页。不要把第一页当完整目录。

```graphql
query Catalog {
  metadata { gameVersion gameAssemblyVersion languageLcid schemaVersion }
  items(offset: 0, limit: 128) {
    id name type stackSize unlocked modelIndex
    heatValueJ fuelType reactorInc productive
  }
  recipes(offset: 0, limit: 128) {
    id name type unlocked handcraft productive timeSpendRaw timeSeconds
    items { itemId itemName count }
    results { itemId itemName count }
  }
}
```

不计算目录指纹，也不自动管理缓存。连接游戏、切换语言或存档后，按需重新查询原型；解锁、库存和实例状态在规划时重读。

`items.raw`、`recipes.raw` 对应原生 `ItemProto`、`RecipeProto`；`items.prefabDesc` 对应物品的默认模型描述。仅按字段读取，不遍历 Unity 资源对象。未知字段仍返回 `null`。

请求原始成员时使用原生字段名；响应沿用现有 JSON 的 camelCase 键格式，例如 `raw { HeatValue }` 返回 `raw.heatValue`。

```graphql
query Discover {
  _schema { prototypeFields { path unit sourcePath description valueScope } }
  items(limit: 1) { raw { _fields(limit: 2048) { name kind type } } }
  itemsWithBuildings: items(where: { isEntity: true }, limit: 1) {
    prefabDesc { _fields(limit: 2048) { name kind type unit sourcePath description nullable } }
  }
}
```

字段发现支持分页，不再被自动一层序列化的 128 字段上限截断。原始字段没有经确认的单位说明时，应查询源码或本文契约，不能按字段名猜单位。

## 燃料和设备能力

```graphql
query FuelAndMachines {
  items(where: { heatValueJ_gt: 0 }, limit: 128) {
    id name stackSize unlocked heatValueJ fuelType reactorInc productive
  }
  machines: items(where: { isEntity: true }, limit: 128) {
    id name unlocked
    prefabDesc {
      isAssembler assemblerRecipeType assemblerSpeedMultiplier
      isLab labSpeedMultiplier
      isPowerConsumer workPowerW idlePowerW
      isPowerGen generationPowerW fuelPowerW fuelMask
      photovoltaic windForcedPower gammaRayReceiver geothermal
      isAccumulator capacityJ chargePowerW dischargePowerW
    }
  }
  cargo { incTableMilli accTableMilli powerTableRatio }
}
```

- `heatValueJ`：每件基础热值，J；不含喷涂。
- `fuelType`、`fuelMask`：原生燃料类别位掩码。热值大于零不等于任意设备都接受。
- `reactorInc`：机甲燃料功率加成小数，基础燃料倍率为 `1 + reactorInc`；机甲科技、当前燃料及喷涂另计。实际恢复能力读取 `mecha.reactorPowerGenEnhanced` 等原生状态。
- `timeSeconds = TimeSpend / 60`：基础配方周期，游戏秒，不含设备速度。
- `assemblerSpeedMultiplier = assemblerSpeed / 10000`；`labSpeedMultiplier = labAssembleSpeed / 10000`，后者只用于研究站制造模式。
- 功率标准化为原生每 tick 能量乘 60，单位 W。`generationPowerW` 是额定能力，`fuelPowerW` 是基础燃料能量消耗率，均不是当前实际值。需要 MW 时除以一百万。
- `capacityJ` 等蓄电器字段不包含物流站、机甲或能量枢纽容量。无对应设备能力时标准化字段为 `null`，不能按零处理。
- `cargo` 的三个数组按喷涂点数索引，`incTableMilli`、`accTableMilli` 已是加成小数，倍率再加 1；`powerTableRatio` 已是总倍率。它们不是按增产剂物品 ID 索引，也不能统一用于全部特殊加工机制。

`recipes.productive` 是原生额外产出标记。普通制造的加速与增产仍结合加工类型判断；分馏、燃料、科研、光子和充放电不能直接套普通配方周期公式。喷涂剂能力等其他信息通过物品 `raw` 按需读取。

目录只是原型信息。解锁不代表库存足够，`canBuild` 不代表指定位置合法，额定功率不代表实际供电。采矿、采集等获取途径也不能伪装成普通配方。

## 垂直堆叠

通过原型属性判断能力，再通过实例状态判断当前能否叠放；不要按建筑名称猜测。

```graphql
query Stacking {
  items(where: { id: 2901 }) {
    id name unlocked
    prefabDesc {
      multiLevel lapJoint { x y z }
      multiLevelAlternativeIds multiLevelAlternativeYawTransposes
      multiLevelAllowRotate
    }
  }
}
```

示例 ID 仅用于演示，实际查询待建物品和下层实体对应的物品原型。`multiLevel == true` 才表示支持堆叠；`false` 不支持，`null` 或缺失表示未知，先用 `_fields` 确认字段。

- `lapJoint` 是下层模型局部坐标中的搭接偏移；上层位置为下层位置加下层旋转作用后的搭接偏移，不直接沿世界 Y 轴抬高。
- 上下层物品相同时可继续检查；不同时，检查待建物品的 `multiLevelAlternativeIds` 是否包含下层物品 ID。`multiLevelAlternativeYawTransposes` 按同一索引给出混合堆叠的朝向转置标记。
- `multiLevelAllowRotate` 决定能否独立指定上层朝向，否则遵守原生继承及混合堆叠朝向规则。
- 用 `factory.objectConnections(entityId: ...)` 查看 `connections { slot otherObjectId }`：15 号槽向上、14 号槽向下。上方已占用时沿连接查询实际顶层并重新检查兼容性；向 `stackOnEntityId` 传入明确选择的空闲顶层，接口不代选。
- 原型支持堆叠不等于当前位置可建。还要核对当前科技层数限制（研究站 `history.labLevel`、适用仓储类 `history.storageLevel`），并保留原生高度、碰撞、距离和材料校验。未知限制先查询原始对象字段，不自行放宽。

建成后核对 15→14 层间连接、整叠生产或研究模式、供电与实际供料。具体任务参数见 [建造接口](game-control.md#建筑传送带与分拣器)。

## 实体端口和连接

```graphql
query Endpoints {
  metadata { gameTick localPlanetId }
  factory {
    source: objectConnections(entityId: 123) {
      entityId protoId modelIndex isBelt tilt
      objectPose { position { x y z } rotation { x y z w } }
      objectPose2 { position { x y z } rotation { x y z w } }
      beltPorts { index localPose { position { x y z } rotation { x y z w } } }
      sorterSlots { index localPose { position { x y z } rotation { x y z w } } }
      connections { slot isOutput otherObjectId otherSlot }
    }
  }
}
```

`entityId` 必须为正整数，缺失、非整数、越界整数或非正数返回 `400 graphql_parse_error`；实体不存在或已回收返回 `null`。同一响应字段不能指定不同实体，查询多个实体使用别名。此入口也适用于原始 `PlanetFactory` 对象及 `localFactory` 别名，不开放任意方法调用。

| 字段 | 语义 |
| --- | --- |
| `objectPose` | 原生实体姿态，位置为行星局部坐标 |
| `objectPose2` | 原生第二姿态，例如分拣器另一端；不是每种实体都有有效第二姿态 |
| `beltPorts` | 原生 `GetLocalPorts` 的结果，传送带直连端口 |
| `sorterSlots` | 原生 `GetLocalSlots` 的结果，分拣器取放插槽 |
| `index` | 原生姿态数组索引，不能自行重编号 |
| `connections` | `ReadObjectConn` 的全部 16 个槽位，包括非输送用途的原生槽位 |
| `otherObjectId` | 0 表示无连接，正数为实体，负数为预建对象；保留原生符号 |
| `isOutput` | 当前记录相对本对象的方向；无连接时为 `null` |
| `otherSlot` | 对端原生槽位，无连接时为 `null` |

原型几何可从 `items.prefabDesc.portPoses`、`slotPoses` 读取；实体查询按实际 `modelIndex` 读取，并遵守原生堆叠隐藏规则。两套端口编号不能混用，16 个连接记录也不等于存在 16 个几何端口。

位置换算：`实体位置 + 实体旋转 × 端口局部位置`；旋转换算：`实体旋转 × 端口局部旋转`。结果仍是行星局部坐标。

分拣器接传送带时，现有命令使用带段及 `slot = -1` 的原生处理，不能伪造普通建筑插槽。用 `isBelt`、实体姿态、`tilt` 及对应带段组件核对接点和流向。物流站的输入输出模式、储物槽映射、分流器优先级等继续查相应原生组件。

提交 `placeSorter` 时，建筑端点的 `slot` 使用选定的 `sorterSlots.index`；省略时默认为 0，不会自动寻找空槽。先检查该槽的 `connections.otherObjectId`，已有连接会返回 `slot_occupied`。通常不传 `position`：命令会使用原生端点姿态；若传入，必须与上述换算后的行星局部坐标或带段位置一致（误差不超过 0.01），否则返回 `invalid_command`。完整参数见 [游戏控制](game-control.md#建筑传送带与分拣器)。

“空槽”“允许输入输出”和“此次能成功接线”不同。查询不返回推荐端口、可用路径或 `canConnect`，最终以任务执行时的原生校验为准。完成后重查连接对象和方向，再验证物料实际流动。

诊断分拣器时，同时读取 `factory.factorySystem.inserterPool` 的 `entityId pickTarget insertTarget`。实测存档存在连接槽为空、组件仍保留投放目标的情况；查询会保留原始差异，不自行修正或推断可用性。第二端姿态可与 `inserterPosePool[inserterId].pos2/rot2` 对照。

分拣器自身的连接槽 1 对应取物来源，槽 0 对应投放目标；它们与来源、目标建筑的几何插槽编号不同。核对两端对象、方向、对端插槽以及反向连接，不要仅凭位置判断实体身份。同一带段可有多个同起点分拣器；当前建造完成匹配会核对两端连接及显式插槽，再返回实际 `entityId`。旧存档的断链仍需通过普通拆建操作修复，更新 Mod 不会自动改写存档。
