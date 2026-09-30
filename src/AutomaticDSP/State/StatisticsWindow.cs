using System;

namespace AutomaticDSP.State
{
    // 对齐 UIStatisticsWindow 的时间层级和环形历史；这里只转换统计，不推进模拟。
    internal static class StatisticsWindow
    {
        internal static readonly int[] Seconds = { 60, 600, 3600, 36000, 360000 };
        private static readonly int[] ProductTicks = { 1, 6, 60, 360, 3600, 36000 };
        private static readonly int[] TrafficTicks = { 10, 60, 600, 3600, 36000, 360000 };
        private static readonly int[] TrafficRecordTicks = { 0, 6, 60, 360, 3600, 36000 };

        internal static int HistoryLevel(int timeLevel, long tick)
        {
            if (timeLevel != 5) return timeLevel;
            for (var i = 0; i < 4; i++)
                if (tick <= Seconds[i] * 60L) return i;
            return 4;
        }

        internal static double? PerMinute(long total, int timeLevel) =>
            timeLevel == 5 ? (double?)null : total * 60.0 / Seconds[timeLevel];

        internal static long RecentPower(long[] energy, int cursor)
        {
            long value = 0;
            for (var i = 1; i <= 60; i++) value += energy[(cursor - i + 600) % 600];
            return value;
        }

        internal static int SampleTicks(int historyLevel, bool traffic) =>
            (traffic ? TrafficTicks : ProductTicks)[historyLevel + 1];

        internal static long[] ProductHistory(int[] count, int[] cursor, int historyLevel, int group, long tick, bool traffic)
        {
            var length = traffic ? 60 : 600;
            var level = historyLevel + 1;
            var offset = level + group * 6;
            var values = new long[length];
            for (var i = 1; i < length; i++)
                values[i - 1] = count[offset * length + (cursor[offset] - offset * length + i) % length];
            // 面板最后一点由更细层级的已记录数据组成，不能直接读当前粗粒度槽。
            if (traffic)
            {
                var interval = TrafficTicks[level];
                var boundary = tick / interval * interval + TrafficRecordTicks[level];
                if (boundary > tick) boundary -= interval;
                var sampleTick = tick;
                var lower = level - 1;
                var back = 0;
                while (lower >= 0)
                {
                    interval = TrafficTicks[lower];
                    sampleTick = sampleTick / interval * interval + TrafficRecordTicks[lower];
                    if (sampleTick > tick) sampleTick -= interval;
                    // 新存档尚无粗粒度历史时，原生补点会向负 tick 回读；仅计入已存在的时间。
                    if (sampleTick <= 0) break;
                    if (sampleTick <= boundary)
                    {
                        sampleTick += interval;
                        lower--;
                        back = 0;
                        continue;
                    }
                    sampleTick -= interval;
                    offset = lower + group * 6;
                    values[length - 1] += count[offset * length + (cursor[offset] - offset * length - back - 1 + length) % length];
                    back++;
                }
            }
            else
            {
                var remainder = (int)(tick % ProductTicks[level]);
                for (var lower = level - 1; lower >= 0 && remainder > 0; lower--)
                {
                    var samples = remainder / ProductTicks[lower];
                    remainder %= ProductTicks[lower];
                    offset = lower + group * 6;
                    for (var back = 1; back <= samples; back++)
                        values[length - 1] += count[offset * length + (cursor[offset] - offset * length - back + length) % length];
                }
            }
            return values;
        }

        internal static long[] PowerHistory(long[] energy, int[] cursor, int historyLevel)
        {
            var offset = (historyLevel + 1) * 600;
            var values = new long[600];
            for (var i = 0; i < values.Length; i++)
                values[i] = energy[offset + (cursor[historyLevel + 1] - offset + i) % 600];
            return values;
        }
    }
}
