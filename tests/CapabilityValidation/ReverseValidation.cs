using System;
using System.Collections.Generic;
using AutomaticDSP.Serialization;
namespace UnityEngine { public struct Vector3 { public float magnitude=>0;public float sqrMagnitude=>0;public static Vector3 operator -(Vector3 a,Vector3 b)=>new(); public static float Distance(Vector3 a,Vector3 b)=>0; } }
public struct EntityData { public int beltId; public UnityEngine.Vector3 pos; }
public struct BeltComponent { public int entityId,segPathId,outputId,mainInputId; }
public class CargoPath { public List<int> belts = new(); }
public class CargoTraffic {
    public BeltComponent[] beltPool=new BeltComponent[1030]; public CargoPath Path=new();
    public CargoPath GetCargoPath(int id)=>Path;
}
public class BeltWindow {
    public PlanetFactory factory; public CargoTraffic traffic; public Player player; public int beltId,Calls; public bool Throw;
    public void OnReverseButtonClick(int n) { Calls++; if(Throw)throw new InvalidOperationException("native"); traffic.beltPool[beltId].outputId=traffic.beltPool[beltId].mainInputId; }
}
public class Mecha { public int buildArea=80; public Forge forge=new(); }
namespace AutomaticDSP.Tasks {
    internal static class TaskStatusNames { public const string CommandFailed="FAILED",CommandSucceeded="SUCCEEDED"; }
    internal class TaskState {}
    internal sealed partial class TaskCommandExecutor {
        internal string Code,Status; internal object Result;
        private void finishCommand(CommandState c,string status,string code,string message,DateTimeOffset now,object result){Code=code;Status=status;Result=result;}
        private static bool TryGetPlayer(out Player p,out string code,out string message){p=GameMain.mainPlayer;code=message=null;return true;}
        private static bool TryValidateCurrentPlanet(CommandState c,Player p,out string code,out string message){code=message=null;return true;}
        private static bool TryGetTargetEntityId(TaskState t,CommandState c,out int id,out string message){id=1;message=null;return true;}
        private static bool TryGetEntity(PlanetFactory f,int id,out EntityData e){e=new(){beltId=1};return true;}
        private static object RangeFailureResult(float d,float r,UnityEngine.Vector3 p)=>null;
        internal static void ValidateReverse() {
            var factory=new PlanetFactory();GameMain.localPlanet.factory=factory;
            var window=UIRoot.instance.uiGame.beltWindow;
            var previous=new PlanetFactory();window.factory=previous;window.beltId=77;
            foreach(var count in new[]{1,2,1023,1024}) {
                var traffic=factory.cargoTraffic;traffic.Path.belts.Clear();
                for(var i=1;i<=count;i++){traffic.Path.belts.Add(i);traffic.beltPool[i]=new(){entityId=i,mainInputId=9};}
                var before=window.Calls;var executor=new TaskCommandExecutor();executor.ExecuteReverseBeltLocked(new(),new(),DateTimeOffset.UtcNow);
                var allowed=count==2||count==1023;
                Program.Check(allowed ? executor.Status=="SUCCEEDED" && window.Calls==before+1 : executor.Code=="invalid_belt_path"&&window.Calls==before,"反向传送带长度边界 "+count);
                Program.Check(ReferenceEquals(window.factory,previous)&&window.beltId==77,"原生反向回调恢复 UI 上下文 "+count);
            }
            factory.cargoTraffic.Path.belts=new(){1,2};window.Throw=true;
            try{new TaskCommandExecutor().ExecuteReverseBeltLocked(new(),new(),DateTimeOffset.UtcNow);}catch(InvalidOperationException){}
            Program.Check(ReferenceEquals(window.factory,previous)&&window.beltId==77,"原生反向回调异常也恢复上下文");
        }
    }
}
public struct PrebuildData { public int id; public UnityEngine.Vector3 pos; }
public class Forge { public System.Collections.Generic.List<ForgeTask> tasks=new(); public void CancelTask(int index){} }
public class ForgeTask {public int recipeId;}
namespace AutomaticDSP.Tasks {
    internal sealed partial class TaskCommandExecutor {
        private static bool GetBool(CommandState c,string name,bool fallback)=>fallback;
        private static bool TryGetInt(CommandState c,string name,out int value){value=name=="itemId"?c.ItemId:c.TargetId;return true;}
        private static bool IsWithinCommandIssueRange(Player p,UnityEngine.Vector3 pos,out float distance,out float range){distance=0;range=80;return true;}
        internal static void ValidatePrebuild() {
            var f=GameMain.localPlanet.factory;f.prebuildPool=new PrebuildData[3];
            var build=GameMain.mainPlayer.controller.actionBuild;var exec=new TaskCommandExecutor();
            exec.ExecuteCancelPrebuild(new(){TargetId=1},DateTimeOffset.UtcNow);
            Program.Check(exec.Code=="target_not_found"&&build.Dismantled==0,"落成后预建槽为空，取消不会调用实体拆除或退款");
            f.prebuildPool[1]=new(){id=1};exec.ExecuteCancelPrebuild(new(){TargetId=1},DateTimeOffset.UtcNow);
            Program.Check(exec.Status=="SUCCEEDED"&&build.Dismantled==-1,"有效预建仅按负对象 ID 交给原生取消");
            build.Succeeds=false;exec.ExecuteCancelPrebuild(new(){TargetId=1},DateTimeOffset.UtcNow);
            Program.Check(exec.Code=="dismantle_failed","原生拒绝取消不伪造成功");
        }
    }
}
