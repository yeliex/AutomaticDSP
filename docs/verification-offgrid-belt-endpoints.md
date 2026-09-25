# 2026-09-25 非网格传送带端点验证

问题：`PlanetAuxData.SnapLineNonAlloc` 会吸附路径端点。原生 `BuildTool_Path` 随后会恢复真实起终点；接口先前遗漏此步骤，已有带段偏离网格时，首尾吸附点可能绕过 0.1m 重合去重，在已有带段旁产生碰撞预览。

修复：生成中间路径后，按原生行为恢复 `snaps[0] = start` 和 `snaps[count - 1] = end`。既有带段的重复端点仍由原有逻辑移除，保留原生碰撞与连接检查。

验证：
- 编译 0 警告、0 错误。
- 游戏内显式创建两段非网格位置的带段，再分别测试 `start.entityId → end.position` 与双端 `entityId` 接续，均成功。
- 10 个带段的 `segPathId` 相同，近零距离重叠检测为零；全部测试带段已原生拆除回收。
- 脚本 `.tools/opening-validation/verify-offgrid-endpoints.mjs`；证据 `.tools/opening-validation/offgrid-endpoint-verification.json`。
