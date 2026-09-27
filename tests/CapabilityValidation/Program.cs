using System;
using System.Collections.Generic;
using System.Linq;
using AutomaticDSP.Serialization;
using AutomaticDSP.State;
using AutomaticDSP.Tasks;
using AutomaticDSP.UI;

static class Program
{
    static int passed;
    internal static void Check(bool value, string message)
    { if (!value) throw new Exception(message); passed++; Console.WriteLine("通过：" + message); }
    static List<JsonObject> Pending() => ((List<object>)GameNoticeService.CapturePending()["notices"]).Cast<JsonObject>().ToList();
    static void Poll() { UnityEngine.Time.realtimeSinceStartup++; GameNoticeService.Update(); }
    static void Main()
    {
        GameStateQueryService.ValidateResources();
        GameMain.data = new();
        var window = UIRoot.instance.uiGame.researchResultTip;
        window.Show(1001, "非空科研正文"); Poll();
        var first = Pending().Single(); var id = (int)first["id"];
        GameNoticeService.CapturePending(); Poll();
        Check((int)Pending().Single()["id"] == id, "重复读取不确认提示，不重复创建身份");
        window.conclusionText.text = ""; Poll();
        Check(((string)Pending().Single()["text"]).Contains("非空科研正文"), "淡出空文本不丢失已读正文");
        window.active = false; Poll();
        Check(!(bool)Pending().Single()["visible"], "原生隐藏后仍保留未确认记录");
        window.Show(1001, "同一提示再次出现"); Poll();
        var second = Pending().Single(n => (int)n["id"] != id); var secondId = (int)second["id"];
        Check(GameNoticeService.Dismiss(id) && window.active && window.Closed == 0, "确认旧记录不关闭重新出现的提示");
        Check(GameNoticeService.Dismiss(secondId) && !window.active && window.Closed == 1, "确认当前提示只调用对应原生关闭一次");
        Check(GameNoticeService.Dismiss(secondId) && window.Closed == 1 && Pending().Count == 0, "重复确认幂等");
        GameMain.data = new(); Poll();
        Check(!GameNoticeService.Dismiss(secondId) && Pending().Count == 0, "换档清除旧提示，不接受旧会话身份");
        window.Show(1001, "新会话"); Poll();
        Check((int)Pending().Single()["id"] > secondId, "会话切换不复用旧提示 ID");
        UIRoot.instance.uiGame.goalPanel.goalGroups.Add(new GoalGroup { active = true, protoId = 10,
            nameText = new() { text = "目标正文" }, goalData = new() { stage = 2 },
            goalInfoEntries = new() { new() { active = true, protoId = 11, nameText = new() { text = "条目正文" }, goalData = new() { stage = 1 } }, new() { active = false } } });
        var goal = (JsonObject)((List<object>)GameNoticeService.CapturePending()["goals"]).Single();
        Check((string)goal["text"] == "目标正文" && (string)goal["stage"] == "2" &&
            ((List<object>)goal["items"]).Count == 1, "非空目标和阶段被序列化，隐藏条目被排除");

        var executor = new TaskCommandExecutor();
        foreach (var type in new[] { "entityfasttakeout", "transferstorageitem", "placebuilding", "placebelt", "placesorter" })
        {
            var before = GameMain.mainPlayer.Cleared;
            executor.StopCommandEffects(new CommandState { NormalizedType = type, OwnsPlayerOrders = true });
            Check(GameMain.mainPlayer.Cleared == before + 1, type + " 取消清理自己持有的靠近订单");
        }
        var count = GameMain.mainPlayer.Cleared; var flight = new FlightInput();
        executor.StopCommandEffects(new CommandState { NormalizedType = "placebuilding", Flight = flight });
        Check(GameMain.mainPlayer.Cleared == count && flight.Disposed, "未持有订单时不清除其他任务移动，仍释放输入包装");
        var command = new CommandState { EnteredBuildMode = true };
        GameMain.mainPlayer.controller.cmd.type = ECommand.Move;
        executor.ExitCommandBuildMode(command);
        Check(!command.EnteredBuildMode && GameMain.mainPlayer.controller.cmd.type == ECommand.Move, "退出建造不覆盖当前非建造模式");
        command.EnteredBuildMode = true; GameMain.mainPlayer.controller.cmd.type = ECommand.Build;
        executor.ExitCommandBuildMode(command); executor.ExitCommandBuildMode(command);
        Check(GameMain.mainPlayer.controller.actionBuild.Closed == 1 && GameMain.mainPlayer.controller.cmd.type == ECommand.None,
            "建造清理幂等，只关闭命令实际进入的建造模式");
        AutomaticDSP.Tasks.TaskCommandExecutor.ValidateReverse();
        AutomaticDSP.Tasks.TaskCommandExecutor.ValidatePrebuild();
        AutomaticDSP.Tasks.TaskCommandExecutor.ValidateTrash();
        Console.WriteLine($"共 {passed} 项源码隔离检查通过；未模拟原生物理或游戏资源。");
    }
}

namespace AutomaticDSP.State
{
    internal sealed partial class GameStateQueryService
    {
        internal static void ValidateResources()
        {
            GameMain.localStar = new(); GameMain.localPlanet = new() { star = GameMain.localStar };
            var near = new PlanetData { star = new(), uPosition = new() { x = 14399999 }, scanned = true };
            var far = new PlanetData { star = near.star, uPosition = new() { x = 14400000 }, scanned = true };
            GameMain.history.universeObserveLevel = 2;
            Program.Check(RequiredObserveLevel(near) == 3 && !CanObserveResources(near), "等级 2 不越权读取近星系资源");
            GameMain.history.universeObserveLevel = 3;
            Program.Check(CanObserveResources(near) && !CanObserveResources(far) && RequiredObserveLevel(far) == 4,
                "等级 3 距离严格小于 14400000，等于边界仍需等级 4");
            Program.Check(!IsResourceMemberRestricted(near, "runtimeVeinGroups") && IsResourceMemberRestricted(far, "runtimeVeinGroups"),
                "资源对象读取与摘要共享等级权限");
            near.scanned = false;
            Program.Check(IsResourceMemberRestricted(near, "data") && IsResourceMemberRestricted(new PlanetFactory { planet = near }, "entityPool"),
                "未扫描时原始 data 和直接 factory 入口均受限");
            var resources = CapturePlanetResources(near);
            Program.Check((string)resources["status"] == "scanning" && resources["minerals"] == null && near.scanning,
                "有权限但未扫描只发起扫描，不伪造已知矿量");
            far.factory = new() { planet = far }; GameMain.history.universeObserveLevel = 1;
            Program.Check(CanObserveResources(far), "已建工厂按原生等级 1 例外可观察");
            GameMain.history.universeObserveLevel = 0;
            Program.Check(!CanObserveResources(far), "工厂例外不绕过最低等级");
            Program.Check(IsResourceMemberRestricted(near, "seed") && IsResourceMemberRestricted(near.star, "seed"), "生成种子始终不对外暴露");
        }
    }
}
