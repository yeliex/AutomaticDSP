using AutomaticDSP.Tasks;

// 链接真实地形命令，隔离原生地形执行；不连接游戏，也不模拟 Unity 帧循环。
int checks = 0;
void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
    checks++;
    Console.WriteLine("通过：" + message);
}
Player Reset()
{
    GameMain.mainPlayer = new Player();
    GameMain.localPlanet = GameMain.mainPlayer.factory.planet;
    AutomationReformTool.ApplyEffect = null;
    AutomationReformTool.ThrowAfterApply = false;
    AutomationReformTool.Freed = false;
    return GameMain.mainPlayer;
}
CommandState Run(string mode)
{
    var command = new CommandState { Mode = mode };
    new TaskCommandExecutor().Run(command);
    return command;
}
var player = Reset();
Check(Run("restore").Status == "SUCCEEDED" && player.inhandItemId == 0 && player.package.Count == 11,
    "还原成功收起临时地基，返还物品进入背包");
Check(AutomationReformTool.Freed && player.HandSelections == 2, "释放独立工具并配对临时手持选择");
player = Reset();
player.package.Capacity = 10;
Run("restore");
Check(player.inhandItemId == 0 && player.package.Count == 10 && player.Dropped == 1, "满包返还走原生收起入口，不丢弃物品数量");
player = Reset();
AutomationReformTool.ThrowAfterApply = true;
try { Run("restore"); throw new Exception("应抛出异常"); }
catch (InvalidOperationException) { }
Check(player.inhandItemId == 0 && player.package.Count == 11 && AutomationReformTool.Freed, "原生执行中途异常仍收起退款并释放工具");
player = Reset();
AutomationReformTool.ApplyEffect = () => player.sandCount++;
Check(Run("restore").Error == "reform_failed" && player.inhandItemId == 0 && AutomationReformTool.Freed,
    "后置校验失败仍清理临时选择");
player = Reset();
player.inhandItemId = 1131;
player.inhandItemCount = 3;
player.controller.cmd = "玩家建造指令";
Run("restore");
Check(player.inhandItemId == 1131 && player.inhandItemCount == 4 && player.HandSelections == 0 &&
    player.controller.cmd == "玩家建造指令", "玩家原有地基手持与建造指令保持不变");
player = Reset();
player.controller.cmd = "玩家建造指令";
Run("restore");
Check(player.controller.cmd == "玩家建造指令", "空手建造模式也不被临时地基清理覆盖");
player = Reset();
Check(Run("flatten").Status == "SUCCEEDED" && player.HandSelections == 0 && player.package.Count == 9,
    "空手铺设不引入地基选择或建造状态");
player = Reset();
player.inhandItemId = 1131;
player.inhandItemCount = 3;
player.controller.cmd = "玩家建造指令";
Run("flatten");
Check(player.inhandItemId == 1131 && player.inhandItemCount == 2 && player.HandSelections == 0 &&
    player.controller.cmd == "玩家建造指令", "铺设保留玩家原有地基选择与建造模式");
player = Reset();
player.mecha.buildArea = 0;
Check(Run("restore").Error == "out_of_range" && player.HandSelections == 0 && AutomationReformTool.Freed,
    "范围校验失败不改变手持");
player = Reset();
player.sandCount = -1;
Check(Run("restore").Error == "insufficient_sand" && player.HandSelections == 0, "资源校验失败不改变手持");
player = Reset();
AutomationReformTool.ApplyEffect = () => player.inhandItemId = 1001;
Run("restore");
Check(player.inhandItemId == 1001 && player.HandSelections == 1, "手持已被其他调用替换时不清理新选择");
Console.WriteLine($"地形命令离线验证通过：{checks} 项。原生库存溢出与逐帧控制器行为仍需独立存档实机验收。");
