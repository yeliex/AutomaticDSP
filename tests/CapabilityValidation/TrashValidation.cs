using System; using AutomaticDSP.Serialization;
public class ItemSet { public ItemProto Select(int id)=>new(); }
public class ItemProto {public int StackSize=100;}
public class Package { public int GetItemCount(int id)=>0;public int TakeItem(int id,int n,out int inc){inc=0;return 0;} }
public class ActionPick {public bool canPick=true;public System.Collections.Generic.Dictionary<int,int> pickFilters=new();}
public static class VFInput {public static bool inFullscreenGUI,onGUI,onGUIOperate;}
public struct TrashObject {public int item,count,inc,expire;public UnityEngine.Vector3 rPos;}
public struct TrashData {public int landPlanetId;}
public class TrashContainer {public int trashCursor=1;public TrashObject[] trashObjPool=new TrashObject[1];public TrashData[] trashDataPool=new TrashData[1];}
public class TrashSystem {public TrashContainer container=new();public void ClearAllTrash(){} }
public class Scenario {public int Picks;public void NotifyOnTrashPicking(){Picks++;}}
namespace UnityEngine {public static class Mathf {public static float Clamp(float n,float min,float max)=>Math.Clamp(n,min,max);}}
namespace AutomaticDSP.Tasks {
    internal sealed partial class TaskCommandExecutor {
        private static bool TryGetString(CommandState c,string name,out string s){s="all";return true;}
        internal static void ValidateTrash() {
            GameMain.data=new();GameMain.localPlanet.id=102;var box=GameMain.data.trashSystem.container;
            box.trashObjPool[0]=new(){item=1014,count=1,expire=-1};box.trashDataPool[0]=new(){landPlanetId=104};
            var exec=new TaskCommandExecutor();var c=new CommandState(){NormalizedType="pickuptrash",TargetId=0,ItemId=1014};
            exec.ExecuteTrashCommand(c,DateTimeOffset.UtcNow);
            Program.Check(exec.Code=="out_of_range"&&box.trashObjPool[0].expire==-1,"异星垃圾即使相对坐标很近也拒绝，未启动吸取");
            box.trashDataPool[0]=new(){landPlanetId=102};exec.ExecuteTrashCommand(c,DateTimeOffset.UtcNow);
            Program.Check(exec.Status=="SUCCEEDED"&&box.trashObjPool[0].expire==35&&((JsonObject)exec.Result)["phase"].Equals("pickupStarted"),"垃圾入口只启动原生吸取，不伪造已入包或改写背包");
            exec.ExecuteTrashCommand(c,DateTimeOffset.UtcNow);
            Program.Check(exec.Code=="pickup_in_progress","正在吸取的垃圾拒绝重复开始");
        }
    }
}
