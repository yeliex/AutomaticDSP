using System;
using System.Collections.Generic;
using System.Reflection;

namespace AutomaticDSP.Tasks
{
    // 地基费用与执行共享原生临时数据，不能用独立的网格估算或直接改地形替代。
    internal sealed class AutomationBlueprintTool : BuildTool_BlueprintPaste
    {
        private static readonly MethodInfo Calculate = typeof(BuildTool_BlueprintPaste).GetMethod("CalculateReformData", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo Apply = typeof(BuildTool_BlueprintPaste).GetMethod("DetermineReforms", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ReformGrids = typeof(BuildTool_BlueprintPaste).GetField("reformGridIds", BindingFlags.Instance | BindingFlags.NonPublic);

        public static bool Available => Calculate != null && Apply != null && ReformGrids != null;

        public void PrepareInventory()
        {
            tmpPackage = new StorageComponent(player.package.size);
            Array.Copy(player.package.grids, tmpPackage.grids, tmpPackage.size);
            tmpInhandId = player.inhandItemId;
            tmpInhandCount = player.inhandItemCount;
            highlightItems = new Dictionary<int, uint>();
            ReformGrids.SetValue(this, new HashSet<int>());
        }

        public void CalculateReforms() => Calculate.Invoke(this, null);

        public bool ApplyReforms() => (bool)Apply.Invoke(this, null);
    }
}
