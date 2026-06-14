namespace AutomaticDSP.Tasks
{
    internal static class TaskCommandType
    {
        public static string Normalize(string value)
        {
            return value.ToLowerInvariant();
        }
    }
}
