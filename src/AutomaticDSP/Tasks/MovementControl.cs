using UnityEngine;

namespace AutomaticDSP.Tasks
{
    internal static class MovementControl
    {
        internal static string OverrideReason(Player player, bool navigation, OrderNode ownedOrder = null)
        {
            var controller = player.controller;
            var uiOpen = VFInput.inFullscreenGUI || VFInput.inputing || VFInput.inScreenshotMode || UIGame.viewMode >= EViewMode.Globe;
            // 界面按键不代表移动接管；显式订单和游戏视图中的人工输入优先。
            return !navigation && uiOpen ? "ui_open" :
                controller.cmd.type == ECommand.Build ? "build_command" :
                player.navigation.navigating ? "native_navigation" :
                player.currentOrder != null && !ReferenceEquals(player.currentOrder, ownedOrder) && !player.currentOrder.targetReached ? "player_order" :
                uiOpen ? null : controller.input0.sqrMagnitude > 0 || controller.input1.sqrMagnitude > 0 ? "movement_input" :
                VFInput._warpKey ? "warp_key" : VFInput._sailSpeedUp ? "speed_up_key" :
                VFInput.rtsStop.onDown ? "stop_key" : navigation && VFInput._sailLockCursor ? "cursor_lock_key" : null;
        }
    }
}
