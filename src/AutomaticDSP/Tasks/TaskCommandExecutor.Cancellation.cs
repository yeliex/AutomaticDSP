namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        public void StopCommandEffects(CommandState command)
        {
            switch (command.NormalizedType)
            {
                case "moveto":
                case "minetarget":
                case "entityfastfillin":
                case "fastfillin":
                case "dismantleentity":
                case "removeentity":
                case "placebuilding":
                case "placebelt":
                case "placesorter":
                    GameMain.mainPlayer?.ClearOrders();
                    return;
            }
        }
    }
}
