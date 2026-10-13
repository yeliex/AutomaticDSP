using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

static class InserterTechValidation
{
    public static void Run(Assembly mod)
    {
        var game = Assembly.Load("Assembly-CSharp");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object Native(string name) => RuntimeHelpers.GetUninitializedObject(game.GetType(name, true)!);
        void Set(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, flags);
            if (field != null) field.SetValue(target, value);
            else target.GetType().GetProperty(name, flags)!.SetValue(target, value);
        }
        object Get(object target, string name) => target.GetType().GetField(name, flags)!.GetValue(target)!;
        var dataField = game.GetType("GameMain")!.GetField("data")!;
        var techsField = game.GetType("LDB")!.GetField("_techs", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldData = dataField.GetValue(null); var oldTechs = techsField.GetValue(null);
        var reconcile = mod.GetType("AutomaticDSP.GameControl.InserterTechEffectRepair")!
            .GetMethod("Reconcile", BindingFlags.Static | BindingFlags.NonPublic)!;
        try
        {
            var history = Native("GameHistoryData"); var data = Native("GameData");
            var player = Native("Player"); Set(player, "mecha", Native("Mecha"));
            Set(data, "mainPlayer", player); Set(data, "history", history); Set(history, "gameData", data);
            dataField.SetValue(null, data);
            var states = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(int), game.GetType("TechState")!))!;
            Set(history, "techStates", states);
            void State(int id, int level, bool unlocked = true, int? current = null)
            {
                var state = Native("TechState"); Set(state, "unlocked", unlocked);
                Set(state, "curLevel", current ?? level); Set(state, "maxLevel", level);
                Set(state, "hashUploaded", 123L); Set(state, "hashNeeded", 456L); states[id] = state;
            }
            // 数值取自本机资源中的 TechProtoSet；执行使用真实原生方法，不模拟覆盖或同步。
            var values = new[] { (3306, 1, new[] {39, 40}, new[] {1.0, 1.0}),
                (3311, 1, new[] {39}, new[] {2.0}), (3312, 2, new[] {41}, new[] {3.0}),
                (3313, 3, new[] {39}, new[] {3.0}), (3314, 4, new[] {41}, new[] {4.0}),
                (3315, 5, new[] {39}, new[] {4.0}), (3316, 6, new[] {40}, new[] {1.0}) };
            var protos = Array.CreateInstance(game.GetType("TechProto")!, values.Length);
            var indices = new Dictionary<int, int>();
            for (var i = 0; i < values.Length; i++)
            {
                var (id, level, functions, unlockValues) = values[i]; var proto = Native("TechProto");
                Set(proto, "ID", id); Set(proto, "Level", level); Set(proto, "MaxLevel", level);
                Set(proto, "UnlockFunctions", functions); Set(proto, "UnlockValues", unlockValues);
                protos.SetValue(proto, i); indices[id] = i; State(id, level);
            }
            var table = Native("TechProtoSet"); Set(table, "dataArray", protos);
            table.GetType().BaseType!.GetField("dataIndices", flags)!.SetValue(table, indices);
            Assembly.Load("UnityEngine.CoreModule").GetType("UnityEngine.Object")!
                .GetField("m_CachedPtr", flags)!.SetValue(table, new IntPtr(1));
            techsField.SetValue(null, table);
            var factory = Native("PlanetFactory"); var system = Native("FactorySystem");
            Set(factory, "factorySystem", system);
            var factories = Array.CreateInstance(factory.GetType(), 1); factories.SetValue(factory, 0);
            Set(data, "factories", factories); Set(data, "factoryCount", 1);
            var inserters = Array.CreateInstance(game.GetType("InserterComponent")!, 3);
            for (var id = 1; id <= 2; id++)
            {
                var inserter = Native("InserterComponent"); Set(inserter, "id", id);
                Set(inserter, "grade", (byte)(id == 1 ? 4 : 3)); inserters.SetValue(inserter, id);
            }
            Set(system, "inserterPool", inserters); Set(system, "inserterCursor", 3);
            Set(history, "inserterStackCountObsolete", 6); Set(history, "inserterStackInput", 4);
            Set(history, "inserterBidirectional", true);
            var queue = new[] { 3904, 3509 }; Set(history, "techQueue", queue);
            var unlock = history.GetType().GetMethod("UnlockTechFunction")!;
            void Apply(int function, double value) => unlock.Invoke(history, new object[] {function, value, 1});
            void Repair() => reconcile.Invoke(null, new[] {history});
            void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("通过：" + message); }
            int Output() => (int)Get(history, "inserterStackOutput");
            Apply(39, 4); Apply(39, 1);
            Check(Output() == 1 && (byte)Get(inserters.GetValue(1)!, "stackOutput") == 1, "原生旧科技覆盖高阶效果及已有集装爪");
            Repair();
            Check(Output() == 4 && (byte)Get(inserters.GetValue(1)!, "stackOutput") == 4, "恢复已完成最高输出并原生同步已有集装爪");
            Apply(39, 1); Apply(39, 4); Repair();
            Check(Output() == 4, "正常先旧后新完成顺序保持最高效果");
            Check((byte)Get(inserters.GetValue(2)!, "stackOutput") == 1 && (int)Get(history, "inserterStackInput") == 4 && (bool)Get(history, "inserterBidirectional"), "普通高速爪、输入堆叠和双向能力保持原生语义");
            Set(data, "factoryCount", 1); Set(data, "factories", null!);
            Repair(); // 若重复调用原生同步，这里将因没有工厂数组而失败。
            Check(Output() == 4, "一致状态幂等且不重复扫描同步工厂");
            Set(data, "factories", factories);
            State(3315, 5, false); Apply(39, 1); Repair();
            Check(Output() == 3, "未解锁高阶效果不会提前获得");
            State(3313, 3, true, 2); Apply(39, 1); Repair();
            Check(Output() == 2, "未完成有限等级不会提前获得");
            State(3311, 1, false); Apply(39, 1); Repair();
            Check(Output() == 1, "仅旧科技完成时保留单件输出");
            State(3315, 5); State(3306, 1, false); Repair();
            Check(Output() == 1, "旧科技未完成时不触发此项兼容修复");
            State(3306, 1); Apply(39, 5); Repair();
            Check(Output() == 5, "不降低已有更高效果");
            Check(states.Count == 7 && states.Values.Cast<object>().All(s => (long)Get(s, "hashUploaded") == 123 && (long)Get(s, "hashNeeded") == 456), "科研投入与科技条目不变");
            Check(ReferenceEquals(Get(history, "techQueue"), queue) && queue.SequenceEqual(new[] {3904, 3509}), "科研队列及顺序不变");
            reconcile.Invoke(null, new object[] { null! });
            var repairType = reconcile.DeclaringType!;
            var repair = Activator.CreateInstance(repairType)!;
            var update = repairType.GetMethod("Update", flags)!;
            var eventField = history.GetType().GetField("onTechUnlocked", flags)!;
            try
            {
                Apply(39, 1);
                update.Invoke(repair, new[] { history });
                Check(Output() == 4, "加载存档时修复已有的科技覆盖");
                Apply(39, 1);
                update.Invoke(repair, new[] { history });
                Check(Output() == 1, "同一存档的后续帧不扫描或重放科技效果");
                ((Delegate)eventField.GetValue(history)!).DynamicInvoke(3501, 1, false);
                Check(Output() == 1, "无关科技解锁不触发修复");
                ((Delegate)eventField.GetValue(history)!).DynamicInvoke(3306, 1, false);
                Check(Output() == 4, "相关科技解锁事件触发修复");
                update.Invoke(repair, new object[] { null });
                Check(eventField.GetValue(history) == null, "离开存档时解除科技事件订阅");
                update.Invoke(repair, new[] { history });
            }
            finally { ((IDisposable)repair).Dispose(); }
            Check(eventField.GetValue(history) == null, "插件销毁时解除科技事件订阅");
        }
        finally { dataField.SetValue(null, oldData); techsField.SetValue(null, oldTechs); }
    }
}
