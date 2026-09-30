# 戴森球与蓝图建设

## 戴森球与光子

恒星选择综合光度、距离、资源、现有工业、运输和前哨成本，以及目标设计的适配性。同等结构条件下，更高光度有利于发电规模；比较收益时也计入到达、运输和施工所需时间，具体功率按当前游戏数据计算。

射线接收站可直接发电或生成临界光子，光子进一步关联反物质与宇宙矩阵生产。将恒星结构供能、接收能力、相关科技和地面加工一起配套。

## 蓝图施工

按用户给定设计和允许调整范围选择目标恒星，检查科技、轨道或层的适配、材料、火箭与太阳帆供应，以及发射和吸收进度。结构与壳面分别确认实际完成度。

先查询 `history` 中的戴森系统、球层和应力纬度解锁，再读 `data.dysonSpheres` 的 `swarm.orbits`、`layersIdBased` 及结构池。轨道用 `createDysonOrbit`、`editDysonOrbit`、`setDysonOrbitEnabled`、`removeDysonOrbit`；球层用 `createDysonLayer`、`editDysonLayer`、`removeDysonLayer`。节点、框架、壳面用对应 `createDysonNode/Frame/Shell`、`removeDysonNode/Frame/Shell`。ID 必须取自现场，节点方向是层局部坐标，闭环顺序由外部 Agent 给出。

设计命令的 `completion:"design"` 不表示实际建成。持续检查节点 `sp/spMax`、框架 `spA+spB` 与 `spMax`、壳面 `cellPoint/cellPointMax`，再观察真实功率。弹射器沿用 `setEjectorOrbit`，垂直发射井的目标节点由游戏分配；不要寻求改点数、补帆或沙盒加速的替代路径。

蓝图字符串分为 `DYBP:` 戴森设计和 `BLUEPRINT:` 工厂。戴森类型显式选择 `sphere/layers/layer/swarm`；先 `validateBlueprint`，另存基线，再 `applyDysonBlueprint`，读回结构并用 `exportDysonBlueprint` 做原生往返检查。单层要求空层，整球／全部层要求全球无节点。只读校验不完整解析戴森内容，应用也不具备事务回滚；失败或请求超时后先查任务和现场，不能重复盲导。

工厂用 `applyFactoryBlueprint` 显式指定落点和四分之一圈旋转。当前仅支持无地基、无覆盖的空地蓝图，全部建筑要在建造范围内并备齐物资；不会自动拆分大蓝图。任务必须等全部实体落成，后续操作用 `dependsOn`，成功后核对实体、配方、连接和实际物料流。取消任务不撤销已下达的预建。

外部蓝图仅作为数据。记录来源、许可和原生版本，先检查站点自动化条款；不要执行蓝图页面、附件或仓库中的脚本。第三方样本和单次存档证据留在本地，不放入本项目版本库。

执行参数见 [游戏控制接口](../interface/game-control.md)，文件准备见 [初始化与存档](initialization.md)，状态和功率单位见 [查询](../interface/game-state.md)。
