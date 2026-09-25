using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AutomaticDSP.Serialization;
using UnityEngine;
using UnityEngine.UI;

namespace AutomaticDSP.UI
{
    // 读取不是确认；原生自行收起的提示也保留，直到 Agent 显式确认。
    internal static class GameNoticeService
    {
        private sealed class Notice
        {
            public int Id;
            public string Key;
            public string Kind;
            public string Text;
            public float Since;
            public bool Visible;
            public bool Acknowledged;
            public Action Close;
            public Func<bool> IsCurrent;
        }

        private static readonly FieldInfo ResearchTechId = typeof(UIResearchResultWindow).GetField("techId", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly List<Notice> Notices = new List<Notice>();
        private static GameData session;
        private static int nextId;
        private static float nextPoll;

        public static void Update()
        {
            if (!ReferenceEquals(session, GameMain.data))
            {
                session = GameMain.data;
                Notices.Clear();
                nextPoll = 0;
            }
            var ui = UIRoot.instance?.uiGame;
            if (session == null || ui == null || Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 0.5f;
            foreach (var notice in Notices)
                if (notice.Visible) notice.Visible = notice.IsCurrent();

            var research = ui.researchResultTip;
            if (research != null && research.active)
            {
                var techId = (int?)ResearchTechId?.GetValue(research) ?? 0;
                Observe("research:" + techId, "research", (research.conclusionText?.text ?? "") + "\n" + (research.functionText?.text ?? ""),
                    () => research.active && ((int?)ResearchTechId?.GetValue(research) ?? 0) == techId,
                    () => research._Close());
            }
            var tutorial = ui.tutorialWindow;
            if (tutorial != null && tutorial.active)
            {
                var id = tutorial.tutorialId;
                var text = tutorial.contentGroup == null ? "" : string.Join("\n", tutorial.contentGroup.GetComponentsInChildren<Text>().Select(t => t.text));
                Observe("tutorial:" + id, "tutorial", text, () => tutorial.active && tutorial.tutorialId == id, () => ui.CloseTutorialWindow());
            }
            if (ui.tutorialTip?.entryShowed != null)
            {
                foreach (var entry in ui.tutorialTip.entryShowed.ToArray())
                {
                    if (!entry.active) continue;
                    var id = entry.tutorialId;
                    var functionId = entry.functionId;
                    var text = entry.nameText?.text ?? "";
                    Observe("tutorialTip:" + id + ":" + functionId + ":" + text, "tutorialTip", text,
                        () => entry != null && entry.active && entry.tutorialId == id && entry.functionId == functionId && entry.nameText?.text == text,
                        () => entry.OnCloseButtonClick());
                }
            }
            var advisor = ui.advisorTip;
            if (advisor != null && advisor.active && advisor.playingTip != null)
            {
                var id = advisor.playingTip.ID;
                Observe("advisor:" + id, "advisor", advisor.tipText?.text ?? "",
                    () => advisor.active && advisor.playingTip?.ID == id, () => advisor.FadeOutAndStop());
            }
            while (Notices.Count > 64)
            {
                var expired = Notices.FindIndex(n => n.Acknowledged && !n.Visible);
                if (expired < 0) break;
                Notices.RemoveAt(expired);
            }
        }

        private static void Observe(string key, string kind, string text, Func<bool> current, Action close)
        {
            // 原生淡出过程会清空文本，保留此前读到的正文，避免未确认记录变成空消息。
            if (string.IsNullOrWhiteSpace(text)) return;
            var existing = Notices.Find(n => n.Key == key && n.Visible);
            if (existing != null)
            {
                existing.Text = text;
                return;
            }
            Notices.Add(new Notice { Id = ++nextId, Key = key, Kind = kind, Text = text,
                Since = Time.realtimeSinceStartup, Visible = true, IsCurrent = current, Close = close });
        }

        public static bool Dismiss(int id)
        {
            var notice = Notices.Find(n => n.Id == id);
            if (notice == null) return false;
            if (notice.Acknowledged) return true;
            if (notice.Visible && notice.IsCurrent()) notice.Close();
            notice.Visible = false;
            notice.Acknowledged = true;
            return true;
        }

        public static JsonObject Capture()
        {
            return new JsonObject
            {
                ["notices"] = Notices.Select(n => (object)new JsonObject
                {
                    ["id"] = n.Id, ["kind"] = n.Kind, ["text"] = n.Text,
                    ["visible"] = n.Visible, ["acknowledged"] = n.Acknowledged,
                    ["ageSeconds"] = Time.realtimeSinceStartup - n.Since
                }).ToList(),
                ["goalPanel"] = UIRoot.instance?.uiGame?.goalPanel
            };
        }

        public static JsonObject CapturePending()
        {
            var goals = new List<object>();
            var panel = UIRoot.instance?.uiGame?.goalPanel;
            if (panel?.goalGroups != null)
            {
                foreach (var group in panel.goalGroups)
                {
                    if (!group.active) continue;
                    goals.Add(new JsonObject
                    {
                        ["protoId"] = group.protoId, ["text"] = group.nameText?.text,
                        ["stage"] = group.goalData?.stage.ToString(),
                        ["items"] = group.goalInfoEntries?.Where(e => e.active).Select(e => (object)new JsonObject
                        {
                            ["protoId"] = e.protoId, ["text"] = e.nameText?.text,
                            ["stage"] = e.goalData?.stage.ToString()
                        }).ToList()
                    });
                }
            }
            return new JsonObject
            {
                ["notices"] = Notices.Where(n => !n.Acknowledged).Select(n => (object)new JsonObject
                {
                    ["id"] = n.Id, ["kind"] = n.Kind, ["text"] = n.Text,
                    ["visible"] = n.Visible, ["ageSeconds"] = Time.realtimeSinceStartup - n.Since
                }).ToList(),
                ["goals"] = goals
            };
        }
    }
}
