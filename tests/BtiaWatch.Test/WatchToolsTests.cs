using BtiaWatch;
using ModelContextProtocol.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BtiaWatch.Test
{
    /// <summary>A stand-in for TIA Portal that records what the tools asked for.</summary>
    internal sealed class FakePortal : IWatchPortal
    {
        public string State = "Offline";
        public readonly List<string> Calls = new();
        public readonly IntPtr Window = new(0x1234);

        public string GetOnlineState(int? portalId, string? plc)
        {
            Calls.Add($"state {plc}");
            return State;
        }

        public string GoOnline(int? portalId, string? plc, string pcInterface, string address)
        {
            Calls.Add($"online {pcInterface} {address}");
            State = "Online";
            return State;
        }

        public string GoOffline(int? portalId, string? plc)
        {
            Calls.Add("offline");
            State = "Offline";
            return State;
        }

        public string ExportDbSnapshot(int? portalId, string? plc, string dbName)
        {
            Calls.Add($"snapshot {dbName}");
            if (dbName == "Missing")
            {
                throw new ArgumentException($"Data block '{dbName}' not found.");
            }
            return SnapshotXml(dbName, $"<Value Path=\"Speed\" Type=\"Int\">{dbName.Length}</Value>");
        }

        /// <summary>Shape of a V21 InterfaceSnapshot export with ExportOptions.WithReadOnly (live test).</summary>
        public static string SnapshotXml(string dbName, string values) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Document><Engineering version=\"V21\" />" +
            $"<SW.Blocks.InterfaceSnapshot ID=\"0\"><AttributeList><Name>{dbName}</Name>" +
            "<Snapshot ReadOnly=\"true\"><SnapshotValues xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/Snapshot/v1\">" +
            values +
            "</SnapshotValues></Snapshot><SnapshotDate ReadOnly=\"true\">2026-10-14T11:54:03Z</SnapshotDate>" +
            "</AttributeList></SW.Blocks.InterfaceSnapshot></Document>";

        public IntPtr ShowInEditor(int? portalId, string? plc, WatchTarget target, string name)
        {
            Calls.Add($"show {target} {name}");
            return Window;
        }
    }

    /// <summary>A stand-in for the desktop: records keys and focus changes, can make a dialog appear.</summary>
    internal sealed class FakeWindows : IWindowOps
    {
        public readonly List<string> Log = new();
        public readonly IntPtr Previous = new(0x9999);
        public readonly IntPtr Dialog = new(0x5555);
        public bool DialogAfterShortcut;
        public bool ShortcutSent;
        public byte[] Png = { 0x89, 0x50, 0x4E, 0x47 };
        /// <summary>What the Monitor button reads before and after Ctrl+T; null = not found (the default).</summary>
        public bool? MonitoringBefore, MonitoringAfter;

        /// <summary>Reads returned first, one per call, before falling back to MonitoringBefore/After.</summary>
        public readonly Queue<bool?> MonitoringReads = new();

        public bool? IsMonitoring(IntPtr hwnd) =>
            MonitoringReads.Count > 0 ? MonitoringReads.Dequeue() : ShortcutSent ? MonitoringAfter : MonitoringBefore;

        public byte[] Capture(IntPtr hwnd) { Log.Add($"capture {hwnd}"); return Png; }
        public void Restore(IntPtr hwnd) => Log.Add($"restore {hwnd}");
        public IntPtr Foreground() => Previous;
        public void Activate(IntPtr hwnd) => Log.Add($"activate {hwnd}");
        public bool SendMonitorShortcut(IntPtr hwnd) { Log.Add($"ctrl+t {hwnd}"); ShortcutSent = true; return true; }
        public bool PressEscape(IntPtr hwnd) { Log.Add($"esc {hwnd}"); return true; }

        public IReadOnlyList<IntPtr> OtherWindowsOfProcess(IntPtr hwnd) =>
            DialogAfterShortcut && ShortcutSent ? new[] { new IntPtr(0x7777), Dialog } : new[] { new IntPtr(0x7777) };
    }

    [TestClass]
    [DoNotParallelize]
    public class WatchToolsTests
    {
        private const string Allowed = "192.0.2.10, 192.0.2.11";
        private FakePortal _fake = null!;
        private FakeWindows _windows = null!;

        [TestInitialize]
        public void Init()
        {
            _fake = new FakePortal();
            _windows = new FakeWindows();
            WatchTools.Portal = _fake;
            WatchTools.Windows = _windows;
            WatchTools.OpenedByTool.Clear();
            Environment.SetEnvironmentVariable(WatchTools.AllowedTargetsVariable, Allowed);
        }

        [TestCleanup]
        public void Cleanup() => Environment.SetEnvironmentVariable(WatchTools.AllowedTargetsVariable, null);

        private static string TextOf(CallToolResult result) =>
            string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));

        private void GoOnlineByTool()
        {
            Assert.AreEqual("State: Online", TextOf(WatchTools.WatchOnline(action: "goOnline", target: "192.0.2.10", pcInterface: "PLCSIM")));
            _fake.Calls.Clear();
        }

        [TestMethod]
        public void WatchOnline_ReportsState()
        {
            var result = WatchTools.WatchOnline("PLC_1");

            Assert.IsFalse(result.IsError == true);
            Assert.AreEqual("State: Offline", TextOf(result));
        }

        [TestMethod]
        public void WatchOnline_GoOnlineToAllowedTargetAndOffline()
        {
            Assert.AreEqual("State: Online", TextOf(WatchTools.WatchOnline(action: "goOnline", target: " 192.0.2.11 ", pcInterface: "PLCSIM")));
            Assert.AreEqual("State: Offline", TextOf(WatchTools.WatchOnline(action: "goOffline")));
            CollectionAssert.AreEqual(new[] { "state ", "online PLCSIM 192.0.2.11", "offline" }, _fake.Calls);
        }

        [TestMethod]
        public void WatchOnline_AlreadyOnlineAllowedTarget_AdoptsAndAllowsShortcut()
        {
            _fake.State = "Online";

            var result = WatchTools.WatchOnline(action: "goOnline", target: "192.0.2.10", pcInterface: "PLCSIM");

            Assert.IsFalse(result.IsError == true);
            Assert.AreEqual("State: Online (already online; connection adopted for 192.0.2.10)", TextOf(result));
            Assert.IsFalse(_fake.Calls.Any(c => c.StartsWith("online")));
            WatchTools.WatchScreen("Tags_1", "tagTable", settleMs: 0);
            Assert.IsTrue(_windows.ShortcutSent);
        }

        [TestMethod]
        public void WatchOnline_AlreadyOnlineTargetOutsideList_IsRefused()
        {
            _fake.State = "Online";

            var result = WatchTools.WatchOnline(action: "goOnline", target: "192.0.2.99", pcInterface: "PLCSIM");

            Assert.IsTrue(result.IsError == true);
            Assert.AreEqual(0, WatchTools.OpenedByTool.Count);
            Assert.AreEqual(0, _fake.Calls.Count);
        }

        [TestMethod]
        public void WatchOnline_GoOnlineDisabledWithoutAllowedTargets()
        {
            Environment.SetEnvironmentVariable(WatchTools.AllowedTargetsVariable, null);

            var result = WatchTools.WatchOnline(action: "goOnline", target: "192.0.2.10", pcInterface: "PLCSIM");

            Assert.IsTrue(result.IsError == true);
            StringAssert.Contains(TextOf(result), "disabled");
            Assert.AreEqual(0, _fake.Calls.Count);
        }

        [TestMethod]
        public void WatchOnline_GoOnlineRefusesTargetOutsideList()
        {
            foreach (var target in new[] { "192.0.2.99", "", null, "192.0.2.1" })
            {
                var result = WatchTools.WatchOnline(action: "goOnline", target: target, pcInterface: "PLCSIM");
                Assert.IsTrue(result.IsError == true, target);
            }
            Assert.AreEqual(0, _fake.Calls.Count);
        }

        [TestMethod]
        public void WatchOnline_GoOnlineNeedsPcInterface()
        {
            var result = WatchTools.WatchOnline(action: "goOnline", target: "192.0.2.10");

            Assert.IsTrue(result.IsError == true);
            Assert.AreEqual(0, _fake.Calls.Count);
        }

        [TestMethod]
        public void WatchOnline_UnknownActionIsAnError()
        {
            var result = WatchTools.WatchOnline(action: "run");

            Assert.IsTrue(result.IsError == true);
            Assert.AreEqual(0, _fake.Calls.Count);
        }

        [TestMethod]
        public void WatchDb_OfflineIsAnErrorAndTakesNoSnapshot()
        {
            var result = WatchTools.WatchDb("Data_A");

            Assert.IsTrue(result.IsError == true);
            StringAssert.Contains(TextOf(result), "not online");
            CollectionAssert.AreEqual(new[] { "state " }, _fake.Calls);
        }

        [TestMethod]
        public void WatchDb_ReadsEachNamedBlockOnce()
        {
            _fake.State = "Online";

            var text = TextOf(WatchTools.WatchDb(" Data_A, Data_B ,Data_A,"));

            CollectionAssert.AreEqual(new[] { "state ", "snapshot Data_A", "snapshot Data_B" }, _fake.Calls);
            StringAssert.Contains(text, "=== Data_A ===");
            StringAssert.Contains(text, "=== Data_B ===" + Environment.NewLine + "Snapshot taken: 2026-10-14T11:54:03Z" + Environment.NewLine + "Speed = 6 (Int)");
        }

        [TestMethod]
        public void FormatSnapshot_WritesPathValueTypeLines()
        {
            var xml = FakePortal.SnapshotXml("Data_A",
                "<Value Path=\"Mode\" Type=\"Int\">3</Value>" +
                "<Value Path=\"Station[2].Hmi.Req\" Type=\"Bool\">FALSE</Value>" +
                "<Value Path=\"Status\" Type=\"Word\">16#8000</Value>");

            var lines = WatchTools.FormatSnapshot(xml).Split(new[] { Environment.NewLine }, StringSplitOptions.None);

            CollectionAssert.AreEqual(new[]
            {
                "Snapshot taken: 2026-10-14T11:54:03Z",
                "Mode = 3 (Int)",
                "Station[2].Hmi.Req = FALSE (Bool)",
                "Status = 16#8000 (Word)"
            }, lines);
        }

        [TestMethod]
        public void FormatSnapshot_ExportWithoutValues_ReturnsRawXml()
        {
            // What ExportOptions.None gave: the name only.
            const string xml = "<Document><SW.Blocks.InterfaceSnapshot ID=\"0\"><AttributeList><Name>Data_A</Name></AttributeList></SW.Blocks.InterfaceSnapshot></Document>";

            var text = WatchTools.FormatSnapshot(xml);

            StringAssert.StartsWith(text, "No snapshot values");
            StringAssert.Contains(text, xml);
            Assert.AreEqual("not xml", WatchTools.FormatSnapshot("not xml"));
        }

        private static string PidXml() => FakePortal.SnapshotXml("PID_Heater",
            "<Value Path=\"State\" Type=\"Int\">3</Value>" +
            "<Value Path=\"Config.InputUpperLimit\" Type=\"Real\">300.0</Value>" +
            "<Value Path=\"Config.InputLowerLimit\" Type=\"Real\">0.0</Value>" +
            "<Value Path=\"ConfigOld\" Type=\"Int\">1</Value>" +
            "<Value Path=\"Retain.CtrlParams.Heat.Gain\" Type=\"Real\">2.5</Value>" +
            "<Value Path=\"Zone[1].Sp\" Type=\"Real\">180.0</Value>" +
            "<Value Path=\"Zone[10].Sp\" Type=\"Real\">190.0</Value>");

        [TestMethod]
        public void FormatSnapshot_MembersKeepsPathsAtOrBelowAndCounts()
        {
            var lines = WatchTools.FormatSnapshot(PidXml(), new[] { "state", "Config", "Retain.CtrlParams.", "Zone[1]" })
                .Split(new[] { Environment.NewLine }, StringSplitOptions.None);

            CollectionAssert.AreEqual(new[]
            {
                "Snapshot taken: 2026-10-14T11:54:03Z",
                "Members: 5 of 7 (filter: state, Config, Retain.CtrlParams., Zone[1])",
                "State = 3 (Int)",
                "Config.InputUpperLimit = 300.0 (Real)",
                "Config.InputLowerLimit = 0.0 (Real)",
                "Retain.CtrlParams.Heat.Gain = 2.5 (Real)",
                "Zone[1].Sp = 180.0 (Real)"
            }, lines);
        }

        [TestMethod]
        public void FormatSnapshot_MembersWithoutMatch_ListsTopLevelMembers()
        {
            var text = WatchTools.FormatSnapshot(PidXml(), new[] { "Sts" });

            StringAssert.Contains(text, "Members: 0 of 7");
            StringAssert.Contains(text, "No member matches. Top-level members: State, Config, ConfigOld, Retain, Zone");
        }

        [TestMethod]
        public void IsAtOrBelow_MatchesWholeSegmentsOnly()
        {
            Assert.IsTrue(WatchTools.IsAtOrBelow("A.B", "A.B"));
            Assert.IsTrue(WatchTools.IsAtOrBelow("A.B.C", "a.b"));
            Assert.IsTrue(WatchTools.IsAtOrBelow("A.B[2].C", "A.B"));
            Assert.IsFalse(WatchTools.IsAtOrBelow("A.BC", "A.B"));
            Assert.IsFalse(WatchTools.IsAtOrBelow("A", "A.B"));
            Assert.IsFalse(WatchTools.IsAtOrBelow("A", " "));
        }

        [TestMethod]
        public void WatchDb_MembersFilterAppliesToEveryBlock()
        {
            _fake.State = "Online";

            var text = TextOf(WatchTools.WatchDb("Data_A,Data_B", members: "Speed"));
            var none = TextOf(WatchTools.WatchDb("Data_A", members: "Other"));

            StringAssert.Contains(text, "=== Data_A ===" + Environment.NewLine + "Snapshot taken: 2026-10-14T11:54:03Z" + Environment.NewLine + "Speed = 6 (Int)");
            StringAssert.Contains(text, "=== Data_B ===" + Environment.NewLine + "Snapshot taken: 2026-10-14T11:54:03Z" + Environment.NewLine + "Speed = 6 (Int)");
            StringAssert.Contains(none, "No member matches. Top-level members: Speed");
        }

        [TestMethod]
        public void WatchDb_PortalErrorBecomesToolError()
        {
            _fake.State = "Online";

            var result = WatchTools.WatchDb("Missing");

            Assert.IsTrue(result.IsError == true);
            StringAssert.Contains(TextOf(result), "not found");
        }

        [TestMethod]
        public void WatchScreen_OnlineByTool_SendsCtrlTOnceAndRestoresFocusAfterCapture()
        {
            GoOnlineByTool();

            var result = WatchTools.WatchScreen("Watch_1", "watchTable", settleMs: 0);

            Assert.IsFalse(result.IsError == true, TextOf(result));
            CollectionAssert.AreEqual(new[] { "state ", "show WatchTable Watch_1" }, _fake.Calls);
            var w = _fake.Window;
            CollectionAssert.AreEqual(new[] { $"restore {w}", $"ctrl+t {w}", $"capture {w}", $"activate {_windows.Previous}" }, _windows.Log);
            StringAssert.Contains(TextOf(result), "toggles");
            var image = result.Content.OfType<ImageContentBlock>().Single();
            Assert.AreEqual("image/png", image.MimeType);
            CollectionAssert.AreEqual(_windows.Png, image.DecodedData.ToArray());
        }

        [TestMethod]
        public void WatchScreen_MonitoringAlreadyOn_SendsNoShortcut()
        {
            GoOnlineByTool();
            _windows.MonitoringBefore = true;

            var result = WatchTools.WatchScreen("Watch_1", settleMs: 0);

            Assert.IsFalse(result.IsError == true, TextOf(result));
            Assert.IsFalse(_windows.ShortcutSent);
            StringAssert.Contains(TextOf(result), "already on");
            StringAssert.Contains(TextOf(result), "Monitoring is on.");
            Assert.AreEqual(1, result.Content.OfType<ImageContentBlock>().Count());
        }

        [TestMethod]
        public void WatchScreen_MonitoringOff_SendsCtrlTAndConfirmsItIsOn()
        {
            GoOnlineByTool();
            _windows.MonitoringBefore = false;
            _windows.MonitoringAfter = true;

            var text = TextOf(WatchTools.WatchScreen("Watch_1", settleMs: 0));

            Assert.AreEqual(1, _windows.Log.Count(l => l.StartsWith("ctrl+t")));
            StringAssert.Contains(text, "Monitoring is on.");
            Assert.IsFalse(text.Contains("Warning"), text);
        }

        [TestMethod]
        public void WatchScreen_MonitoringStillOffAfterCtrlT_Warns()
        {
            GoOnlineByTool();
            _windows.MonitoringBefore = false;
            _windows.MonitoringAfter = false;

            var text = TextOf(WatchTools.WatchScreen("Watch_1", settleMs: 0));

            Assert.AreEqual(1, _windows.Log.Count(l => l.StartsWith("ctrl+t")));
            StringAssert.Contains(text, "reads as off");
        }

        [TestMethod]
        public void WatchScreen_StateUnreadableThenOn_RetriesAndSendsNoCtrlT()
        {
            GoOnlineByTool();
            _windows.MonitoringReads.Enqueue(null);
            _windows.MonitoringReads.Enqueue(true);
            _windows.MonitoringBefore = true;

            var text = TextOf(WatchTools.WatchScreen("Watch_1", settleMs: 1000));

            Assert.AreEqual(0, _windows.Log.Count(l => l.StartsWith("ctrl+t")));
            StringAssert.Contains(text, "already on");
        }

        [TestMethod]
        public void WatchScreen_StateUnreadableThenOff_RetriesAndSendsCtrlT()
        {
            GoOnlineByTool();
            _windows.MonitoringReads.Enqueue(null);
            _windows.MonitoringReads.Enqueue(false);
            _windows.MonitoringAfter = true;

            var text = TextOf(WatchTools.WatchScreen("Watch_1", settleMs: 1000));

            Assert.AreEqual(1, _windows.Log.Count(l => l.StartsWith("ctrl+t")));
            StringAssert.Contains(text, "Monitoring is on.");
            Assert.IsFalse(text.Contains("could not be read"), text);
        }

        [TestMethod]
        public void WatchScreen_MonitoringStateUnreadable_SendsCtrlTOnceWithWarning()
        {
            GoOnlineByTool();

            var text = TextOf(WatchTools.WatchScreen("Watch_1", settleMs: 0));

            Assert.AreEqual(1, _windows.Log.Count(l => l.StartsWith("ctrl+t")));
            StringAssert.Contains(text, "could not be read");
        }

        [TestMethod]
        public void WatchScreen_Offline_SendsNoShortcut()
        {
            var result = WatchTools.WatchScreen("Tags_1", "tagTable", settleMs: 0);

            Assert.IsFalse(result.IsError == true, TextOf(result));
            Assert.IsFalse(_windows.ShortcutSent);
            StringAssert.Contains(TextOf(result), "NOT sent");
            StringAssert.Contains(TextOf(result), "'Offline'");
            Assert.AreEqual(1, result.Content.OfType<ImageContentBlock>().Count());
        }

        [TestMethod]
        public void WatchScreen_OnlineButNotOpenedByTool_SendsNoShortcut()
        {
            _fake.State = "Online";

            var result = WatchTools.WatchScreen("Tags_1", "tagTable", settleMs: 0);

            Assert.IsFalse(_windows.ShortcutSent);
            StringAssert.Contains(TextOf(result), "not opened by this server");
        }

        [TestMethod]
        public void WatchScreen_ConnectionLost_ForgetsItAndSendsNoShortcut()
        {
            GoOnlineByTool();
            _fake.State = "Offline";
            WatchTools.WatchScreen("Tags_1", "tagTable", settleMs: 0);
            _fake.State = "Online"; // e.g. someone went online by hand afterwards

            WatchTools.WatchScreen("Tags_1", "tagTable", settleMs: 0);

            Assert.IsFalse(_windows.ShortcutSent);
        }

        [TestMethod]
        public void WatchScreen_SendShortcutFalse_OnlyCaptures()
        {
            GoOnlineByTool();

            var result = WatchTools.WatchScreen("Tags_1", "tagTable", sendShortcut: false, settleMs: 0);

            Assert.IsFalse(_windows.ShortcutSent);
            StringAssert.Contains(TextOf(result), "sendShortcut=false");
        }

        [TestMethod]
        public void WatchScreen_DialogAfterShortcut_IsClosedWithEscAndIsAnError()
        {
            GoOnlineByTool();
            _windows.DialogAfterShortcut = true;

            var result = WatchTools.WatchScreen("DB_1", "block", settleMs: 0);

            Assert.IsTrue(result.IsError == true);
            StringAssert.Contains(TextOf(result), "dialog");
            CollectionAssert.Contains(_windows.Log, $"esc {_windows.Dialog}");
            Assert.IsFalse(_windows.Log.Any(l => l.StartsWith("capture")));
            Assert.AreEqual($"activate {_windows.Previous}", _windows.Log.Last());
        }

        [TestMethod]
        public void WatchScreen_UnknownKindIsAnError()
        {
            var result = WatchTools.WatchScreen("X", "hardware", settleMs: 0);

            Assert.IsTrue(result.IsError == true);
            Assert.AreEqual(0, _fake.Calls.Count);
        }
    }
}
