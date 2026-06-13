namespace AutomaticDSP.GameControl
{
    internal sealed class NewGameRequest
    {
        public int? GalaxyAlgo { get; set; }

        public int? GalaxySeed { get; set; }

        public int? StarCount { get; set; }

        public int? PlayerProto { get; set; }

        public float? ResourceMultiplier { get; set; }

        public string Mode { get; set; }

        public bool? IsPeaceMode { get; set; }

        public bool? PeaceMode { get; set; }

        public bool? IsCombatMode { get; set; }

        public bool? CombatMode { get; set; }

        public bool? IsSandboxMode { get; set; }

        public bool? Sandbox { get; set; }

        public bool? SkipPrologue { get; set; }

        public string GoalLevel { get; set; }

        public CombatSettingsRequest CombatSettings { get; set; }
    }
}
