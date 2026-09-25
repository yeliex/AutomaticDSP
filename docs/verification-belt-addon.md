# 传送带附属建筑验证

2026-09-25，在 `AutomaticDSP-redblue-opening-20260925` 非沙盒存档验证喷涂机。

## 修复

- `placeBuilding` 对原型 `addonType == Belt` 使用原生 `BuildTool_Addon`，执行附近碰撞体激活、吸附、严格传送带匹配与原生建造校验。
- 创建预建后复用单建筑等待状态，避免后续 tick 重复创建预建。隔离测试曾暴露该问题，重复实体及临时传送带已通过原生拆除回收。
- 保留距离、物资、科技、碰撞及无人机落成约束。布局仍由外部 Agent 决定。

## 实测结果

- 喷涂机实体 153：一次请求只消耗一台设备，落成后任务成功；原生 `cargoBeltId=100` 对应主干煤带实体 136。
- 制造台 152 自动生产增产剂，经短分拣器与升高至喷涂机上层入口的供剂带送入；原生 `incBeltId=369`，缓冲由 225 回升至 601 个喷涂次数。
- 主电网供电正常；煤货物出现 `inc=1`，石墨熔炉 431、376、443 的 `incServed` 与额外产出进度增长，证明真实喷涂与增产生效。
- 错误堆叠目标（研究站 37）返回 `invalid_command`；在火电厂 128 上放置返回原生 `Collide`，均未生成实体。
- 原煤矿旁候选位置与矿脉冲突；移至主干带 136 后成功，未放宽碰撞规则。
- `dotnet build src/AutomaticDSP/AutomaticDSP.csproj --no-restore -v minimal`：0 警告、0 错误。

证据位于本地 `.tools/opening-validation/coater-positive.json`、`coater-negative-verification.json` 及任务历史。尚未单独验证流速监测器及喷涂机垂直堆叠，不据此宣称所有附属建筑场景已通过。
