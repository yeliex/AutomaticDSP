using System;
using System.Reflection;
using AutomaticDSP.Serialization;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecuteSandboxUnlockTechs(CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var code, out var message))
            {
                finishCommand(command, CommandFailed, code, message, now, null);
                return;
            }
            if (GameMain.data.gameDesc == null || !GameMain.data.gameDesc.isSandboxMode || !GameMain.sandboxToolsEnabled)
            {
                finishCommand(command, CommandFailed, "sandbox_required", "仅在沙盒存档且沙盒工具已启用时允许一键解锁科技。", now, null);
                return;
            }
            var tree = UIRoot.instance?.uiGame?.techTree;
            var unlock = typeof(UITechTree).GetMethod("Do1KeyUnlock", BindingFlags.Instance | BindingFlags.NonPublic);
            if (tree == null || unlock == null)
            {
                finishCommand(command, CommandFailed, "native_api_unavailable", "原生沙盒一键解锁不可用。", now, null);
                return;
            }
            // 复用原生按钮的确认后动作，保留科技排除项、等级上限和研究队列处理。
            unlock.Invoke(tree, null);
            finishCommand(command, CommandSucceeded, null, null, now, new JsonObject
            {
                ["action"] = "sandboxUnlockTechs",
                ["blueprintLimit"] = GameMain.history.blueprintLimit,
                ["bpReformLimit"] = GameMain.history.bpReformLimit
            });
        }
    }
}
