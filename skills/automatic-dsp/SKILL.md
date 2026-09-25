---
name: automatic-dsp
description: 通过 AutomaticDSP Mod 查询和操作《戴森球计划》，根据当前科技、产量、资源、电力和物流推进研究、规划建设产线、诊断瓶颈或完成戴森球目标。
---

# AutomaticDSP

Mod 提供游戏原生观测和操作，Agent 负责选址、产能计算、布局、科技路线与资源调度。

布局默认以满足吞吐和原生建造规则为前提，减少占地、传送带长度与输电塔数量，尽量缩短分拣器往返距离。发电设施按电力需求配置，用输电塔覆盖生产区；结合维护和供电隔离需求决定电网合并或分区。具体方法见 [产线建设与运行](references/guides/production.md)。

## 理解目标并决定下一步

目标可包含期望结果、规模或速度、范围、时间要求、改建范围、资源偏好和完成依据。结合当前存档补齐会影响方案的信息。

根据当前科技、产量、库存、电力、物流和资源选择下一步。目标所需科技未完成时，优先缩短相关科技的解锁时间；具备所需能力后，围绕实际瓶颈推进目标规模。具体取舍见 [规划提示](references/guides/goal-planning.md)，矩阵俗称见 [名词映射](references/guides/goal-planning.md#矩阵名词映射)。

用游戏状态验证操作效果，依据新证据调整计划。长任务保留目标、关键决定和恢复所需信息，恢复时重新核对存档与现场。

尽量并行推进：制造和科研优先先入原生队列，再利用等待时间移动、采集或建设；只有真实材料/科技依赖才阻塞。开局手搓按首批可用物资小批安排，避免大批前置材料拖延第一件成品。接口即时通道与等待语义见游戏控制，取舍见机甲指南。

调度自动分为科研、手搓、机甲指令、建造通道；垃圾等即时操作无需排队。预建下达后可继续移动，建造命令仍等实际落成才成功。跨通道依赖使用 `dependsOn` 或状态条件，实体引用自动等待落成；不能把命令在数组中的前后位置当作制造或科研完成保证。

每次状态响应都包含 `notifications.notices` 和当前游戏目标。LLM 应读取、理解并及时用 `dismissNotice` 确认已处理的消息，同时关闭仍可见的提示；不确认就会在之后每次状态响应继续返回，读取本身不算确认。海岸建设先采样占地地形；建造命令结束会自动退出其进入的建造模式。

## 按需读取参考

需要安装 Mod、配置服务或确认连接地址时，读取 [Mod 使用说明](references/mod-usage.md)。

需要选择存档、新建对局或完成落地准备时，读取 [游戏初始化](references/guides/initialization.md)。已有对局时，从当前状态和目标开始。

接口按需读取：创建、加载、保存对局见 [游戏管理](references/interface/game.md)；世界状态和原型查询见 [游戏状态](references/interface/game-state.md)；任务提交、查询、取消、历史及操作参数见 [游戏控制](references/interface/game-control.md)。

查询燃料热值、设备功率、完整物品与配方目录，或规划输送端口与垂直堆叠时，读取 [原型属性与连接契约](references/interface/prototypes-and-connections.md)，按实际原型和实体数据选择参数；堆叠能力以 `prefabDesc.multiLevel` 等属性判断，不按建筑名称推断。

| 当前需要 | 玩法指南 |
| --- | --- |
| 选择下一步、科技推进、矩阵名称与研究站 | [目标与科研](references/guides/goal-planning.md) |
| 新开局与第一条基础产线 | [开局经验](references/guides/opening.md) |
| 机甲能源、移动、人工采集与手搓 | [机甲](references/guides/mecha.md) |
| 首批钛硅、资源探测、跨星球运输与后期选址 | [外星资源与扩张](references/guides/interplanetary.md) |
| 产线建设、运输、仓储、供电与运行诊断 | [产线建设与运行](references/guides/production.md) |
| 产能配比、机器取整、增产剂与计算脚本 | [产线需求计算](references/guides/production-calculation.md) |
| 戴森球、光子与蓝图建设 | [戴森球](references/guides/dyson-sphere.md) |

遵守游戏的科技、材料、距离、碰撞、地形、端口与无人机建造规则。玩法按当前游戏数据和用户目标取舍，接口契约及限制集中在 `references/interface/`。日常执行按需读取本地参考；遇到资料缺失或版本差异时再补充查证。
