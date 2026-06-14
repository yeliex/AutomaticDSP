using System;
using System.Collections.Generic;
using System.Text;
using AutomaticDSP.Tasks;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace AutomaticDSP.UI
{
    internal sealed class TaskStatusOverlay : IDisposable
    {
        private const float RefreshInterval = 0.25f;
        private const int MaxTasks = 1;
        private const int MaxCommandsPerTask = 8;

        private readonly TaskQueueService taskQueueService;
        private readonly ManualLogSource log;
        private readonly CombineKey visibilityToggleKey = new CombineKey((int)KeyCode.F8, 0, ECombineKeyAction.OnceClick, false);
        private GameObject root;
        private Text titleText;
        private Text bodyText;
        private float nextRefreshTime;
        private bool isVisible = true;

        public TaskStatusOverlay(TaskQueueService taskQueueService, ManualLogSource log)
        {
            this.taskQueueService = taskQueueService;
            this.log = log;
        }

        public void Update()
        {
            HandleVisibilityShortcut();

            if (Time.unscaledTime < nextRefreshTime)
            {
                return;
            }

            nextRefreshTime = Time.unscaledTime + RefreshInterval;
            EnsureCreated();
            if (root == null)
            {
                return;
            }

            var snapshots = taskQueueService.GetOverlaySnapshots(MaxTasks, MaxCommandsPerTask);
            root.SetActive(isVisible && snapshots.Count > 0);
            if (snapshots.Count == 0)
            {
                return;
            }

            titleText.text = "AutomaticDSP 指令";
            bodyText.text = BuildBodyText(snapshots);
        }

        public void Dispose()
        {
            if (root != null)
            {
                UnityEngine.Object.Destroy(root);
                root = null;
            }
        }

        private void EnsureCreated()
        {
            if (root != null)
            {
                return;
            }

            var parent = FindHudParent();
            if (parent == null)
            {
                return;
            }

            root = new GameObject("AutomaticDSP Task Status Overlay", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            root.transform.SetParent(parent, false);
            root.transform.SetAsLastSibling();

            var rect = (RectTransform)root.transform;
            rect.sizeDelta = new Vector2(430f, 210f);
            PlaceAbovePlanetGlobe(rect);

            var background = root.GetComponent<Image>();
            background.color = new Color(0.02f, 0.05f, 0.08f, 0.54f);
            background.raycastTarget = false;

            titleText = CreateText("Title", root.transform, 14, FontStyle.Bold, new Color(0.78f, 0.92f, 1f, 0.9f));
            var titleRect = (RectTransform)titleText.transform;
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = new Vector2(0f, -10f);
            titleRect.sizeDelta = new Vector2(-20f, 22f);

            bodyText = CreateText("Body", root.transform, 12, FontStyle.Normal, new Color(0.9f, 0.96f, 1f, 0.8f));
            var bodyRect = (RectTransform)bodyText.transform;
            bodyRect.anchorMin = new Vector2(0f, 0f);
            bodyRect.anchorMax = new Vector2(1f, 1f);
            bodyRect.pivot = new Vector2(0.5f, 0.5f);
            bodyRect.anchoredPosition = new Vector2(0f, -18f);
            bodyRect.sizeDelta = new Vector2(-20f, -46f);

            root.SetActive(false);
            log?.LogInfo("AutomaticDSP task status overlay initialized.");
        }

        private void HandleVisibilityShortcut()
        {
            if (VFInput.inputing)
            {
                return;
            }

            if (visibilityToggleKey.GetKeyDown())
            {
                isVisible = !isVisible;
                if (!isVisible && root != null)
                {
                    root.SetActive(false);
                }
            }
        }

        private static void PlaceAbovePlanetGlobe(RectTransform rect)
        {
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot = new Vector2(0f, 0f);
            rect.anchoredPosition = new Vector2(0f, PlanetGlobeTopOffset(rect));
        }

        private static float PlanetGlobeTopOffset(RectTransform rect)
        {
            var rootInstance = UIRoot.instance;
            var globeRect = rootInstance?.uiGame?.planetGlobe == null
                ? null
                : rootInstance.uiGame.planetGlobe.transform as RectTransform;
            var parentRect = rect.parent as RectTransform;
            if (globeRect == null || parentRect == null)
            {
                return 174f;
            }

            // 把面板底边贴到星球视图顶边，避免覆盖左下角原生 HUD。
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(parentRect, globeRect);
            return Mathf.Max(0f, bounds.max.y - parentRect.rect.yMin);
        }

        private static Text CreateText(string name, Transform parent, int fontSize, FontStyle fontStyle, Color color)
        {
            var gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            gameObject.transform.SetParent(parent, false);

            var text = gameObject.GetComponent<Text>();
            text.font = FindGameFont();
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.color = color;
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            text.supportRichText = true;
            return text;
        }

        private static Transform FindHudParent()
        {
            // 挂在低层 HUD，保证科技树、星图和普通窗口仍能盖住这个叠层。
            var rootInstance = UIRoot.instance;
            if (rootInstance != null && rootInstance.uiGame != null && rootInstance.uiGame.lowGroup != null)
            {
                return rootInstance.uiGame.lowGroup;
            }

            return null;
        }

        private static Font FindGameFont()
        {
            var texts = Resources.FindObjectsOfTypeAll<Text>();
            foreach (var text in texts)
            {
                if (text != null && text.font != null)
                {
                    return text.font;
                }
            }

            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        private static string BuildBodyText(List<TaskOverlaySnapshot> snapshots)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < snapshots.Count; i++)
            {
                var task = snapshots[i];
                if (i > 0)
                {
                    builder.AppendLine();
                }

                var label = string.IsNullOrWhiteSpace(task.ClientRequestId) ? task.Id : task.ClientRequestId;
                builder.Append("<color=#B8DFFFCC>");
                builder.Append(FormatTimestamp(task.CreatedAt));
                builder.Append(" ");
                builder.Append(Escape(label));
                builder.Append("  ");
                builder.Append(Escape(DisplayStatus(task.Status)));
                if (task.QueueIndex >= 0)
                {
                    builder.Append(" 队列 #");
                    builder.Append(task.QueueIndex);
                }

                builder.Append("</color>");
                builder.AppendLine();

                foreach (var command in task.Commands)
                {
                    builder.Append("  ");
                    builder.Append("<color=#8FBACC99>");
                    builder.Append(FormatTimestamp(command.Timestamp));
                    builder.Append("</color> ");
                    builder.Append(StatusBullet(command.Status));
                    builder.Append(" ");
                    builder.Append(Escape(command.Id));
                    builder.Append(" <color=#D8E7F0AA>[");
                    builder.Append(Escape(command.Type));
                    builder.Append("]</color> ");
                    builder.Append(StatusText(command));
                    builder.AppendLine();
                }

                if (task.CommandCount > task.Commands.Count)
                {
                    builder.Append("  ... 还有 ");
                    builder.Append(task.CommandCount - task.Commands.Count);
                    builder.AppendLine(" 条未显示");
                }
            }

            return builder.ToString();
        }

        private static string StatusBullet(string status)
        {
            switch (status)
            {
                case TaskStatusNames.CommandSucceeded:
                    return "<color=#9AF0B2CC>●</color>";
                case TaskStatusNames.CommandFailed:
                case TaskStatusNames.CommandCancelled:
                    return "<color=#FF9B93CC>●</color>";
                case TaskStatusNames.CommandRunning:
                    return "<color=#FFE08ACC>●</color>";
                case TaskStatusNames.CommandSkipped:
                    return "<color=#AAB3BCCC>●</color>";
                default:
                    return "<color=#B8DFFF99>●</color>";
            }
        }

        private static string StatusText(CommandOverlaySnapshot command)
        {
            var status = Escape(DisplayStatus(command.Status));
            if (!string.IsNullOrWhiteSpace(command.Phase))
            {
                status += "/" + Escape(DisplayPhase(command.Phase));
            }

            if (!string.IsNullOrWhiteSpace(command.ErrorCode))
            {
                status += " " + Escape(command.ErrorCode);
            }

            return status;
        }

        private static string FormatTimestamp(DateTimeOffset timestamp)
        {
            return timestamp.ToLocalTime().ToString("HH:mm:ss");
        }

        private static string FormatTimestamp(DateTimeOffset? timestamp)
        {
            return timestamp.HasValue ? FormatTimestamp(timestamp.Value) : "--:--:--";
        }

        private static string DisplayStatus(string status)
        {
            switch (status)
            {
                case TaskStatusNames.TaskQueued:
                case TaskStatusNames.CommandPending:
                    return "待执行";
                case TaskStatusNames.TaskRunning:
                    return "执行中";
                case TaskStatusNames.TaskSucceeded:
                    return "完成";
                case TaskStatusNames.TaskFailed:
                    return "失败";
                case TaskStatusNames.TaskCancelRequested:
                    return "取消中";
                case TaskStatusNames.TaskCancelled:
                    return "已取消";
                case TaskStatusNames.CommandSkipped:
                    return "已跳过";
                default:
                    return status ?? string.Empty;
            }
        }

        private static string DisplayPhase(string phase)
        {
            switch (phase)
            {
                case "validating":
                    return "校验";
                case "completed":
                    return "完成";
                case "cancelled":
                    return "取消";
                case "skipped":
                    return "跳过";
                case "failed":
                    return "失败";
                default:
                    return phase ?? string.Empty;
            }
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
