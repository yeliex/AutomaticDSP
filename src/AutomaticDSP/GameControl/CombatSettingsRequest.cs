namespace AutomaticDSP.GameControl
{
    internal sealed class CombatSettingsRequest
    {
        public float? Aggressiveness { get; set; }

        public float? InitialLevel { get; set; }

        public float? InitialGrowth { get; set; }

        public float? InitialColonize { get; set; }

        public float? MaxDensity { get; set; }

        public float? GrowthSpeedFactor { get; set; }

        public float? PowerThreatFactor { get; set; }

        public float? BattleThreatFactor { get; set; }

        public float? BattleExpFactor { get; set; }
    }
}
