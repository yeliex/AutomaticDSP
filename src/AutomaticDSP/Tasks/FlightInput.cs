using System;
using UnityEngine;

namespace AutomaticDSP.Tasks
{
    // 只包装原生移动动作的输入窗口；位置、速度、耗能与模式切换仍由原生动作处理。
    internal sealed class FlightInput : IDisposable
    {
        internal readonly Player Player;
        internal string Mode;
        internal Vector3 Direction;
        internal float Thrust;
        internal float Lift;
        internal bool Boost;
        internal bool WarpToggle;
        internal readonly FlightNavigation Navigation;
        internal readonly int Duration;
        internal int AppliedTicks;
        internal string Error;
        private readonly PlayerController controller;
        private readonly int planetId;
        private readonly DateTimeOffset deadline;
        private long lastTick = -1;
        private bool warpPressed;
        private bool disposed;

        internal FlightInput(Player player, string mode, Vector3 direction, float thrust, float lift,
            bool boost, int duration, DateTimeOffset deadline, FlightNavigation navigation = null)
        {
            Player = player;
            controller = player.controller;
            planetId = player.planetId;
            Mode = mode;
            Direction = direction;
            Thrust = thrust;
            Lift = lift;
            Boost = boost;
            Duration = duration;
            this.deadline = deadline;
            Navigation = navigation;
            for (var i = 0; i < controller.actions.Length; i++)
            {
                var action = controller.actions[i];
                if (action == controller.actionWalk || action == controller.actionFly || action == controller.actionSail)
                    controller.actions[i] = new InputAction(this, action);
            }
        }

        public void Dispose()
        {
            disposed = true;
            if (controller == null || controller.actions == null) return;
            for (var i = 0; i < controller.actions.Length; i++)
                if (controller.actions[i] is InputAction wrapper && wrapper.Owner == this)
                    controller.actions[i] = wrapper.Native;
        }

        private bool Prepare(long tick)
        {
            if (disposed || Error != null || (AppliedTicks >= Duration && lastTick != tick)) return false;
            if (DateTimeOffset.UtcNow >= deadline) Error = "timeout";
            else if (GameMain.mainPlayer != Player) Error = "session_changed";
            else if (!Player.isAlive || controller.gameData.disableController) Error = "controller_unavailable";
            if (Error != null) return false;
            var uiOpen = VFInput.inFullscreenGUI || VFInput.inputing || VFInput.inScreenshotMode || UIGame.viewMode >= EViewMode.Globe;
            // 界面按键不代表移动接管；导航仍逐 tick 驱动原生动作，显式订单始终优先。
            if ((Navigation == null && uiOpen) || controller.cmd.type == ECommand.Build ||
                Player.navigation.navigating || (Player.currentOrder != null && !Player.currentOrder.targetReached) ||
                (!uiOpen && (controller.input0.sqrMagnitude > 0 || controller.input1.sqrMagnitude > 0 ||
                VFInput._warpKey || VFInput._sailSpeedUp || VFInput.rtsStop.onDown ||
                (Navigation != null && VFInput._sailLockCursor))))
                Error = "manual_override";
            else if (Navigation == null && Mode != "sail" && Mode != "warp" && Mode != "exitWarp" && Player.planetId != planetId)
                Error = "planet_changed";
            else if (Player.mecha.coreEnergy <= 0 && Mode != "exitWarp") Error = "insufficient_energy";
            if (Error != null) return false;
            if (lastTick != tick)
            {
                if (Navigation != null && Player.movementState == EMovementState.Sail)
                {
                    // 与原生 Tab 解锁相同；起飞进入 Sail 后 UI 会重新启用锁定，因此在导航输入前处理。
                    UIRoot.instance.uiGame.disableLockCursor = true;
                    if (UICursor.locked) UICursor.SetLockCursorDirect(false);
                }
                Navigation?.Update(this, tick);
                if (Error != null || Navigation?.Arrived == true) return false;
                lastTick = tick;
                AppliedTicks++;
            }
            // 固定输入到模式切换即止；导航则按当前模式生成下一帧输入。
            if (Navigation == null && (Mode == "takeOff" ? Player.movementState != EMovementState.Walk :
                Mode == "land" || Mode == "fly" ? Player.movementState != EMovementState.Fly :
                Player.movementState != EMovementState.Sail)) return false;
            return true;
        }

        private void Tick(PlayerAction native, long tick)
        {
            if (!Prepare(tick))
            {
                native.GameTick(tick);
                return;
            }
            var input0 = controller.input0;
            var input1 = controller.input1;
            var ray = controller.fwdRayUDir;
            var shift = VFInput.shift;
            var noModifier = VFInput.noModifier;
            var speedKey = VFInput.override_keys[22];
            var warpKey = VFInput.override_keys[24];
            var warpDown = VFInput.axis_button.down[29];
            var fullscreen = VFInput.inFullscreenGUI;
            var inputing = VFInput.inputing;
            try
            {
                controller.input0 = Vector4.zero;
                controller.input1 = Vector4.zero;
                if (Mode == "takeOff")
                    controller.input0.z = AppliedTicks % 12 == 1 ? 1 : 0;
                else if (Mode == "land") controller.input1.y = -1;
                else if (Mode == "fly")
                {
                    var up = Player.position.normalized;
                    var right = Vector3.Cross(up, controller.mainCamera.transform.forward).normalized;
                    var forward = Vector3.Cross(right, up);
                    var tangent = Vector3.ProjectOnPlane(Direction, up).normalized;
                    controller.input0.x = Vector3.Dot(tangent, right) * Thrust;
                    controller.input0.y = Vector3.Dot(tangent, forward) * Thrust;
                    controller.input1.y = Lift;
                }
                else if (Mode == "sail")
                {
                    controller.input0.y = Thrust;
                    controller.fwdRayUDir = Direction;
                }

                if (native == controller.actionSail)
                {
                    // 仅在原生航行动作内解除界面对合成加速/曲速键的屏蔽；同帧 UI 仍读取原值。
                    if (Navigation != null)
                    {
                        VFInput.inFullscreenGUI = false;
                        VFInput.inputing = false;
                    }
                    // 临时生成等价按键边沿；还原绑定及全局输入，避免影响同帧其他系统。
                    VFInput.override_keys[22] = default;
                    VFInput.override_keys[24] = default;
                    VFInput.shift = Boost;
                    VFInput.noModifier = true;
                    VFInput.axis_button.down[29] = Navigation != null ? WarpToggle :
                        !warpPressed && (Mode == "warp" || (Mode == "exitWarp" && Player.warpCommand));
                    if (VFInput.axis_button.down[29]) warpPressed = true;
                }
                native.GameTick(tick);
                // 对应原生低能量强制退出 Fly 的判定，不能将失去动力报告为完成飞行。
                if (Mode == "fly" && native == controller.actionFly && Player.mecha.coreEnergy < 10000.0 &&
                    controller.movementStateInFrame == EMovementState.Walk)
                    Error = "insufficient_energy";
            }
            finally
            {
                controller.input0 = input0;
                controller.input1 = input1;
                controller.fwdRayUDir = ray;
                VFInput.shift = shift;
                VFInput.noModifier = noModifier;
                VFInput.override_keys[22] = speedKey;
                VFInput.override_keys[24] = warpKey;
                VFInput.axis_button.down[29] = warpDown;
                VFInput.inFullscreenGUI = fullscreen;
                VFInput.inputing = inputing;
            }
        }

        private sealed class InputAction : PlayerAction
        {
            internal readonly FlightInput Owner;
            internal readonly PlayerAction Native;
            internal InputAction(FlightInput owner, PlayerAction native) { Owner = owner; Native = native; }
            public override void GameTick(long time) => Owner.Tick(Native, time);
            public override void Free() { Owner.disposed = true; Native.Free(); }
        }
    }
}
