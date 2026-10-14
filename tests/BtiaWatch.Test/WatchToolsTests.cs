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
            return $"<Snapshot Name=\"{dbName}\" />";
        }

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
            CollectionAssert.AreEqual(new[] { "online PLCSIM 192.0.2.11", "offline" }, _fake.Calls);
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
            StringAssert.Contains(text, "<Snapshot Name=\"Data_B\" />");
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
