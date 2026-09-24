# Agent skill 资料整理记录

本页供项目维护者追溯资料。玩法要点整理在 `skills/automatic-dsp/references/`，Agent 使用 skill 时无需读取本页。

资料检索日期：2026-09-24。玩法经验经归纳编写，数值和适用条件以当前游戏数据为准。

## 外部资料

| 来源 | 本 skill 使用范围 | 核验边界 |
| --- | --- | --- |
| [科学矩阵](https://wiki.biligame.com/dsp/科学矩阵) | 六种科学矩阵的游戏名称 | 本轮核对检索内容；俗称按矩阵颜色映射 |
| [快速入门](https://wiki.biligame.com/dsp/快速入门) | 初始材料回收、机甲燃料、手搓与基础生产概念 | 已通过侧边浏览器读取正文；页面显示 2024-11-02 更新，部分章节仍在施工 |
| [萌新向流程攻略](https://wiki.biligame.com/dsp/萌新向流程攻略) | 开局自动化、燃料链、精炼副产物、跨行星资源和物流准备 | 已通过侧边浏览器读取正文；页面显示 2026-01-15 更新，图片配方及末尾比例表未作为数值依据 |
| [矩阵研究站](https://wiki.biligame.com/dsp/研究站) | 区分矩阵生产和科研消耗 | 读取检索返回内容；页面显示 2026-02-06 更新 |
| [增产剂 Mk.II](https://wiki.biligame.com/dsp/增产剂_Mk.II) | 额外产出、加速、耗电和配方适用性 | 读取检索返回内容；页面显示 2026-01-27 更新，具体倍率从当前游戏确认 |
| [喷涂机](https://wiki.biligame.com/dsp/喷涂机) | 各级基础倍率、喷涂次数、自喷及特殊物品效果 | 核对检索返回内容；使用时以当前游戏为准 |
| [增产策略选取](https://www.bilibili.com/opus/717931344520282135/) | 比较当前工序与上游成本，计入增产剂及电力反馈 | 浏览器读取正文，作者标注 2022-10-19；使用方法论，旧版最优方案按当前条件重算 |
| [量化计算器思路（一）](https://www.bilibili.com/opus/722169261748912147/) | 共用副产物、循环净流量、外部供应及联合配平 | 浏览器读取正文，作者标注 2023-04-17；脚本为本项目独立实现 |
| [量化计算器思路（二），合集第三篇](https://www.bilibili.com/read/readlist/rl630834) | 建筑向上取整 | 已读取合集摘要；文章导航未成功打开，未据此声称已核验后续算法 |
| [射线接收](https://wiki.biligame.com/dsp/射线接收) | 直接发电、光子与反物质链的关联 | 已读取页面；页面显示 2025-04-20 更新 |
| [戴森云和戴森球](https://wiki.biligame.com/dsp/戴森云和戴森球) | 恒星光度影响选址的提示 | 正文抓取超时，仅使用搜索摘要；未据此写入详细数值或几何公式 |
| [工厂蓝图说明](https://www.dysonsphereblueprints.com/help) | 工厂蓝图类别及游戏内应用方式 | 已读取站点说明 |
| [戴森球蓝图说明](https://www.dysonsphereblueprints.com/help?for=dyson_sphere) | 戴森球编辑界面的代码复制与粘贴 | 已读取站点说明 |
| [机甲蓝图说明](https://www.dysonsphereblueprints.com/zh-CN/help?for=mecha) | Customize/Mecha 路径线索 | 搜索摘要；实际目录需在游戏主机核对 |

## 本项目事实与推导

接口说明已核对当前仓库实现；本轮验证范围为源码与文档一致性。

默认目录与物品、配方、建筑信息接口计划来自维护者。

开局经验归纳自项目 `docs/ai-agent-getting-started.md` 和 `docs/examples/opening-to-iron-line.md`，使用时读取 skill 内的 `guides/opening.md` 即可。后者记录了种子 `23397062-64-A10`、和平模式、跳过序幕的实机闭环；这是既有文档记载，本轮未重新游戏实测。固定坐标、对象 ID 和采矿数量未提升为通用流程。

产线公式和自喷收支是明确假设下的算术推导；从瓶颈、准备时间和资源机会成本比较方案，属于 Agent 的规划方法。计算脚本用教学配方验证取整、共用上游、增产、自喷与回流，尚未与运行中的游戏逐项对照。

## 内容落点

| 资料主题 | Skill 内文档 |
| --- | --- |
| 快速入门、开局自动化与初期科技 | [开局经验](../skills/automatic-dsp/references/guides/opening.md) |
| 初期燃料、手搓与人工采集 | [机甲操作](../skills/automatic-dsp/references/guides/mecha.md) |
| 运输分配、供电与燃料链恢复 | [产线操作](../skills/automatic-dsp/references/guides/production.md) |
| 矩阵名称与研究 | [研究](../skills/automatic-dsp/references/guides/goal-planning.md) |
| 增产剂与量化方法 | [产线计算](../skills/automatic-dsp/references/guides/production-calculation.md) |
| 戴森球与射线接收 | [戴森球](../skills/automatic-dsp/references/guides/dyson-sphere.md) |
| 存档目录与蓝图文件 | [初始化](../skills/automatic-dsp/references/guides/initialization.md) |

两篇入门攻略的正文已通过侧边浏览器读取。技能内容提炼其适用机制与规划取舍，未固化攻略的种子、资源倍率、固定科研顺序和生产规模；图片配方及作者注明未完全核查的比例表未照搬。科技前置的资源与物流准备补充在 `guides/goal-planning.md`，机甲补给与连接方式分别归入对应操作页。
