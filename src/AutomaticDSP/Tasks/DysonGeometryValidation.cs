using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace AutomaticDSP.Tasks
{
    internal static class DysonGeometryValidation
    {
        private static readonly MethodInfo NodeCheck = typeof(UIDysonBrush_Node).GetMethod("CheckCondition", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo NodeCollisions = typeof(UIDysonBrush_Node).GetMethod("RecalcCollides", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo NodeCollided = typeof(UIDysonBrush_Node).GetField("collideWithOtherNodes", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo FrameCheck = typeof(UIDysonBrush_Frame).GetMethod("CheckCondition", BindingFlags.Instance | BindingFlags.NonPublic);

        public static string CheckNode(DysonSphereLayer layer, Vector3 direction)
        {
            if (Mathf.RoundToInt(Mathf.Asin(Mathf.Clamp01(Mathf.Abs(direction.y))) * Mathf.Rad2Deg) >
                Mathf.RoundToInt(GameMain.history.dysonNodeLatitude)) return "StressExceed";
            if (NodeCheck == null || NodeCollisions == null || NodeCollided == null) return "NativeValidatorUnavailable";
            // 使用独立原生笔刷执行私有几何校验，避免改动玩家正在使用的编辑器与缓存。
            var host = new GameObject("AutomaticDSP.DysonNodeValidation");
            try
            {
                var brush = host.AddComponent<UIDysonBrush_Node>();
                brush.layer = layer;
                NodeCollisions.Invoke(brush, new object[] { direction });
                var condition = (UIDysonBrush_Node.BuildCondition)NodeCheck.Invoke(brush, new object[] { direction });
                if (condition != UIDysonBrush_Node.BuildCondition.Ok) return condition.ToString();
                return (bool)NodeCollided.GetValue(brush) ? "TooClose" : "Ok";
            }
            finally { UnityEngine.Object.Destroy(host); }
        }

        public static string CheckFrame(DysonSphereLayer layer, DysonNode a, DysonNode b, bool euler)
        {
            if (a == b) return "TooClose";
            if (DysonNode.FrameBetween(a, b) != null) return "Exist";
            if (FrameCheck == null) return "NativeValidatorUnavailable";
            var host = new GameObject("AutomaticDSP.DysonFrameValidation");
            try
            {
                var brush = host.AddComponent<UIDysonBrush_Frame>();
                brush.layer = layer;
                brush.isEuler = euler;
                return ((UIDysonBrush_Frame.BuildCondition)FrameCheck.Invoke(brush,
                    new object[] { a.pos.normalized, b.pos.normalized, a.id, b.id })).ToString();
            }
            finally { UnityEngine.Object.Destroy(host); }
        }

        public static string CheckShell(DysonSphereLayer layer, List<DysonNode> nodes)
        {
            var center = Vector3.zero;
            foreach (var node in nodes) center += node.pos;
            center.Normalize();
            var polygon = new List<VectorLF3>();
            for (var i = 0; i < nodes.Count; i++)
            {
                // 原生壳面笔刷同样限制闭环相对中心的角跨度。
                if ((center - nodes[i].pos.normalized).sqrMagnitude > 0.26832402f * 0.6f) return "CycleTooLarge";
                var frame = DysonNode.FrameBetween(nodes[i], nodes[(i + 1) % nodes.Count]);
                if (frame == null) return "NoCycle";
                var segments = frame.GetSegments();
                if (frame.nodeA == nodes[i])
                    for (var j = 0; j < segments.Count - 1; j++) polygon.Add(segments[j]);
                else
                    for (var j = segments.Count - 1; j > 0; j--) polygon.Add(segments[j]);
            }
            // 临时对象仅复用原生点在壳面内的判定，不挂入任何池、不初始化网格或建造点数。
            var region = new DysonShell(layer) { polygon = polygon };
            if (!region.IsPointInShell(center)) return "NoCycle";
            for (var i = 1; i < layer.nodeCursor; i++)
            {
                var node = layer.nodePool[i];
                if (node != null && node.id == i && !nodes.Contains(node) && region.IsPointInShell(node.pos)) return "OthersInCycle";
            }
            for (var i = 1; i < layer.shellCursor; i++)
            {
                var shell = layer.shellPool[i];
                if (shell == null || shell.id != i) continue;
                if (shell.nodes.TrueForAll(nodes.Contains)) return "Exist";
            }
            return "Ok";
        }
    }
}
