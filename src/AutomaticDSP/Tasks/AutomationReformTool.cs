using System.Reflection;
using UnityEngine;

namespace AutomaticDSP.Tasks
{
    // 原生方法依赖同一工具与工厂中的临时计算结果，调用方必须在同一主线程操作内完成准备和执行。
    internal sealed class AutomationReformTool : BuildTool_Reform
    {
        private static readonly MethodInfo PrepareBrush = typeof(BuildTool_Reform).GetMethod("PrepareBrushPoints", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo FlattenCost = typeof(BuildTool_Reform).GetMethod("CalculateFlattenCost", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo RestoreCost = typeof(BuildTool_Reform).GetMethod("CalculateRestoreCost", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo Flatten = typeof(BuildTool_Reform).GetMethod("DoFlattenExecute", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo Restore = typeof(BuildTool_Reform).GetMethod("DoRestoreExecute", BindingFlags.Instance | BindingFlags.NonPublic);

        public static bool Available => PrepareBrush != null && FlattenCost != null && RestoreCost != null && Flatten != null && Restore != null;

        public void Prepare(Vector3 position)
        {
            PrepareBrush.Invoke(this, new object[] { position });
        }

        public int CalculateSand()
        {
            var args = new object[] { 0.990946f * brushSize, 0, 0 };
            (reformMode == 0 ? FlattenCost : RestoreCost).Invoke(this, args);
            return reformMode == 0
                ? GetNeedSandCountByInc(handItem.ID, cursorPointCount, (int)args[1], (int)args[2])
                : (int)args[1] - (int)args[2];
        }

        public void Apply(int needSand)
        {
            // 保留原生扣料、退款、统计及地形对象更新；仅调用底层地形函数会漏掉这些副作用。
            (reformMode == 0 ? Flatten : Restore).Invoke(this, new object[] { 0.990946f * brushSize, needSand, true });
        }
    }
}
