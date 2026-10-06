using System;
using System.Collections.Generic;
using AutomaticDSP.Tasks;
namespace UnityEngine { public struct Vector3 {} }
public class ForgeTask {}
public class BuildPreview { public void Clone(BuildPreview other) {} }
public enum EObjectType { None }
public static class GameMain { public static long gameTick; }
namespace BepInEx.Logging { public class ManualLogSource { public void LogWarning(object message) { throw new Exception(message.ToString()); } } }
namespace AutomaticDSP.Storage { public class HistoryStore { public void InsertCommandHistory(params object[] args) {} } }
namespace AutomaticDSP.Tasks
{
    internal delegate void TaskCommandFinisher(CommandState c,string status,string code,string message,DateTimeOffset now,object result);
    internal sealed class TaskCommandExecutor
    {
        public static Action<CommandState,TaskCommandFinisher,DateTimeOffset> Behavior;
        public static readonly List<string> Stops=new List<string>();
        private readonly TaskCommandFinisher finish;
        public TaskCommandExecutor(TaskCommandFinisher finish) {this.finish=finish;}
        public void Execute(TaskState task,CommandState c,DateTimeOffset now) => Behavior(c,finish,now);
        public bool AreEntityReferencesReady(TaskState task,CommandState c,DateTimeOffset now) => c.Id != "connect" || Program.EntityReady;
        public void ExitCommandBuildMode(CommandState c) {c.EnteredBuildMode=false;}
        public void StopCommandEffects(CommandState c) {if(c.OwnsPlayerOrders)Stops.Add(c.Id);}
        public static object FlightResult(FlightInput input)=>null; public object TimeoutResult(CommandState c)=>null;
        public static bool CanExecuteImmediately(CommandState c)=>!new[]{"moveTo","placeBuilding"}.Contains(c.Type);
    }
}
namespace AutomaticDSP.Tasks { internal sealed class FlightInput { public void Dispose() {} } }
