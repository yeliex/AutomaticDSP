using AutomaticDSP.Tasks;
using AutomaticDSP.Serialization;
using UnityEngine;

var executor = new TaskCommandExecutor();
void Check(bool value, string message) { if (!value) throw new Exception(message); }
CommandState Start()
{
    GameMain.mainPlayer = new(); GameMain.gameTick = 0;
    var c = new CommandState(); executor.Execute(c); return c;
}
var command = Start(); var player = GameMain.mainPlayer;
Check(player.issued == 1 && ReferenceEquals(player.currentOrder, command.NativeMoveOrder), "首次下达原生订单");
for (var i = 0; i < 5; i++) executor.Execute(command);
Check(command.Status == "RUNNING" && player.issued == 1, "暂停不累计停滞，也不重复下达");
GameMain.gameTick = 599; executor.Execute(command);
Check(command.Status == "RUNNING", "短暂受阻继续等待");
GameMain.gameTick = 600; executor.Execute(command);
Check(command.ErrorCode == "movement_stuck" && player.currentOrder == null, "持续受阻失败并清理订单");

command = Start(); player = GameMain.mainPlayer;
GameMain.gameTick = 590; player.position.x = 1; executor.Execute(command);
GameMain.gameTick = 600; executor.Execute(command);
Check(command.Status == "RUNNING", "真实接近目标重置停滞计时");
GameMain.gameTick = 1190; executor.Execute(command);
Check(command.ErrorCode == "movement_stuck", "推进后再次受阻仍会失败");

command = Start(); player = GameMain.mainPlayer;
var manual = OrderNode.MoveTo(new Vector3 { y = 100 }); player.currentOrder = manual;
GameMain.gameTick = 900; executor.Execute(command);
Check(command.Phase == "manualOverride" && ReferenceEquals(player.currentOrder, manual) &&
    (string)((JsonObject)command.Result)["overrideReason"] == "player_order", "人工订单优先且不会被清理");
player.ClearOrders(); GameMain.gameTick = 2000; executor.Execute(command);
Check(command.Status == "RUNNING" && command.Phase == "approaching" && player.issued == 2, "人工订单结束后恢复且不累计接管时间");
player.controller.input0.x = 1; executor.Execute(command);
Check(command.Phase == "manualOverride" && player.currentOrder == null, "人工移动撤销本任务订单");
player.controller.input0.x = 0; executor.Execute(command);
Check(player.issued == 3, "输入释放恢复移动");
VFInput.inFullscreenGUI = true; player.controller.input0.x = 1; executor.Execute(command);
Check(command.Phase == "approaching", "界面输入不误判为移动接管");
VFInput.inFullscreenGUI = false; player.controller.input0.x = 0;
VFInput.rtsStop.onDown = true; executor.Execute(command);
Check(command.Phase == "manualOverride", "停止键临时接管");
VFInput.rtsStop.onDown = false; executor.Execute(command);
player.currentOrder = manual; executor.StopCommandEffects(command);
Check(ReferenceEquals(player.currentOrder, manual), "取消保留人工订单");
player.ClearOrders(); executor.Execute(command);
player.position = command.Target; executor.Execute(command);
Check(command.Status == "SUCCEEDED" && player.currentOrder == null, "到达容差后成功并清理本任务订单");

command = Start(); player = GameMain.mainPlayer;
player.ClearOrders(); GameMain.gameTick = 300; executor.Execute(command);
Check(player.issued == 2 && command.MoveProgressTick == 0, "丢失订单自动恢复且不重置停滞计时");
GameMain.gameTick = 600; executor.Execute(command);
Check(command.ErrorCode == "movement_stuck", "重复丢失订单不能无限延长停滞");
command = Start(); GameMain.mainPlayer.planetId = 2; executor.Execute(command);
Check(command.ErrorCode == "planet_changed", "切换行星拒绝沿用局部坐标");
Check(MovementControl.OverrideReason(GameMain.mainPlayer, true) == null, "空闲导航不让出控制");
GameMain.mainPlayer.currentOrder = manual;
Check(MovementControl.OverrideReason(GameMain.mainPlayer, true) == "player_order", "导航与移动共享接管判定");
Console.WriteLine("通过：真实移动执行代码的受阻、暂停、进度、人工订单／输入／停止接管与恢复、到达、取消及行星切换；不模拟原生物理。");
