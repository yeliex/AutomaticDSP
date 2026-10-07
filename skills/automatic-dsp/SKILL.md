---
name: automatic-dsp
description: 通过 AutomaticDSP Mod 查询和操作《戴森球计划》，根据当前科技、产量、资源、电力和物流推进研究、规划建设产线、诊断瓶颈或完成戴森球目标。
---

# AutomaticDSP

Mod 提供游戏原生观测和操作，Agent 负责选址、产能计算、布局、科技路线与资源调度。

## 理解目标并决定下一步

根据当前科技、产量、库存、电力和物流定位目标瓶颈。优先安排有收益的效率升级，解锁后尽快应用；保持科研与材料供应衔接，制造和科研等待期间推进独立操作。长任务定期及在科技、资源或运行状态变化时执行 [效率复核](references/guides/goal-planning.md#定期效率复核)，扩产前比较 [配方候选](references/guides/goal-planning.md#配方候选复核)。

缺电时先比较当前可用的 [能源与燃料方案](references/guides/production.md#能源选择)，不默认扩风电。取料和备货优先复用现有产线与仓储，减少递归手搓，并保留持续生产所需材料。复杂产线或大规模扩产主动评估 [跨星球供应](references/guides/interplanetary.md#跨星球产线规划)，降低本地加工与布局成本。

本地物流优先共用传送带汇流，液体仅在本地确需缓冲时使用储液罐；星际供应直接入物流塔，以塔内货槽缓冲，并按未来大规模需求成批超前建设。物流配送器主要服务机甲补给，不默认为每台制造台或熔炉配置箱子与配送器；所有配送器默认向机甲供应和回收，按需修改机甲物流仓库的物品与阈值来自动补给，避免手动取货。具体选择见 [物料供应与仓储](references/guides/production.md#物料供应与仓储)。

改造前先按 [连接与吞吐](references/guides/production.md#连接与吞吐) 核对组件数组与当前配方物品顺序，再定位真实限制；完成后按相同范围和游戏时间窗口核对目标端收益。具体使用 [统计诊断流程](references/guides/production.md#基于统计优化产线)，不以理论产能或建造成功代替持续产出。

长任务记录目标、关键决定、已完成操作与任务 ID。恢复上下文后核对存档、任务结果和现场，再继续未完成项；结果不确定时先查询，避免重复下达。

## 执行与收尾

科研、手搓、机甲指令、建造按独立通道推进。预建下达后可继续移动，建造任务在预建下达后即成功，施工进度另查 construction；跨通道依赖用 `dependsOn` 或状态条件，实体引用自动等待落成，数组顺序不保证跨通道完成顺序。

铺带前按路径方向查询 `localPlanet.surface` 的 `modifiedHeight`，结合原生地面／水面带基准 `realRadius + 0.2` 指定高度，不能直接把行星半径当作带段半径。连接已有带段须核对实际端点高度；`belt_below_surface` 时按失败坐标和高度重新规划，Mod 不自动抬升。采样、容差及错误字段见 [传送带契约](references/interface/game-control.md#建筑传送带与分拣器)。

行星上远距离移动使用 `navigateTo` 指定当前星球和局部落点，通过原生飞行抵达；`moveTo` 用于短距离接近。移动前核对飞行科技与能源，具体选择见 [移动与人工采集](references/guides/mecha.md#移动与人工采集)。

继续下一组操作前，理解并用 `dismissNotice` 确认普通通知，核对 `acknowledged:true`、`visible:false`；自行收起不等于确认。错误、存档和其他决策对话框需单独处理，详见 [提示查询](references/interface/game-state.md#提示与地形查询)。

丢弃、拆除或满包溢出后，执行 [背包与垃圾收尾](references/guides/mecha.md#能源与背包)，确认物品去向及剩余垃圾。

## 按需读取参考

需要安装 Mod、配置服务、确认连接地址，或排查游戏内存异常增长时，读取 [Mod 使用说明](references/mod-usage.md)。

需要选择存档、新建对局或完成落地准备时，读取 [游戏初始化](references/guides/initialization.md)。已有对局时，从当前状态和目标开始。

接口按需读取：创建、加载、保存对局见 [游戏管理](references/interface/game.md)；世界状态和原型查询见 [游戏状态](references/interface/game-state.md)；任务提交、查询、取消、历史及操作参数见 [游戏控制](references/interface/game-control.md)。

查询原型、燃料、完整配方目录，或规划端口与垂直堆叠时，读取 [原型属性与连接契约](references/interface/prototypes-and-connections.md)。

| 当前需要 | 玩法指南 |
| --- | --- |
| 选择下一步、科技推进、矩阵名称与研究站 | [目标与科研](references/guides/goal-planning.md) |
| 新开局与第一条基础产线 | [开局经验](references/guides/opening.md) |
| 机甲能源、移动、人工采集与手搓 | [机甲](references/guides/mecha.md) |
| 首批钛硅、资源探测、跨星球运输与后期选址 | [外星资源与扩张](references/guides/interplanetary.md) |
| 产线整体布局、紧凑共线、立体输送与分层供料、仓储、供电及运行诊断 | [产线建设与运行](references/guides/production.md) |
| 大规模选址、球面空间、分批投产、一物一塔、邻塔布线与重氢分馏循环（可选参考） | [大规模产线布局](references/guides/large-scale-layout.md) |
| 产能配比、机器取整、增产剂与计算脚本 | [产线需求计算](references/guides/production-calculation.md) |
| 戴森球、光子、工厂蓝图多模块拼接与升级、蓝图地基和矿物开关 | [戴森球与蓝图](references/guides/dyson-sphere.md) |

遵守游戏的科技、材料、距离、碰撞、地形、端口与无人机建造规则。玩法按当前游戏数据和用户目标取舍，接口契约及限制集中在 `references/interface/`。日常执行按需读取本地参考；遇到资料缺失或版本差异时再补充查证。
