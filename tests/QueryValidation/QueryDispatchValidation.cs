using System.Reflection;
using System.Runtime.CompilerServices;

static class QueryDispatchValidation
{
    public static void Run(Assembly mod)
    {
        var game = Assembly.Load("Assembly-CSharp");
        var gameMain = game.GetType("GameMain", true)!;
        var dataField = gameMain.GetField("data")!;
        var instanceField = gameMain.GetField("<instance>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!;
        var preload = game.GetType("VFPreload", true)!;
        var done = preload.GetField("done")!;
        var dbDone = preload.GetField("dbDone")!;
        var oldData = dataField.GetValue(null);
        var oldInstance = instanceField.GetValue(null);
        var oldDone = done.GetValue(null);
        var oldDbDone = dbDone.GetValue(null);
        var main = RuntimeHelpers.GetUninitializedObject(gameMain);
        // 离线夹具只保留非空 Unity 对象身份，避免调用原生对象存活查询。
        Assembly.Load("UnityEngine.CoreModule").GetType("UnityEngine.Object", true)!
            .GetField("m_CachedPtr", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(main, new IntPtr(1));
        var tick = gameMain.GetField("timei")!;
        var paused = gameMain.GetField("_paused", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            instanceField.SetValue(null, main);
            var data = RuntimeHelpers.GetUninitializedObject(game.GetType("GameData", true)!);
            data.GetType().GetField("gameDesc")!.SetValue(data,
                RuntimeHelpers.GetUninitializedObject(game.GetType("GameDesc", true)!));
            dataField.SetValue(null, data);
            gameMain.GetField("_running", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(main, true);
            done.SetValue(null, true);
            dbDone.SetValue(null, true);
            var serviceType = mod.GetType("AutomaticDSP.State.GameStateQueryService", true)!;
            var loggerType = Assembly.Load("BepInEx").GetType("BepInEx.Logging.ManualLogSource", true)!;
            var service = Activator.CreateInstance(serviceType, 60, Activator.CreateInstance(loggerType, "查询调度验证"))!;
            var update = serviceType.GetMethod("Update")!;
            var enqueue = serviceType.GetMethod("EnqueueStateQuery")!;
            var planType = mod.GetType("AutomaticDSP.State.StateQueryPlan", true)!;
            Task Query() => (Task)enqueue.Invoke(service, new[] { Activator.CreateInstance(planType)!, (object)CancellationToken.None })!;
            void Frame(long value) { tick.SetValue(main, value); update.Invoke(service, null); }
            void Completed(Task query, string message)
            {
                if (!query.IsCompletedSuccessfully) throw new Exception(message);
            }
            var first = Query();
            Frame(101);
            Completed(first, "首次查询不能等待整除 tick");
            var crossing = Query();
            Frame(130);
            if (crossing.IsCompleted) throw new Exception("查询间隔未满足时不应派发");
            Frame(162);
            Completed(crossing, "跨过整除 tick 后查询不能饿死");
            var same = Query();
            Frame(162);
            if (same.IsCompleted) throw new Exception("同一 tick 不应重复派发");
            paused.SetValue(main, true);
            Frame(162);
            Completed(same, "暂停时应允许查询当前状态");
            paused.SetValue(main, false);
            var reload = Query();
            Frame(10);
            Completed(reload, "读档回退 tick 后应恢复查询");
            var largeJump = Query();
            Frame(131);
            Completed(largeJump, "跨多个查询周期后应派发");
            Console.WriteLine("通过：真实查询服务首次派发、间隔、跨 tick、暂停、读档回退与大跨度调度；不模拟实机帧率。");
        }
        finally
        {
            dataField.SetValue(null, oldData);
            instanceField.SetValue(null, oldInstance);
            done.SetValue(null, oldDone);
            dbDone.SetValue(null, oldDbDone);
        }
    }
}
