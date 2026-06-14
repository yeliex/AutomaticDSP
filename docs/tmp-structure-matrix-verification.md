# 结构矩阵验证临时记录

> 临时工作记录，不作为正式 API 约定。用于集中记录结构矩阵目标测试中的状态、失败点和后续修复项。

## 当前目标

- 以实现结构矩阵科技为长期目标，尽量用现有任务队列能力完成行星内基础生产线。
- Mod 不暴露矿机/传送带规划接口；位置计算放在外部 Agent、测试脚本或后续 skill 文档中。
- 状态查询尽量与游戏对象对齐。需要端口/插槽姿态时，优先查询原始对象字段，例如 `factory.entityPool.pos/rot/protoId` 与 `items.prefabDesc.portPoses/slotPoses`。

## 已确认

- 使用 Steam 启动游戏：`steam://rungameid/1366540`。
- `factoryDetails` 不应包含 `ports` / `slots` 这类规划派生字段。
- 通用查询层需要支持 `Quaternion` / `Pose`，用于读取原始游戏字段。
- 采矿机建造需要：
  - 外部按游戏规则计算 `position` 和 `rotation`。
  - `placeBuilding` 提供 `veinId` 以表达原生“点击矿脉后放置采矿机”的上下文。
  - 执行器在校验前激活矿脉附近 collider，否则普通采矿机会 `NeedResource`。

## 当前测试档

- 存档名：`AutomaticDSP-structure-matrix-test`
- 当前已建：
  - 风力涡轮机。
  - 电力感应塔。
  - 铁矿采矿机，覆盖 6 个铁矿点。
  - 铁块产线：采矿机 -> 传送带 -> 分拣器 -> 电弧熔炉。
- 当前铁矿机结果：
  - 成功实体：`entityId = 3`
  - `veinCount = 6`
  - `totalVeinAmount` 约 38863
- 当前铁块产线结果：
  - 熔炉实体：`entityId = 4`，配方 `recipeId = 1`。
  - 传送带实体：`entityId = 5, 6`。
  - 分拣器实体：`entityId = 7`，连接 `pickTarget = 6` 到 `insertTarget = 4`。
  - `factory.powerSystem.consumerPool` 中矿机、熔炉、分拣器均在 `networkId = 1`。
  - 熔炉已开始生产铁块，`assembler.produced` 曾观察到非零。

## 已发现问题

- `placeBelt` 从采矿机实体端口接出时，曾返回：
  - `Collide`
  - `TooSteep`
- 怀疑原因：
  - 执行器手工构造 `BuildTool_Path` preview 时，只设置了 preview 的 `inputObjId`，但没有完整同步原生路径工具上下文。
  - 已尝试修正：`tool.startObjectId` / `tool.castObjectId` / `tool.castObject` / `tool.castObjectPos` / `tool.startTarget`。
  - 修正后，从采矿机 `slot=0` 到短距离地面点仍返回 `TooSteep`，说明问题更可能在手写 `SnapLine` 未复刻原生建筑端口到传送带的过渡路径。
  - 不采用 `BuildTool_Path.DeterminePreviews()` 作为主方案，避免把命令执行变成鼠标状态机模拟。
  - 保持手工 preview，但实体起点的 `startTarget` / `cmd.test` 使用 `GetObjectPose(startObjectId).position`，preview 的第一点仍使用解析出的端口位置。
  - `SnapLineNonAlloc` 的 `begin_flat` 不能对建筑端点硬编码为 true；建筑端点出带时改为 `beginFlat = false`，纯地面点连线仍为 true。
  - 直线、直角、出带点和转弯点选择属于外部 Agent / 测试脚本策略，Mod 不自动插入路径点，也不限制路径风格。
  - `placeBelt` 需要允许 `points` 与可选 `start` / `end` 实体连接同时存在。`points` 表达外部计算好的路径，`start` / `end` 只表达游戏对象端口连接。
  - `points` 是调用方给定的精确路径点，Mod 不再对它二次调用 `SnapLineNonAlloc` 插值或重算路径；需要补中间点时由外部 Agent / 测试脚本负责。
- 建筑布局经验：
  - 建筑应先按游戏经纬度/网格吸附规则放置，再根据已落成建筑计算传送带与分拣器。
  - 传送带应接近建筑端口附近点，不应靠长分拣器弥补路线偏差。
  - 需要供电覆盖时可以新建电塔，不应为了复用已有电塔把熔炉等建筑强行挤在一起。
  - 电力状态由外部 Agent 查询 `factory.powerSystem.consumerPool.networkId` 判断；Mod 不在 `factoryDetails` 中主动派生供电结论。
  - 外部 Agent 为了幂等可查询 `factory.factorySystem.inserterPool`，避免重复下发相同或重叠的分拣器命令；Mod 不用 `pickTarget + insertTarget` 增加比游戏更严格的限制。
- 已修复：
  - `factoryDetails` 不再用 `entity.powerNodeId == 0` 判断 `missingPower`。
  - `placeSorter` preview 启用近场 collider 区域，继续依赖游戏原生建造校验。

## 待继续验证

- 继续建立下一条基础产线时验证：
  - 如果建筑不在现有电网覆盖内，优先放置新电塔补覆盖。
  - 用同一流程建立铜块、磁铁、电路板、磁线圈、电磁矩阵等结构矩阵前置产线。
  - 对新的分拣器连接不要使用 slot 扫描；应通过原始 `slotPoses` / 建筑姿态或外部策略一次性选定连接。

## 后续生产线目标

- 铁矿 -> 铁块。
- 铜矿 -> 铜块。
- 铁块 + 铜块 -> 电路板。
- 磁铁 + 铜块 -> 磁线圈。
- 磁线圈 + 电路板 -> 电磁矩阵。
- 用电磁矩阵推进结构矩阵链路前置科技。
