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

        public string SetOnline(int? portalId, string? plc, bool online)
        {
            Calls.Add($"online={online} {plc}");
            State = online ? "Online" : "Offline";
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

    [TestClass]
    [DoNotParallelize]
    public class WatchToolsTests
    {
        private FakePortal _fake = null!;

        [TestInitialize]
        public void Init()
        {
            _fake = new FakePortal();
            WatchTools.Portal = _fake;
        }

        private static string TextOf(CallToolResult result) =>
            string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));

        [TestMethod]
        public void WatchOnline_ReportsState()
        {
            var result = WatchTools.WatchOnline("PLC_1");

            Assert.IsFalse(result.IsError == true);
            Assert.AreEqual("State: Offline", TextOf(result));
        }

        [TestMethod]
        public void WatchOnline_GoOnlineAndOffline()
        {
            Assert.AreEqual("State: Online", TextOf(WatchTools.WatchOnline(action: "goOnline")));
            Assert.AreEqual("State: Offline", TextOf(WatchTools.WatchOnline(action: "goOffline")));
            CollectionAssert.AreEqual(new[] { "online=True ", "online=False " }, _fake.Calls);
        }

        [TestMethod]
        public void WatchOnline_UnknownActionIsAnError()
        {
            var result = WatchTools.WatchOnline(action: "run");

            Assert.IsTrue(result.IsError == true);
            Assert.AreEqual(0, _fake.Calls.Count);
        }

        [TestMethod]
        public void WatchDb_ReadsEachNamedBlockOnce()
        {
            var text = TextOf(WatchTools.WatchDb(" Data_A, Data_B ,Data_A,"));

            CollectionAssert.AreEqual(new[] { "snapshot Data_A", "snapshot Data_B" }, _fake.Calls);
            StringAssert.Contains(text, "=== Data_A ===");
            StringAssert.Contains(text, "<Snapshot Name=\"Data_B\" />");
        }

        [TestMethod]
        public void WatchDb_PortalErrorBecomesToolError()
        {
            var result = WatchTools.WatchDb("Missing");

            Assert.IsTrue(result.IsError == true);
            StringAssert.Contains(TextOf(result), "not found");
        }

        [TestMethod]
        public void WatchScreen_OpensEditorSendsShortcutAndReturnsPng()
        {
            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
            var sent = new List<string>();
            WatchTools.Capture = h => h == _fake.Window ? png : throw new InvalidOperationException("wrong window");
            WatchTools.SendKeys = (h, keys, ms) => { sent.Add(keys); return true; };

            var result = WatchTools.WatchScreen("Watch_1", "watchTable", monitorShortcut: "^t", settleMs: 0);

            Assert.IsFalse(result.IsError == true, TextOf(result));
            CollectionAssert.AreEqual(new[] { "show WatchTable Watch_1" }, _fake.Calls);
            CollectionAssert.AreEqual(new[] { "^t" }, sent);
            var image = result.Content.OfType<ImageContentBlock>().Single();
            Assert.AreEqual("image/png", image.MimeType);
            CollectionAssert.AreEqual(png, image.DecodedData.ToArray());
        }

        [TestMethod]
        public void WatchScreen_EmptyShortcutSendsNoKeys()
        {
            var sent = 0;
            WatchTools.Capture = h => new byte[] { 1 };
            WatchTools.SendKeys = (h, keys, ms) => { sent++; return true; };

            var result = WatchTools.WatchScreen("Tags_1", "tagTable", settleMs: 0);

            Assert.AreEqual(0, sent);
            CollectionAssert.AreEqual(new[] { "show TagTable Tags_1" }, _fake.Calls);
            StringAssert.Contains(TextOf(result), "No monitoring shortcut sent");
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
