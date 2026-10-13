using System;
using System.Collections.Generic;

namespace AutomaticDSP.Tasks
{
    // 原生移除事件先于预建 ID 回收；记录生命周期，避免旧蓝图取消新建的同址同型号对象。
    internal sealed class ConstructionTracker : IDisposable
    {
        private object gameData;
        public object Session { get; private set; } = new object();
        private readonly Dictionary<PlanetFactory, Dictionary<int, BuildWaitTarget>> targets = new Dictionary<PlanetFactory, Dictionary<int, BuildWaitTarget>>();
        private readonly Dictionary<PlanetFactory, Action<int, int>> callbacks = new Dictionary<PlanetFactory, Action<int, int>>();

        public void Refresh()
        {
            if (ReferenceEquals(gameData, GameMain.data)) return;
            Dispose();
            gameData = GameMain.data;
            Session = new object();
        }

        public void Track(CommandState command)
        {
            Refresh();
            var factory = GameMain.galaxy?.PlanetById(command.BuildPlanetId)?.factory;
            if (factory == null || command.BuildTargets == null) return;
            foreach (var target in command.BuildTargets)
            {
                if (!target.CreatedPrebuild || target.PrebuildRemoved) continue;
                if (!targets.TryGetValue(factory, out var byId))
                {
                    byId = new Dictionary<int, BuildWaitTarget>();
                    targets.Add(factory, byId);
                    Action<int, int> callback = (id, change) =>
                    {
                        if (change != -1 || !byId.TryGetValue(id, out var removed)) return;
                        removed.PrebuildRemoved = true;
                        byId.Remove(id);
                    };
                    callbacks.Add(factory, callback);
                    factory.onPrebuildChange += callback;
                }
                byId[-target.ObjectId] = target;
            }
        }

        public void Dispose()
        {
            foreach (var pair in callbacks) pair.Key.onPrebuildChange -= pair.Value;
            callbacks.Clear();
            targets.Clear();
            gameData = null;
        }
    }
}
