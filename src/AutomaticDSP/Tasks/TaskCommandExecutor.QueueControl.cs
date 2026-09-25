using System;
using AutomaticDSP.Serialization;
using AutomaticDSP.UI;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        public static bool CanExecuteImmediately(CommandState command)
        {
            switch (command.NormalizedType)
            {
                case "craftinventory": return !GetBool(command, "waitForCompletion", false);
                case "researchtech": return !GetBool(command, "waitForUnlock", false);
                case "discardinventoryitem":
                case "pickuptrash":
                case "cleartrash":
                case "removeforgetask":
                case "removetechinqueue":
                case "dismissnotice": return true;
                default: return false;
            }
        }

        private void ExecuteRemoveForgeTask(CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var code, out var message))
            {
                finishCommand(command, CommandFailed, code, message, now, null);
                return;
            }
            var forge = player.mecha?.forge;
            if (forge == null || !TryGetInt(command, "index", out var index) || index < 0 || index >= forge.tasks.Count ||
                !TryGetInt(command, "recipeId", out var recipeId) || forge.tasks[index].recipeId != recipeId)
            {
                finishCommand(command, CommandFailed, "queue_changed", "Refresh forge.tasks and provide its current index and recipeId.", now, null);
                return;
            }
            // 原生取消会处理父任务、递归材料和退款，不能直接删除列表元素。
            forge.CancelTask(index);
            finishCommand(command, CommandSucceeded, null, null, now, new JsonObject
            {
                ["index"] = index, ["recipeId"] = recipeId, ["remainingTaskCount"] = forge.tasks.Count
            });
        }

        private void ExecuteCancelPrebuild(CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var code, out var message))
            {
                finishCommand(command, CommandFailed, code, message, now, null);
                return;
            }
            if (!TryValidateCurrentPlanet(command, player, out code, out message))
            {
                finishCommand(command, CommandFailed, code, message, now, null);
                return;
            }
            var factory = GameMain.localPlanet?.factory;
            if (factory == null || !TryGetInt(command, "prebuildId", out var id) || id <= 0 ||
                id >= factory.prebuildPool.Length || factory.prebuildPool[id].id != id)
            {
                finishCommand(command, CommandFailed, "target_not_found", "Prebuild no longer exists; refresh prebuildPool.", now, null);
                return;
            }
            var pos = factory.prebuildPool[id].pos;
            if (!IsWithinCommandIssueRange(player, pos, out var distance, out var range) ||
                Vector3.Distance(player.position, pos) > (player.mecha?.buildArea ?? 0))
            {
                finishCommand(command, CommandFailed, "out_of_range", "Move within build range before cancelling a prebuild.", now, RangeFailureResult(distance, range, pos));
                return;
            }
            var removed = player.controller?.actionBuild?.DoDismantleObject(-id) == true;
            finishCommand(command, removed ? CommandSucceeded : CommandFailed, removed ? null : "dismantle_failed",
                removed ? null : "Native prebuild cancellation failed.", now, new JsonObject { ["prebuildId"] = id });
        }

        private void ExecuteDismissNotice(CommandState command, DateTimeOffset now)
        {
            if (!TryGetInt(command, "noticeId", out var id) || !GameNoticeService.Dismiss(id))
            {
                finishCommand(command, CommandFailed, "notice_not_found", "Notice was not found in the current session; refresh notifications.notices.", now, null);
                return;
            }
            finishCommand(command, CommandSucceeded, null, null, now, new JsonObject { ["noticeId"] = id });
        }
    }
}
