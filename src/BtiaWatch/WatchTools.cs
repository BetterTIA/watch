using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;

namespace BtiaWatch
{
    /// <summary>
    /// The three read-only tools. None of them writes values to the PLC; there is no parameter that
    /// could make them do so.
    /// </summary>
    [McpServerToolType]
    public static class WatchTools
    {
        // shortcut: placeholder until the live spike confirms the key for "Monitor all"; empty = send nothing.
        public const string DefaultMonitorShortcut = "";

        // shortcut: placeholder until the live spike measures how long TIA Portal needs to show values.
        public const int DefaultSettleMs = 2000;

        /// <summary>Set by Program to the Openness implementation, by tests to a fake.</summary>
        public static IWatchPortal? Portal { get; set; }

        /// <summary>Captures a window; replaceable so tests need no real window behind a fake portal.</summary>
        public static Func<IntPtr, byte[]> Capture { get; set; } = WindowCapture.CapturePng;

        /// <summary>Sends keys to a window; replaceable for the same reason.</summary>
        public static Func<IntPtr, string, int, bool> SendKeys { get; set; } = WindowCapture.SendKeysTo;

        private static IWatchPortal Tia => Portal ?? throw new InvalidOperationException("No TIA Portal connection layer is configured.");

        [McpServerTool(Name = "watch_online", Title = "PLC online state", ReadOnly = true, Destructive = false, OpenWorld = false),
         Description("Return the online state of a PLC (OnlineProvider.State). With action 'goOnline' or 'goOffline' it first opens or closes the online connection of TIA Portal; this changes nothing in the PLC.")]
        public static CallToolResult WatchOnline(
            [Description("plc: name of the PLC software. Leave empty when the project has one PLC")] string? plc = null,
            [Description("action: 'state' (default), 'goOnline' or 'goOffline'")] string action = "state",
            [Description("portalId: process id of the TIA Portal instance. Leave empty when only one is running")] int? portalId = null)
        {
            return Run(() =>
            {
                var state = action.Trim().ToLowerInvariant() switch
                {
                    "state" or "" => Tia.GetOnlineState(portalId, plc),
                    "goonline" => Tia.SetOnline(portalId, plc, true),
                    "gooffline" => Tia.SetOnline(portalId, plc, false),
                    _ => throw new ArgumentException($"Unknown action '{action}'. Use 'state', 'goOnline' or 'goOffline'.")
                };
                return Text($"State: {state}");
            });
        }

        [McpServerTool(Name = "watch_db", Title = "Actual values of data blocks", ReadOnly = true, Destructive = false, OpenWorld = false),
         Description("Read the actual values of one or more data blocks: takes a snapshot of each (ValueService.CreateSnapshot) and returns its SimaticML export as text. The snapshot is stored in the offline project, which TIA Portal then shows as changed. Fail-safe data blocks are refused.")]
        public static CallToolResult WatchDb(
            [Description("dbNames: data block names, separated by commas")] string dbNames,
            [Description("plc: name of the PLC software. Leave empty when the project has one PLC")] string? plc = null,
            [Description("portalId: process id of the TIA Portal instance. Leave empty when only one is running")] int? portalId = null)
        {
            return Run(() =>
            {
                var names = SplitNames(dbNames);
                if (names.Count == 0)
                {
                    throw new ArgumentException("dbNames is empty.");
                }

                // shortcut: export passed through as text; parse into name/value rows once the spike shows the format.
                var sb = new StringBuilder();
                foreach (var name in names)
                {
                    sb.AppendLine($"=== {name} ===");
                    sb.AppendLine(Tia.ExportDbSnapshot(portalId, plc, name));
                }
                return Text(sb.ToString());
            });
        }

        [McpServerTool(Name = "watch_screen", Title = "Screenshot of a monitored editor", ReadOnly = true, Destructive = false, OpenWorld = false),
         Description("Open a watch table, tag table or block in TIA Portal (ShowInEditor), switch on monitoring with a keyboard shortcut sent to the TIA Portal window, and return a PNG capture of the window. Go online first with watch_online.")]
        public static CallToolResult WatchScreen(
            [Description("name: name of the watch table, tag table or block")] string name,
            [Description("kind: 'watchTable' (default), 'tagTable' or 'block'")] string kind = "watchTable",
            [Description("plc: name of the PLC software. Leave empty when the project has one PLC")] string? plc = null,
            [Description("monitorShortcut: keys for 'Monitor all' in SendKeys notation, e.g. '^t' for Ctrl+T. Empty sends no keys")] string monitorShortcut = DefaultMonitorShortcut,
            [Description("settleMs: wait after opening the editor and after the shortcut, in milliseconds")] int settleMs = DefaultSettleMs,
            [Description("portalId: process id of the TIA Portal instance. Leave empty when only one is running")] int? portalId = null)
        {
            return Run(() =>
            {
                var target = ParseTarget(kind);
                settleMs = Math.Max(0, Math.Min(settleMs, 30000));

                var hwnd = Tia.ShowInEditor(portalId, plc, target, name);
                Thread.Sleep(settleMs);

                var note = "No monitoring shortcut sent.";
                if (!string.IsNullOrEmpty(monitorShortcut))
                {
                    if (SendKeys(hwnd, monitorShortcut, settleMs))
                    {
                        Thread.Sleep(settleMs);
                        note = $"Sent '{monitorShortcut}'.";
                    }
                    else
                    {
                        note = "TIA Portal could not be brought to the front; no keys were sent.";
                    }
                }

                var png = Capture(hwnd);
                return new CallToolResult
                {
                    Content =
                    [
                        new TextContentBlock { Text = $"{kind} '{name}'. {note}" },
                        ImageContentBlock.FromBytes(png, "image/png")
                    ]
                };
            });
        }

        internal static WatchTarget ParseTarget(string kind) => kind.Trim().ToLowerInvariant() switch
        {
            "watchtable" or "" => WatchTarget.WatchTable,
            "tagtable" => WatchTarget.TagTable,
            "block" => WatchTarget.Block,
            _ => throw new ArgumentException($"Unknown kind '{kind}'. Use 'watchTable', 'tagTable' or 'block'.")
        };

        private static List<string> SplitNames(string names) =>
            (names ?? string.Empty).Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).Distinct().ToList();

        private static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };

        // A failed call returns its message as a tool error the model can read, instead of a protocol error.
        private static CallToolResult Run(Func<CallToolResult> body)
        {
            try
            {
                return body();
            }
            catch (Exception ex)
            {
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = ex.Message }] };
            }
        }
    }
}
