using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
// 在独立进程装载真实 Mod 程序集，只构造查询数据，不访问运行中的游戏。
var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var mod = Path.Combine(root, "src/AutomaticDSP/bin/Debug/net472");
var game = "C:/Program Files (x86)/Steam/steamapps/common/Dyson Sphere Program";
AssemblyLoadContext.Default.Resolving += (_, name) => {
    foreach (var dir in new[]{mod, game+"/DSPGAME_Data/Managed", game+"/BepInEx/core"}) {
        var path=Path.Combine(dir,name.Name+".dll"); if(File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(mod,"AutomaticDSP.dll"));
var fieldType=assembly.GetType("AutomaticDSP.State.StateQueryField",true)!;
object Field(string name, params string[] children) {
    var f=Activator.CreateInstance(fieldType,name,name)!;
    var list=(IList)fieldType.GetProperty("Children")!.GetValue(f)!;
    foreach(var child in children)list.Add(Activator.CreateInstance(fieldType,child,child));
    return f;
}
var field=Field("buffer","id","ownerId","title","content","contentColorIndex");
var filterType=assembly.GetType("AutomaticDSP.State.StateQueryFilter",true)!;
var op=assembly.GetType("AutomaticDSP.State.StateQueryFilterOperator",true)!;
((IList)fieldType.GetProperty("Filters")!.GetValue(field)!).Add(Activator.CreateInstance(filterType,new string[]{"ownerId"},Enum.Parse(op,"Equals"),new List<object>{104}));
var records=new[]{new {id=1,ownerId=103,title="母星",content="原料",contentColorIndex=new[]{1,2}},new{id=2,ownerId=104,title="冰原",content="钛与硅\n补给",contentColorIndex=new[]{3,4}}};
var service=assembly.GetType("AutomaticDSP.State.GameStateQueryService",true)!;
var method=service.GetMethod("ResolveQueryValue",BindingFlags.NonPublic|BindingFlags.Static)!;
var result=(IList)method.Invoke(null,new object[]{records,field,0})!;
var row=(IDictionary)result[0]!;
if(result.Count!=1 || (string)row["title"]!="冰原" || (string)row["content"]!="钛与硅\n补给" || (int)row["ownerId"]! !=104 || ((IList)row["contentColorIndex"]!).Count!=2)throw new Exception("非空查询失败");
Console.WriteLine("通过：真实 Mod 查询器保留非空中文/换行/颜色数组，按跨行星 ownerId 筛选；这是离线数据夹具，不代表实机创建备忘录。");
var markerField=Field("markerPool","gid","astroId","name","word","color");
var colorType=Assembly.Load("UnityEngine.CoreModule").GetType("UnityEngine.Color",true)!;
var color=Activator.CreateInstance(colorType,0.25f,0.5f,0.75f,1f)!;
var markers=new[]{new{gid=7,astroId=103,name="补给点",word="燃料",color},new{gid=8,astroId=104,name="采矿点",word="钛",color}};
((IList)fieldType.GetProperty("Filters")!.GetValue(markerField)!).Add(Activator.CreateInstance(filterType,new string[]{"astroId"},Enum.Parse(op,"Equals"),new List<object>{104}));
var markerRows=(IList)method.Invoke(null,new object[]{markers,markerField,0})!;
var marker=(IDictionary)markerRows[0]!;
var colorResult=(IDictionary)marker["color"]!;
if(markerRows.Count!=1 || (string)marker["word"]!="钛" || Convert.ToSingle(colorResult["r"])!=0.25f || Convert.ToSingle(colorResult["a"])!=1f)throw new Exception("信标颜色查询失败");
Console.WriteLine("通过：真实 Mod 查询器筛选跨行星信标，保留非空文字及真实 Unity Color 的 RGBA。");
