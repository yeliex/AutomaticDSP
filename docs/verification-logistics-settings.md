# 基础物流设置验证

2026-09-25，存档 `AutomaticDSP-redblue-opening-20260925` 实机验证。

- `setStorageLimit`：储物仓 169 共 30 格；设为 0 后 bans=30，已有库存未减少；超过 size 被拒绝；恢复 30 后 bans=0。
- `setSorterFilter`：分拣器 277 设置蓝糖 6001 后，filter 与实体图标同步为 6001；清除与恢复为 0 成功；不存在的物品被拒绝。
- `setSplitterPriority`：临时分流器 119，两个输入、两个输出，共接 16 段临时传送带。依次选中输入端口 0、1 和输出端口 2、3，逐次核对 input0/output0 与该端口 beltId 一致。输入、输出优先级可以同时存在，分别取消后都为 false。
- 空端口返回 invalid_connection，非法端口 4 返回 invalid_command。
- 初次使用起终点自动吸附铺设测试线路时原生返回 TooBend；读取端口姿态后由测试脚本显式给出直线点列，原生校验通过，没有修改碰撞或弯曲限制。
- 测试分流器及 16 段传送带已拆除回收，现有箱子与分拣器恢复原配置。

编译 0 警告、0 错误，skill 检查通过。覆盖的是配置写入、读取及连接状态，未声称已覆盖混料、满载竞争时的吞吐分配，或带有非零输出过滤器时的所有分流器行为。

原始结果保存在本地 `.tools/opening-validation/logistics-settings-verification.json`、`splitter-settings-verification.json`。
