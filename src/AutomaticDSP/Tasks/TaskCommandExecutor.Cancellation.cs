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
            if (command.BatchCommands != null && command.BatchIndex < command.BatchCommands.Count)
            {
                var child = command.BatchCommands[command.BatchIndex];
                StopCommandEffects(child);
                ExitCommandBuildMode(child);
                foreach (var item in command.BatchCommands)
                    if (item.Status == TaskStatusNames.CommandRunning || item.Status == TaskStatusNames.CommandPending)
                        item.Status = TaskStatusNames.CommandCancelled;
                command.Result = BatchResult(command);
            }
            command.Flight?.Dispose();
            if (!command.OwnsPlayerOrders) return;
            switch (command.NormalizedType)
            {
                case "moveto":
                    if (command.NativeMoveOrder != null &&
                        ReferenceEquals(GameMain.mainPlayer?.currentOrder, command.NativeMoveOrder))
                        GameMain.mainPlayer.ClearOrders();
                    return;
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
