using System;

namespace AutomaticDSP.GameControl
{
    internal sealed class InserterTechEffectRepair : IDisposable
    {
        private GameHistoryData history;

        internal void Update(GameHistoryData current)
        {
            if (ReferenceEquals(history, current)) return;
            Dispose();
            history = current;
            if (history == null) return;
            history.onTechUnlocked += OnTechUnlocked;
            Reconcile(history);
        }

        private void OnTechUnlocked(int techId, int level, bool unlockedDirect)
        {
            if (techId == 3306 || techId == 3311 || techId == 3313 || techId == 3315)
                Reconcile(history);
        }

        public void Dispose()
        {
            if (history != null) history.onTechUnlocked -= OnTechUnlocked;
            history = null;
        }

        internal static void Reconcile(GameHistoryData history)
        {
            if (history == null || !history.TechUnlocked(3306, true)) return;

            // 旧科技 3306 的原生函数 39 会把输出覆盖为 1；只恢复已完成的集装改良效果。
            var output = history.inserterStackOutput;
            TechProto strongest = null;
            double unlockValue = 0;
            for (var techId = 3311; techId <= 3315; techId += 2)
            {
                if (!history.TechUnlocked(techId, true)) continue;
                var tech = LDB.techs.Select(techId);
                if (tech == null) continue;
                for (var i = 0; i < tech.UnlockFunctions.Length; i++)
                {
                    if (tech.UnlockFunctions[i] != 39) continue;
                    var value = tech.UnlockValues[i];
                    var count = (int)(value > 0 ? value + 0.5 : value - 0.5);
                    if (count <= output) continue;
                    output = count;
                    strongest = tech;
                    unlockValue = value;
                }
            }

            // 不重放整项科技，避免重复奖励或累加效果；原生方法同时同步已有分拣器。
            if (strongest != null)
                history.UnlockTechFunction(39, unlockValue, history.TechState(strongest.ID).curLevel);
        }
    }
}
