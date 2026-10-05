using System;
using System.Collections.Generic;
using System.Linq;
using AutomaticDSP.Tasks;
using AutomaticDSP.Serialization;
using AutomaticDSP.Storage;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using static AutomaticDSP.Tasks.TaskStatusNames;

static class Program
{
    static int Executions;
    static readonly Dictionary<string,CommandState> Seen=new();
    static readonly HashSet<string> Complete=new();
    static TaskQueueService New()
    {
        Executions=0;Seen.Clear();Complete.Clear();TaskCommandExecutor.Stops.Clear();
        TaskCommandExecutor.Behavior=(c,f,n)=>{
            Executions++;Seen[c.Id]=c;
            if(c.Id.StartsWith("fail")) {f(c,CommandFailed,"test",null,n,null);return;}
            if(c.Type=="placeBuilding") {c.BuildObjectId=-1;c.EnteredBuildMode=!c.Background;}
            if(c.Type=="applyFactoryBlueprint") {c.BuildTargets=new();c.EnteredBuildMode=!c.Background;}
            if(c.Type=="craftInventory"||c.Type=="researchTech") c.ActionIssued=true;
            if(c.Type=="discardInventoryItem"||c.Type=="noop"||Complete.Contains(c.Id)) f(c,CommandSucceeded,null,null,n,new JsonObject());
        };
        return new(new HistoryStore(),new ManualLogSource());
    }
    static TaskCommandRequest C(string id,string type,params string[] deps)=>new(){Id=id,Type=type,DependsOn=deps.ToList()};
    static string Add(TaskQueueService q,params TaskCommandRequest[] cmds)=>(string)((JsonObject)q.Enqueue(new(){Commands=cmds.ToList()})["task"])["id"];
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("通过："+name);}
    static void Main()
    {
        var navQueue=New();Add(navQueue,C("nav","navigateTo"),C("build-after-nav","placeBuilding"),C("move-after-nav","moveTo"),C("nav-craft","craftInventory"),C("nav-trash","discardInventoryItem"));navQueue.Update();
        Check(Seen["nav"].OwnsPlayerOrders&&!Seen.ContainsKey("build-after-nav")&&!Seen.ContainsKey("move-after-nav")&&Seen["nav-craft"].Background&&Seen["nav-trash"].Status==CommandSucceeded,"导航阻止后续建造与移动但允许独立通道");
        Add(navQueue,C("other-nav","navigateTo"));navQueue.Update();Check(!Seen.ContainsKey("other-nav"),"跨任务导航不能抢占正在运行的导航");
        Complete.Add("nav");navQueue.Update();Check(Seen.ContainsKey("build-after-nav")&&Seen["move-after-nav"].OwnsPlayerOrders&&!Seen.ContainsKey("other-nav"),"导航完成后按顺序释放机甲通道");
        navQueue=New();var background=Add(navQueue,C("nav-background-build","placeBuilding"));Add(navQueue,C("nav","navigateTo"));navQueue.Update();TaskCommandExecutor.Stops.Clear();navQueue.Cancel(background);navQueue.Update();
        Check(TaskCommandExecutor.Stops.Count==0&&Seen["nav"].OwnsPlayerOrders&&Seen["nav"].Status==CommandRunning,"取消后台建造不会停止导航");
        var q=New();Add(q,C("move","moveTo"),C("craft","craftInventory"),C("research","researchTech"),C("trash","discardInventoryItem"));q.Update();
        Check(Seen.Count==4&&Seen["move"].OwnsPlayerOrders&&Seen["craft"].Background&&Seen["research"].Background&&Seen["trash"].Status==CommandSucceeded,"四通道与垃圾自动分流");
        q=New();var id=Add(q,C("build","placeBuilding"),C("move","moveTo"));q.Update();
        Check(Seen["build"].Background&&!Seen["build"].EnteredBuildMode&&Seen["move"].OwnsPlayerOrders,"预建释放移动与建造模式");
        Check((string)((JsonObject)q.GetTaskResponse(id)["task"])["status"]==TaskRunning,"预建不冒充整项完成");
        q=New();id=Add(q,C("blueprint","applyFactoryBlueprint"),C("move","moveTo"));q.Update();
        Check(Seen["blueprint"].Background&&!Seen["blueprint"].EnteredBuildMode&&Seen["move"].OwnsPlayerOrders,"蓝图预建释放机甲通道");
        Check((string)((JsonObject)q.GetTaskResponse(id)["task"])["status"]==TaskRunning,"蓝图必须等待实际落成");
        q=New();Add(q,C("blueprint","applyFactoryBlueprint"),C("after","noop","blueprint"));q.Update();
        Check(!Seen.ContainsKey("after"),"蓝图显式依赖等待整组落成");Complete.Add("blueprint");q.Update();Check(Seen.ContainsKey("after"),"蓝图落成释放显式依赖");
        q=New();var connect=C("connect","placeSorter");connect.ExtensionData=new Dictionary<string,JToken>{{"input",JObject.Parse("{commandId:'build',slot:0}")}};
        Add(q,C("build","placeBuilding"),connect);q.Update();Check(!Seen.ContainsKey("connect"),"实体引用自动等待落成");Complete.Add("build");q.Update();Check(Seen.ContainsKey("connect"),"落成后释放依赖");
        q=New();var bg=Add(q,C("build","placeBuilding"));Add(q,C("move","moveTo"));q.Update();TaskCommandExecutor.Stops.Clear();q.Cancel(bg);q.Update();
        Check(TaskCommandExecutor.Stops.Count==0&&Seen["move"].OwnsPlayerOrders,"取消后台建造不停止其他任务移动");
        q=New();var timeout=C("build","placeBuilding");timeout.TimeoutSeconds=60;Add(q,timeout);Add(q,C("move","moveTo"));q.Update();Seen["build"].StartedAt=DateTimeOffset.UtcNow.AddSeconds(-61);TaskCommandExecutor.Stops.Clear();q.Update();
        Check(Seen["build"].Status==CommandFailed&&TaskCommandExecutor.Stops.Count==0&&Seen["move"].OwnsPlayerOrders,"后台建造超时不清除移动订单");
        q=New();Add(q,C("craft","craftInventory"),C("build","placeBuilding","craft"),C("trash","discardInventoryItem"));q.Update();
        Check(!Seen.ContainsKey("build")&&Seen["trash"].Status==CommandSucceeded,"显式材料依赖不堵即时操作");Complete.Add("craft");q.Update();Check(Seen.ContainsKey("build"),"制造完成释放依赖");
        q=New();Add(q,C("move1","moveTo"));Add(q,C("move2","moveTo"),C("trash","discardInventoryItem"));q.Update();Check(!Seen.ContainsKey("move2")&&Seen.ContainsKey("trash"),"跨任务机甲互斥且垃圾无需排队");Complete.Add("move1");q.Update();Check(Seen.ContainsKey("move2"),"移动按先后顺序推进");
        q=New();var rejected=false;try{Add(q,C("x","noop","future"),C("future","noop"));}catch(TaskQueueException){rejected=true;}Check(rejected,"提交时拒绝前向依赖与环");
        q=New();Add(q,C("build","placeBuilding"),C("move","moveTo"),C("fail-trash","discardInventoryItem"),C("later","noop"));q.Update();
        Check(Seen["build"].Status==CommandCancelled&&Seen["move"].Status==CommandCancelled&&!Seen.ContainsKey("later"),"失败停止本任务未完成工作");
        q=New();GameMain.gameTick=10;var paused=C("paused","waitUntil");paused.TimeoutSeconds=1;Add(q,paused);q.Update();Seen["paused"].StartedAt=DateTimeOffset.UtcNow.AddSeconds(-2);q.Update();
        Check(GameMain.gameTick==10&&Seen["paused"].Status==CommandFailed,"游戏 tick 不推进时仍按墙钟超时");
        foreach(var type in new[]{"reformTerrain","collectVegetation","plantVegetation"})
        {
            q=New();Add(q,C("move","moveTo"),C("terrain",type),C("later","moveTo"));q.Update();
            Check(!Seen.ContainsKey("terrain"),type+" 等待移动释放机甲");
            Complete.Add("move");q.Update();
            Check(Seen["terrain"].OwnsPlayerOrders&&!Seen["terrain"].Background&&!Seen.ContainsKey("later"),type+" 占用机甲且阻止后续移动");
            Complete.Add("terrain");q.Update();Check(Seen.ContainsKey("later"),type+" 完成后释放机甲");
        }
        q=New();id=Add(q,C("take","entityFastTakeOut"));q.Update();q.Cancel(id);q.Update();var count=Executions;q.Update();
        Check(Seen["take"].Status==CommandCancelled&&Executions==count&&TaskCommandExecutor.Stops.Contains("take"),"取消取料后不再调用执行器");
        Console.WriteLine("调度回归全部通过");
    }
}
