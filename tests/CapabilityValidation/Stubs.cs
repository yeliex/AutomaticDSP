using System;
using System.Collections.Generic;

// 只隔离原生状态与回调，不复制被测 Mod 的权限、提示或取消逻辑，不模拟游戏物理。
public struct VectorLF3
{
    public double x;
    public double magnitude => Math.Abs(x);
    public static VectorLF3 operator -(VectorLF3 a, VectorLF3 b) => new() { x = a.x - b.x };
}
public enum EPlanetType { Desert, Gas }
public class StarData { }
public class PlanetFactory { public PlanetData planet; public CargoTraffic cargoTraffic=new(); public PrebuildData[] prebuildPool; }
public class PlanetData
{
    public int id; public float realRadius; public StarData star;
    public VectorLF3 uPosition;
    public PlanetFactory factory;
    public bool scanned, scanning;
    public EPlanetType type;
    public int waterItemId;
    public int[] gasItems = Array.Empty<int>();
    public float[] gasSpeeds = Array.Empty<float>();
    public void RunScanThread() { scanning = true; }
    public bool SummarizeVeinAmountsByFilter(ref long[] amounts, HashSet<int> hashes, int filter)
    { amounts = new long[1]; return true; }
    public bool SummarizeVeinCountsByFilter(ref int[] counts, HashSet<int> hashes, int filter)
    { counts = new int[1]; return true; }
}
public static class LDB { public static ItemSet items=new(); public static VeinSet veins = new(); }
public class VeinSet { public Vein[] dataArray = Array.Empty<Vein>(); }
public class Vein { public int ID, MiningItem; }
public class GameHistory { public int universeObserveLevel; }
public class GameData { public TrashSystem trashSystem=new(); }
public static class GameMain
{
    public static Scenario gameScenario=new(); public static GameData data;
    public static PlanetData localPlanet;
    public static StarData localStar;
    public static Player mainPlayer = new();
    public static GameHistory history = new();
}
public class Player
{
    public VectorLF3 uPosition;
    public UnityEngine.Vector3 position; public Mecha mecha=new(); public PlayerController controller = new();
    public bool isAlive=true;public int inhandItemId;public Package package=new();public void ThrowTrash(int id,int count,int inc,int n){} public int Cleared;
    public void ClearOrders() { Cleared++; }
}
public enum ECommand { None, Move, Build }
public class Command { public ECommand type; public void SetNoneCommand() { type = ECommand.None; } }
public class BuildAction { public int Dismantled; public bool Succeeds=true; public bool DoDismantleObject(int id){Dismantled=id;return Succeeds;} public int Closed; public void Close() { Closed++; } }
public class PlayerController { public ActionPick actionPick=new(); public Command cmd = new(); public BuildAction actionBuild = new(); }
namespace AutomaticDSP.Tasks
{
    internal class FlightInput { public bool Disposed; public void Dispose() { Disposed = true; } }
    internal class CommandState
    {
        public bool EnteredBuildMode, OwnsPlayerOrders;
        public int ItemId;public int TargetId; public string NormalizedType;
        public FlightInput Flight;
    }
}
namespace UnityEngine { public static class Time { public static float realtimeSinceStartup; } }
namespace UnityEngine.UI { public class Text { public string text; } }
public class UIRoot { public static UIRoot instance = new(); public UIGame uiGame = new(); }
public class UIGame
{
    public BeltWindow beltWindow=new();
    public UIResearchResultWindow researchResultTip = new();
    public TutorialWindow tutorialWindow;
    public TutorialTip tutorialTip;
    public AdvisorTip advisorTip;
    public GoalPanel goalPanel = new();
    public void CloseTutorialWindow() { tutorialWindow.active = false; }
}
public class UIResearchResultWindow
{
    private int techId;
    public bool active;
    public UnityEngine.UI.Text conclusionText = new(), functionText = new();
    public int Closed;
    public void Show(int id, string text) { techId = id; active = true; conclusionText.text = text; }
    public void _Close() { active = false; Closed++; }
}
public class TextGroup { public T[] GetComponentsInChildren<T>() => Array.Empty<T>(); }
public class TutorialWindow { public bool active; public int tutorialId; public TextGroup contentGroup; }
public class TutorialTip { public List<TutorialEntry> entryShowed = new(); }
public class TutorialEntry
{
    public bool active; public int tutorialId, functionId; public UnityEngine.UI.Text nameText;
    public void OnCloseButtonClick() { active = false; }
}
public class AdvisorProto { public int ID; }
public class AdvisorTip
{
    public bool active; public AdvisorProto playingTip; public UnityEngine.UI.Text tipText;
    public void FadeOutAndStop() { active = false; }
}
public class GoalData { public int stage; }
public class GoalEntry
{
    public bool active; public int protoId; public UnityEngine.UI.Text nameText; public GoalData goalData;
}
public class GoalGroup : GoalEntry { public List<GoalEntry> goalInfoEntries = new(); }
public class GoalPanel { public List<GoalGroup> goalGroups = new(); }
