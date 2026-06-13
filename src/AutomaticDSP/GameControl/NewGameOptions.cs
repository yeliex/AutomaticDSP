namespace AutomaticDSP.GameControl
{
    internal sealed class NewGameOptions
    {
        public int GalaxyAlgo { get; set; }

        public int GalaxySeed { get; set; }

        public int StarCount { get; set; }

        public int PlayerProto { get; set; }

        public float ResourceMultiplier { get; set; }

        public bool IsPeaceMode { get; set; }

        public bool IsSandboxMode { get; set; }

        public bool SkipPrologue { get; set; }

        public CombatSettings CombatSettings { get; set; }

        public EGoalLevel GoalLevel { get; set; }
    }
}
