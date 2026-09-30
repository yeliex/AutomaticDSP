# 分析面板统计查询

通过 `POST /game/state` 的 `statistics` 查询游戏原生分析面板的数据，无需打开面板。它提供统计及单位转换；瓶颈分析、产线配比和优化选择由 Agent 完成。

## 范围与时间

`statistics(astroFilter: 0, timeLevel: 0)`：

| 参数 | 含义 |
| --- | --- |
| `astroFilter: 0`（默认） | 当前行星；在太空中为当前恒星系，无当前天体时为全局 |
| `astroFilter: -1` | 全局已有工厂，不加载未开发行星 |
| 正整数行星 ID | 指定行星 |
| 恒星 ID × 100 | 指定恒星系；不是直接传恒星 ID |
| `timeLevel: 0..4` | 最近 1 分钟、10 分钟、1 小时、10 小时、100 小时 |
| `timeLevel: 5` | 原生累计量；速率为 null |

范围不存在返回 `available:false, reason:astro_not_found`。参数类型或范围错误会拒绝查询。已有行星没有工厂时，`factoryCount:0`，统计为空或零，不创建工厂。使用别名可在一次请求中比较不同范围和时间窗口。

顶层可选字段：`available`、`gameTick`、解析后的 `astroFilter`、`scope`（planet/star/galaxy）、`factoryCount`、`timeLevel`、`windowSeconds`、`windowFilled`、`historyLevel`、`trafficAvailable`、`trafficScope`、`includesPlayerStorage`、`productionExtraInfo`。

速率采用完整窗口作分母，与面板一致；游戏时间不足窗口时仍有零填充，`windowFilled:false`。该标记只判断存档游戏时间，不保证某个工厂已运行满窗口，也不能证明存档历史未经重置。游戏暂停不增加统计时间。

## 物品产消、运输与库存

```graphql
{
  short: statistics(astroFilter: 0, timeLevel: 0) {
    available gameTick astroFilter factoryCount windowSeconds windowFilled
    trafficAvailable trafficScope includesPlayerStorage
    productionExtraInfo { source freshness calculating }
    products(where: { itemId_in: [1101, 1104, 6001] }, limit: 16) {
      itemId name produced consumed productionPerMinute consumptionPerMinute
      referenceProductionPerMinute referenceConsumptionPerMinute
      imported exported importPerMinute exportPerMinute
      storage playerStorage importStorage exportStorage trash
    }
  }
  long: statistics(astroFilter: 0, timeLevel: 1) {
    products(where: { itemId_in: [1101, 1104, 6001] }, limit: 16) {
      itemId productionPerMinute consumptionPerMinute
    }
  }
}
```

- `produced/consumed/imported/exported` 是所选窗口数量，`*PerMinute` 为件/分钟。全局面板没有进出口统计，相关值和历史返回 null，不能解释为零；行星范围采用该行星出入运输记录，恒星系采用跨恒星系记录，不把内部运输重复计入。未产生运输记录的有效范围返回零。
- `referenceProductionPerMinute/referenceConsumptionPerMinute` 是原生参考产能，按当前设备、配方及原生规则计算，不是已经实现的产速或保证交付量。
- `storage` 是原生工厂库存加符合范围条件的玩家库存；包含设备缓存、带上物料及原生统计涵盖的物流物料，不能直接视为可取用储物仓余量。需要取料时仍查询具体容器。
- `playerStorage` 单独返回计入总库存的玩家部分；全局始终计入，恒星系在玩家属于该星系时计入，行星在玩家位于该行星且非航行状态时计入。
- `importStorage/exportStorage` 是星际运输站远程需求／供应货槽的现存数量，是 `storage` 的分类信息，不能再加到总库存，也不是运输中数量或窗口进出口量。`trash` 单独统计，不属于可用生产库存。
- 参考产能、`storage/playerStorage/importStorage/exportStorage/trash` 读取原生缓存；查询不主动执行或排队刷新。面板未打开或游戏尚未更新这些缓存时，值可能陈旧或尚未计算。`productionExtraInfo` 返回 `source:nativeCache`、`freshness:unknown`、原生 `calculating` 和最近一次计算耗时 `lastCalculationMilliseconds`；后者不是更新时间，也不证明本次范围已更新。`gameTick` 只标记查询时刻，不能当作缓存时间戳。缓存零值不能独立证明库存或参考产能为零，关键判断应结合实际产消和具体容器、设备状态。
- 常规诊断先查目标行星，只有需要跨星系比较时再扩大范围。列表按物品 ID 排序，使用通用 `where/limit/offset`；未出现于统计或缓存库存中的物品不列出。

## 电力与研究

```graphql
{
  statistics(astroFilter: 0, timeLevel: 0) {
    power {
      currentWindowSeconds generationCapacityWatts consumptionDemandWatts
      chargeWatts dischargeWatts shieldChargeWatts storedJoules consumptionJoulesTotal
    }
    research { hashes hashesPerSecond }
  }
  research { currentTechId hashUploaded hashNeeded queueLength isStalled }
}
```

电力的 `*Watts` 对齐面板最近 60 tick（1 游戏秒）的统计：发电能力、用电需求、蓄电充电／放电和行星护盾充能。供需是能力与需求，不等于实际发出或使用的能量；总量比较也不能证明不同电网之间互相供电。定位缺电仍查询 `power.netPool`、`networkServes` 和设备所在电网。

`storedJoules` 是范围内有效电网当前储能；`consumptionJoulesTotal` 是原生累计实际耗电。另可读取所选窗口的 `generationCapacityJoules`、`consumptionDemandJoules`、`chargeJoules`、`dischargeJoules`、`shieldChargeJoules`；前两者仍是对应供需统计的能量积分，不应当作实际利用量。

`statistics.research` 是范围内工厂研究统计：`hashes` 为所选窗口累计研究量，`hashesPerSecond` 为 hash/秒。当前科技及队列使用已有根 `research`；两者不能互相替代，也不要用矩阵产量直接代替科研速度。

## 戴森球

```graphql
{
  statistics(astroFilter: 100, timeLevel: 0) {
    dyson {
      products { itemId name produced consumed productionPerMinute consumptionPerMinute }
      spheres {
        starId name generationWatts requestedWatts sailCount
        structurePoints structurePointsRequired cellPoints cellPointsRequired
      }
    }
  }
}
```

将示例 `100` 替换为目标恒星 ID × 100。`products` 对应面板原生虚拟统计项 `11901/11902/11903`，分别标记为太阳帆、结构点、细胞点，保留原生生产／消耗口径。它们不是普通物品原型，也不混入常规 `products` 列表；不能用普通物品制造量冒充结构落成量。`spheres` 读取已存在的戴森对象，返回功率、需求、太阳帆数及节点汇总的已建／设计结构点和细胞点；没有初始化的球不列出，查询不会创建球。

行星范围的戴森 `products` 来自该行星工厂，`spheres` 则为其所在恒星的整个戴森系统。全局分别聚合工厂统计、列出已有恒星戴森对象。戴森发电、接收需求与地面电网可用功率不同，验收前明确目标口径。

## 历史曲线

在物品行显式选择 `productionHistory`、`consumptionHistory`、`importHistory`、`exportHistory`；电力选择 `generationCapacityHistory`、`consumptionDemandHistory`、`chargeHistory`、`dischargeHistory`、`shieldChargeHistory`；研究选择 `history`。例如：

```graphql
{
  statistics(astroFilter: 0, timeLevel: 0) {
    products(where: { itemId: 6001 }, limit: 1) {
      itemId
      productionHistory {
        timeLevel sampleTicks pointCount unit lastPointPartial
        points(offset: 540, limit: 60) { index value }
      }
    }
  }
}
```

点按从旧到新排列，`index` 是本次曲线内索引，不是绝对 tick。产消、电力、研究各 600 点，运输 60 点；`sampleTicks` 表示每点跨度，60 tick 为 1 游戏秒。数量、J、hash 都是该采样桶的原始统计量，不是已归一化速率。产消和运输最后一点按面板规则由细粒度记录补齐，是不完整桶，不能拿它和完整桶直接比较。

电力和研究曲线沿用面板的完整槽读取。累计档历史按存档时长选择一个原生窗口，最多 100 小时，不是无限保存全局历史。列表默认只返回 256 点；需要完整曲线时显式 `points(limit:600)`，常规分析只取有关物品及所需点数。

原始入口仍可通过 `production`、`power`、`rawStatistics`、`data.statistics` 读取。`statistics` 根现在提供面板契约，旧调用若把它当原始对象，应改用 `rawStatistics` 或 `data.statistics`。
