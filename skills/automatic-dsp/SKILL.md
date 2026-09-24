---
name: automatic-dsp
description: 通过 AutomaticDSP Mod 查询和操作《戴森球计划》，根据当前科技、产量、资源、电力和物流推进研究、规划建设产线、诊断瓶颈或完成戴森球目标。
---

# AutomaticDSP

Mod 提供游戏原生观测和操作，Agent 负责选址、产能计算、布局、科技路线与资源调度。

## 理解目标并决定下一步

目标可包含期望结果、规模或速度、范围、时间要求、改建范围、资源偏好和完成依据。结合当前存档补齐会影响方案的信息。

根据当前科技、产量、库存、电力、物流和资源选择下一步。目标所需科技未完成时，优先缩短相关科技的解锁时间；具备所需能力后，围绕实际瓶颈推进目标规模。具体取舍见 [规划提示](references/guides/goal-planning.md)，矩阵俗称见 [名词映射](references/guides/goal-planning.md#矩阵名词映射)。

用游戏状态验证操作效果，依据新证据调整计划。长任务保留目标、关键决定和恢复所需信息，恢复时重新核对存档与现场。

## 按需读取参考

需要安装 Mod、配置服务或确认连接地址时，读取 [Mod 使用说明](references/mod-usage.md)。

需要选择存档、新建对局或完成落地准备时，读取 [游戏初始化](references/guides/initialization.md)。已有对局时，从当前状态和目标开始。

接口按需读取：创建、加载、保存对局见 [游戏管理](references/interface/game.md)；世界状态和原型查询见 [游戏状态](references/interface/game-state.md)；任务提交、查询、取消、历史及操作参数见 [游戏控制](references/interface/game-control.md)。

| 当前需要 | 玩法指南 |
| --- | --- |
| 选择下一步、科技推进、矩阵名称与研究站 | [目标与科研](references/guides/goal-planning.md) |
| 新开局与第一条基础产线 | [开局经验](references/guides/opening.md) |
| 机甲能源、移动、人工采集与手搓 | [机甲](references/guides/mecha.md) |
| 产线建设、运输、仓储、供电与运行诊断 | [产线建设与运行](references/guides/production.md) |
| 产能配比、机器取整、增产剂与计算脚本 | [产线需求计算](references/guides/production-calculation.md) |
| 戴森球、光子与蓝图建设 | [戴森球](references/guides/dyson-sphere.md) |

遵守游戏的科技、材料、距离、碰撞、地形、端口与无人机建造规则。玩法按当前游戏数据和用户目标取舍，接口契约及限制集中在 `references/interface/`。日常执行按需读取本地参考；遇到资料缺失或版本差异时再补充查证。
