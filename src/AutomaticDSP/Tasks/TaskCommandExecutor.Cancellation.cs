namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        public void ExitCommandBuildMode(CommandState command)
        {
            if (!command.EnteredBuildMode) return;
            command.EnteredBuildMode = false;
            var controller = GameMain.mainPlayer?.controller;
            if (controller?.cmd.type == ECommand.Build)
            {
                controller.actionBuild?.Close();
                controller.cmd.SetNoneCommand();
            }
        }

        public void StopCommandEffects(CommandState command)
        {
            command.Flight?.Dispose();
            if (!command.OwnsPlayerOrders) return;
            switch (command.NormalizedType)
            {
                case "moveto":
                case "minetarget":
                case "entityfastfillin":
                case "entityfasttakeout":
                case "transferstorageitem":
                case "fastfillin":
                case "dismantleentity":
                case "removeentity":
                case "placebuilding":
                case "placebelt":
                case "applyfactoryblueprint":
                case "placesorter":
                    GameMain.mainPlayer?.ClearOrders();
                    return;
            }
        }
    }
}
