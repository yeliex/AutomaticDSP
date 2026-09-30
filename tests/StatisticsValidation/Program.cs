using System.Collections;
using System.Reflection;
using System.Runtime.Loader;

// 装载真实 Mod 和原生统计类型，离线构造数据，不启动或修改游戏。
var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var mod = Path.Combine(root, "src/AutomaticDSP/bin/Debug/net472");
var game = "C:/Program Files (x86)/Steam/steamapps/common/Dyson Sphere Program";
AssemblyLoadContext.Default.Resolving += (_, name) => {
    foreach (var dir in new[] { mod, game + "/DSPGAME_Data/Managed", game + "/BepInEx/core" }) {
        var path = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(mod, "AutomaticDSP.dll"));
var native = Assembly.Load("Assembly-CSharp");
var math = assembly.GetType("AutomaticDSP.State.StatisticsWindow", true)!;
var service = assembly.GetType("AutomaticDSP.State.GameStateQueryService", true)!;
var parser = assembly.GetType("AutomaticDSP.State.StateQueryParser", true)!;
object Call(Type type, string name, params object[] values) => type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, values)!;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
object Parse(string query) => Call(parser, "Parse", query, null!);
var passed = 0;
void Test(string label, Action action) { action(); passed++; Console.WriteLine("通过：" + label); }

Test("参数严格校验与别名合并", () => {
    Parse("{ a: statistics(astroFilter: -1, timeLevel: 5) { power { storedJoules } } b: statistics(astroFilter: 100, timeLevel: 0) { research { hashes } } }");
    Parse("{ statistics { timeLevel } statistics(astroFilter: 0,timeLevel: 0) { gameTick } }");
    foreach (var query in new[] {
        "{statistics(timeLevel:6){gameTick}}", "{statistics(timeLevel:1.5){gameTick}}",
        "{statistics(astroFilter:\"103\"){gameTick}}", "{statistics(astroFilter:-2){gameTick}}",
        "{statistics(timeLevel:0){gameTick} statistics(timeLevel:1){gameTick}}"
    }) {
        var rejected = false;
        try { Parse(query); } catch (TargetInvocationException e) when (e.InnerException!.GetType().Name == "StateQueryParseException") { rejected = true; }
        Check(rejected, "未拒绝非法统计参数：" + query);
    }
});
Test("窗口分母、累计量与早期历史层级", () => {
    Check((double)Call(math, "PerMinute", 1200L, 1) == 120, "十分钟速率");
    Check(Call(math, "PerMinute", 1200L, 5) == null, "累计量不应伪造速率");
    Check((int)Call(math, "HistoryLevel", 5, 3600L) == 0, "总计历史一分钟边界");
    Check((int)Call(math, "HistoryLevel", 5, 3601L) == 1, "总计历史跨窗口");
    Check((int)Call(math, "HistoryLevel", 5, 2160001L) == 4, "总计历史上限");
});
Test("产消环形历史、跨层残余及分组隔离", () => {
    var count = new int[7200];
    var cursors = Enumerable.Range(0,12).Select(i => i*600).ToArray();
    cursors[1] = 1199; count[600] = 11; count[1198] = 22;
    cursors[0] = 1; count[0] = 7; count[599] = 8; count[598] = 9;
    var points = (long[])Call(math, "ProductHistory", count, cursors, 0, 0, 9L, false);
    Check(points.Length == 600 && points[0] == 11 && points[598] == 22 && points[599] == 24, "环形或末点错误");
    Check(((long[])Call(math, "ProductHistory", count, cursors, 0, 1, 9L, false)).All(v => v == 0), "产消串组");
    Check(((long[])Call(math, "ProductHistory", count, cursors, 0, 0, 12L, false))[599] == 0, "整窗口末点应为零");
});
Test("运输错峰采样与不完整末点", () => {
    var count = new int[1080];
    var cursors = Enumerable.Range(0,18).Select(i=>i*60).ToArray();
    cursors[0] = 6; count[5] = 17;
    var before = (long[])Call(math, "ProductHistory", count, cursors, 0, 0, 65L, true);
    var boundary = (long[])Call(math, "ProductHistory", count, cursors, 0, 0, 66L, true);
    Check(before[59] == 17 && boundary[59] == 0, "运输记录偏移必须为6 tick");
    foreach (var level in Enumerable.Range(0,5))
        Check(((long[])Call(math,"ProductHistory",new int[1080],Enumerable.Range(0,18).Select(i=>i*60).ToArray(),level,0,0L,true)).All(v=>v==0),"新存档长窗口应安全返回零");
});
Test("电力一秒均值与原生完整历史槽", () => {
    var energy = new long[3600];
    var cursors = Enumerable.Range(0,6).Select(i=>i*600).ToArray();
    Array.Fill(energy, 20L, 0, 600);
    Check((long)Call(math, "RecentPower", energy, 3) == 1200, "60 tick 能量应为1200 W");
    cursors[1] = 1199; energy[1199] = 99; energy[600] = 100;
    var points = (long[])Call(math, "PowerHistory", energy, cursors, 0);
    Check(points[0] == 99 && points[1] == 100, "电力曲线从当前槽开始");
});
Test("五档历史与原生面板纯计算方法逐点一致", () => {
    var uiType = native.GetType("UIStatisticsWindow",true)!;
    var ui = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(uiType);
    var random = new Random(17);
    foreach (var traffic in new[]{false,true})
    foreach (var level in Enumerable.Range(0,5))
    foreach (var group in new[]{0,1})
    foreach (var tick in new[]{0L,65L,66L,1234567L}) {
        var length = traffic ? 60 : 600;
        var count = Enumerable.Range(0,length*12).Select(_=>random.Next(100)).ToArray();
        var cursor = Enumerable.Range(0,12).Select(i=>i*length+random.Next(length)).ToArray();
        var expected = new long[length];
        var offset = level+1+group*6;
        uiType.GetMethod("ComputeFirstHalfDetailExceptLast")!.Invoke(ui,new object[]{offset*length+length-1,length,cursor[offset],count,expected});
        var ticks = traffic ? new[]{10,60,600,3600,36000,360000} : new[]{1,6,60,360,3600,36000};
        // 原生面板在早于该层首个记录时可能越界；这部分另测零填充，不调用原生方法。
        if (traffic && tick < ticks[level+1]) continue;
        object[] values = traffic
            ? new object[]{level+1,group*6,length,tick,ticks,new[]{0,6,60,360,3600,36000},cursor,count}
            : new object[]{level+1,group*6,length,tick,ticks,cursor,count};
        var refine = uiType.GetMethods().Single(m=>m.Name=="RefineTheLastDetailValue" && m.GetParameters().Length==values.Length && m.GetParameters().Last().ParameterType==typeof(int[]));
        expected[^1]=(int)refine.Invoke(ui,values)!;
        var actual=(long[])Call(math,"ProductHistory",count,cursor,level,group,tick,traffic);
        Check(actual.SequenceEqual(expected),$"原生历史不一致：traffic={traffic}, level={level}, group={group}, tick={tick}");
    }
});
Test("原生工厂生产、研究与功率统计经适配后保持口径", () => {
    var type = native.GetType("FactoryProductionStat", true)!;
    var stat = Activator.CreateInstance(type)!;
    type.GetMethod("Init")!.Invoke(stat, null);
    var itemType = native.GetType("ItemProto", true)!;
    itemType.GetField("itemIds")!.SetValue(null, new[]{1001});
    var products = (int[])type.GetField("productRegister")!.GetValue(stat)!;
    var consumes = (int[])type.GetField("consumeRegister")!.GetValue(stat)!;
    products[1001] = 2; consumes[1001] = 1;
    type.GetField("powerGenRegister")!.SetValue(stat, 100L);
    type.GetField("powerConRegister")!.SetValue(stat, 80L);
    type.GetField("powerChaRegister")!.SetValue(stat, 3L);
    type.GetField("powerDisRegister")!.SetValue(stat, 5L);
    type.GetField("hashRegister")!.SetValue(stat, 7L);
    for (long tick = 1; tick <= 3600; tick++) type.GetMethod("GameTick")!.Invoke(stat,new object[]{tick});
    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!; list.Add(stat);
    var queryField = assembly.GetType("AutomaticDSP.State.StateQueryField",true)!;
    object Field(string name) => Activator.CreateInstance(queryField,name,name)!;
    var rows = (IList)Call(service,"PanelProducts",list,null!,false,null!,0,3600L,Field("products"),false);
    var row = (IDictionary)rows[0]!;
    Check((long)row["produced"]! == 7200 && (long)row["consumed"]! == 3600 && row["imported"] == null,"产消或全局运输语义");
    var research = (IDictionary)Call(service,"PanelResearch",list,0,3600L,Field("research"));
    Check((long)research["hashes"]! == 25200 && (double)research["hashesPerSecond"]! == 420,"科研单位换算");
    var factories = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(native.GetType("PlanetFactory",true)!))!;
    var power = (IDictionary)Call(service,"PanelPower",list,factories,0,3600L,Field("power"));
    Check((long)power["generationCapacityWatts"]! == 6000 && (long)power["consumptionDemandWatts"]! == 4800,"供需功率换算");
    Check((long)power["chargeWatts"]! == 180 && (long)power["dischargeWatts"]! == 300,"蓄电充放电不能反转");
    type.GetMethod("CreateProductStat")!.Invoke(stat,new object[]{11902});
    rows=(IList)Call(service,"PanelProducts",list,null!,false,null!,0,3600L,Field("products"),false);
    Check(rows.Count==1,"戴森虚拟项不应混入物品列表");
    rows=(IList)Call(service,"PanelProducts",list,null!,false,null!,0,3600L,Field("products"),true);
    Check(rows.Count==3 && (string)((IDictionary)rows[1]!)["name"]! == "结构点","虚拟项需要可识别名称");
    var trafficType=native.GetType("AstroTrafficStat",true)!;
    var traffic=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(trafficType);
    var itemIndices=new int[12000]; itemIndices[1001]=1;
    trafficType.GetField("itemIndices")!.SetValue(traffic,itemIndices);
    var trafficStatType=native.GetType("TrafficStat",true)!;
    var transport=Activator.CreateInstance(trafficStatType)!;
    trafficStatType.GetMethod("Init",new[]{typeof(int)})!.Invoke(transport,new object[]{1001});
    var trafficTotals=(long[])trafficStatType.GetField("total")!.GetValue(transport)!;
    trafficTotals[1]=50; trafficTotals[8]=20; trafficTotals[6]=500; trafficTotals[13]=200;
    var trafficPool=Array.CreateInstance(trafficStatType,2); trafficPool.SetValue(transport,1);
    trafficType.GetField("trafficPool")!.SetValue(traffic,trafficPool);
    trafficType.GetField("trafficCursor")!.SetValue(traffic,2);
    rows=(IList)Call(service,"PanelProducts",list,traffic,true,null!,0,3600L,Field("products"),false);
    row=(IDictionary)rows[0]!;
    Check((long)row["imported"]! == 50 && (long)row["exported"]! == 20,"非零进口出口映射");
    rows=(IList)Call(service,"PanelProducts",list,traffic,true,null!,5,3600L,Field("products"),false);
    row=(IDictionary)rows[0]!;
    Check((long)row["imported"]! == 500 && (long)row["exported"]! == 200 && row["importPerMinute"]==null,"进出口累计窗口");
});
Console.WriteLine($"统计验证通过：{passed} 组。");
