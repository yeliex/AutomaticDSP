# 传送带反转验证

2026-09-25，在 `AutomaticDSP-redblue-opening-20260925` 使用实际游戏接口验证 `reverseBelt`。

- 复用原生 `UIBeltWindow.OnReverseButtonClick`，临时绑定上下文并在 finally 恢复，不打开窗口。
- 原生按钮限定运输路径包含 2–1023 个带段，命令保留这一限制。
- 非传送带目标返回 `invalid_command`，不存在的实体返回 `target_not_found`。
- 蓝糖外侧路线包含 16 个带段，逐段验证反转后的 `outputId` 等于原 `mainInputId`，反之亦然。
- 再次反转后，16 个带段连接全部恢复；分拣器的取放目标和偏移也恢复一致。
- 首次数量比较受到持续生产干扰（220→221）。隔离源分拣器后重测，统计传送带货物、箱子、背包和分拣器中的蓝糖，前后均为 229；测试结束重新建成源分拣器。
- 校验脚本不比较分拣器瞬时持货状态，因为物品正常移动不代表连接变化。

本地原始结果：`.tools/opening-validation/reverse-belt-isolated-verification.json`。编译通过，0 警告、0 错误；skill 格式检查通过。复杂分支、运输站和 1024 带段边界未实机覆盖，按原生行为执行，不能据此声称所有拓扑均已验证。
