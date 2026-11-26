using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace BtiaWatch
{
    /// <summary>
    /// The three read-only tools. None of them writes values to the PLC; there is no parameter that
    /// could make them do so.
    /// </summary>
    [McpServerToolType]
    public static class WatchTools
    {
        // Measured in the offline spike: editors open within about a second; monitor values need a little longer.
        public const int DefaultSettleMs = 3000;

        /// <summary>Default and upper bound for the rows watch_db returns per block.</summary>
        public const int DefaultMaxRows = 500, MaxRowsLimit = 20000;

        /// <summary>Comma-separated IP addresses watch_online may connect to; empty or unset disables goOnline.</summary>
        public const string AllowedTargetsVariable = "BTIA_WATCH_ALLOWED_TARGETS";

        /// <summary>Set by Program to the Openness implementation, by tests to a fake.</summary>
        public static IWatchPortal? Portal { get; set; }

        /// <summary>Window capture and keystrokes; replaced by tests.</summary>
        public static IWindowOps Windows { get; set; } = new Win32Windows();

        // shortcut: keyed by the call parameters (portalId|plc), so a PLC addressed differently counts as not opened; fine for one agent.
        /// <summary>PLCs this server brought online itself. Only these get the monitoring shortcut.</summary>
        internal static readonly HashSet<string> OpenedByTool = new();

        private static IWatchPortal Tia => Portal ?? throw new InvalidOperationException("No TIA Portal connection layer is configured.");

        [McpServerTool(Name = "watch_online", Title = "PLC online state", ReadOnly = true, Destructive = false, OpenWorld = false),
         Description("Return the online state of a PLC (OnlineProvider.State). With action 'goOnline' it first opens the TIA Portal online connection to 'target' over 'pcInterface'; the target must be in the server's allowed list (environment variable " + AllowedTargetsVariable + "), otherwise goOnline is refused. If the PLC is already online, goOnline adopts that connection instead of opening a new one. 'goOffline' closes the connection. Nothing is changed in the PLC.")]
        public static CallToolResult WatchOnline(
            [Description("plc: name of the PLC software. Leave empty when the project has one PLC")] string? plc = null,
            [Description("action: 'state' (default), 'goOnline' or 'goOffline'")] string action = "state",
            [Description("target: IP address of the PLC for goOnline; must be in the allowed list")] string? target = null,
            [Description("pcInterface: name of the PG/PC interface for goOnline, e.g. 'PLCSIM'; a wrong name lists the available ones")] string? pcInterface = null,
            [Description("portalId: process id of the TIA Portal instance. Leave empty when only one is running")] int? portalId = null)
        {
            return Run(() =>
            {
                var key = Key(portalId, plc);
                string state;
                switch (action.Trim().ToLowerInvariant())
                {
                    case "state":
                    case "":
                        state = Tia.GetOnlineState(portalId, plc);
                        break;
                    case "goonline":
                        var allowed = AllowedTargets();
                        if (allowed.Count == 0)
                        {
                            throw new InvalidOperationException($"goOnline is disabled: {AllowedTargetsVariable} lists no allowed targets.");
                        }
                        var address = target?.Trim() ?? string.Empty;
                        if (!allowed.Contains(address))
                        {
                            throw new ArgumentException($"Target '{address}' is not allowed. Allowed: {string.Join(", ", allowed)}");
                        }
                        if (string.IsNullOrWhiteSpace(pcInterface))
                        {
                            throw new ArgumentException("pcInterface is empty; pass the PG/PC interface to connect over.");
                        }
                        // Already online (e.g. after a server restart): GoOnline would throw, so adopt the connection. The Ctrl+T
                        // guard protects against the Go online dialog in an offline editor, which cannot appear when Online, and an
                        // explicit call with an allowed target is the operator's consent.
                        // shortcut: Openness V21 cannot read the address the connection uses, so it is not compared with target.
                        if (Tia.GetOnlineState(portalId, plc) == "Online")
                        {
                            OpenedByTool.Add(key);
                            state = $"Online (already online; connection adopted for {address})";
                            break;
                        }
                        state = Tia.GoOnline(portalId, plc, pcInterface!.Trim(), address);
                        if (state == "Online")
                        {
                            OpenedByTool.Add(key);
                        }
                        break;
                    case "gooffline":
                        OpenedByTool.Remove(key);
                        state = Tia.GoOffline(portalId, plc);
                        break;
                    default:
                        throw new ArgumentException($"Unknown action '{action}'. Use 'state', 'goOnline' or 'goOffline'.");
                }
                return Text($"State: {state}");
            });
        }

        [McpServerTool(Name = "watch_db", Title = "Actual values of data blocks", ReadOnly = true, Destructive = false, OpenWorld = false),
         Description("Read the actual values of one or more data blocks: takes a snapshot of each (ValueService.CreateSnapshot) and returns, per block, 'Snapshot taken: <time>' and one 'path = value (type)' line per member, nested members as 'A.B[2].C'. The PLC must be online (watch_online); offline the snapshot would silently stay empty, so the call is refused. The snapshot is stored in the offline project, which TIA Portal then shows as changed. Fail-safe data blocks are refused. Technology objects (PID_Temp, PID_Compact...) are found by name, but Openness V21 gives no actual values for them: the call then says so and points to a watch table with watch_screen, or to BTIA_Reader read_technology_object for the offline parameters. Section headers are left out. At most 'maxRows' rows (default 500) are returned per block; a longer block is cut with 'truncated: true' and the number of rows left out, so pass 'members' to keep only the members below the given paths. The answer says when the project is marked as modified ('projectMarkedModified'); the snapshot is the likely cause and nothing is saved.")]
        public static CallToolResult WatchDb(
            [Description("dbNames: data block or technology object names, separated by commas")] string dbNames,
            [Description("members: optional member paths, separated by commas, e.g. 'State,Config.Input,Retain.CtrlParams' or 'Heater_1.PID'; keeps each member equal to a path or below it (case-insensitive). Applied to every block. Empty: all members")] string? members = null,
            [Description("plc: name of the PLC software. Leave empty when the project has one PLC")] string? plc = null,
            [Description("portalId: process id of the TIA Portal instance. Leave empty when only one is running")] int? portalId = null,
            [Description("maxRows: most rows returned per block, 1 to 20000. Default 500")] int maxRows = DefaultMaxRows)
        {
            return Run(() =>
            {
                maxRows = Math.Max(1, Math.Min(maxRows, MaxRowsLimit));
                var names = SplitNames(dbNames);
                if (names.Count == 0)
                {
                    throw new ArgumentException("dbNames is empty.");
                }

                // Offline, CreateSnapshot() returns without error and leaves the snapshot empty (spike, V21).
                var state = Tia.GetOnlineState(portalId, plc);
                if (state != "Online")
                {
                    throw new InvalidOperationException($"The PLC is '{state}', not online; an offline snapshot holds no values. Go online with watch_online first.");
                }

                var filter = SplitNames(members ?? string.Empty);
                var modifiedBefore = Tia.IsProjectModified(portalId);
                var sb = new StringBuilder();
                foreach (var name in names)
                {
                    sb.AppendLine($"=== {name} ===");
                    sb.AppendLine(FormatSnapshot(Tia.ExportDbSnapshot(portalId, plc, name), filter, maxRows));
                }
                // The snapshot lives in the offline project and Openness has no way to undo it; only say so, never save.
                var modifiedAfter = Tia.IsProjectModified(portalId);
                sb.AppendLine(modifiedAfter == true
                    ? "projectMarkedModified: true" + (modifiedBefore == true ? " (it was already marked as modified before this call)" : " (set by the snapshot; the project was not saved)")
                    : "projectMarkedModified: " + (modifiedAfter == false ? "false" : "unknown"));
                return Text(sb.ToString());
            });
        }

        [McpServerTool(Name = "watch_screen", Title = "Screenshot of a monitored editor", ReadOnly = true, Destructive = false, OpenWorld = false),
         Description("Open a watch table, tag table or block in TIA Portal (ShowInEditor) and return a PNG capture of the TIA Portal window, with monitoring switched on when possible. Monitoring is only switched on when the PLC is online over a connection this server opened with watch_online goOnline, and only when it is not already on: the state is read from the pressed state of the Monitor button in the editor's toolbar, retried for up to settleMs while the editor is still drawing. The Monitor button is pressed through MSAA, without bringing TIA Portal to the front; only when that fails or monitoring stays off, Ctrl+T (Monitor all) is sent, which needs TIA Portal in front for a moment. When the state cannot be read, Ctrl+T is sent once with a warning. The result says which path was used and whether monitoring is on.")]
        public static CallToolResult WatchScreen(
            [Description("name: name of the watch table, tag table or block")] string name,
            [Description("kind: 'watchTable' (default), 'tagTable' or 'block'")] string kind = "watchTable",
            [Description("plc: name of the PLC software. Leave empty when the project has one PLC")] string? plc = null,
            [Description("sendShortcut: switch monitoring on (Monitor button through MSAA, Ctrl+T only as fallback). Default true; false only captures")] bool sendShortcut = true,
            [Description("settleMs: wait before the capture, and the longest wait for the monitoring state to become readable, in milliseconds")] int settleMs = DefaultSettleMs,
            [Description("portalId: process id of the TIA Portal instance. Leave empty when only one is running")] int? portalId = null)
        {
            return Run(() =>
            {
                var target = ParseTarget(kind);
                settleMs = Math.Max(0, Math.Min(settleMs, 30000));
                var key = Key(portalId, plc);

                var state = Tia.GetOnlineState(portalId, plc);
                if (state != "Online")
                {
                    OpenedByTool.Remove(key);
                }

                var main = Tia.ShowInEditor(portalId, plc, target, name);
                Windows.Restore(main);
                // An undocked editor is a top-level window of its own; use it for the capture, the Monitor button and Ctrl+T.
                var floating = Windows.FindFloatingEditor(main, name);
                var hwnd = floating != IntPtr.Zero ? floating : main;
                if (hwnd != main)
                {
                    Windows.Restore(hwnd);
                }
                var where = hwnd != main ? "Floating editor window used. " : string.Empty;

                if (!sendShortcut || !OpenedByTool.Contains(key))
                {
                    // Offline, Ctrl+T makes TIA Portal connect on its own over the last used interface (spike, V21); the button is kept to the same rule.
                    var reason = where + (!sendShortcut
                        ? "No monitoring shortcut sent (sendShortcut=false)."
                        : $"Monitoring shortcut NOT sent: the PLC is '{state}'" +
                          (state == "Online" ? ", but the connection was not opened by this server" : string.Empty) +
                          ". The Monitor button is only pressed and Ctrl+T only sent over a connection opened with watch_online goOnline; the capture shows no monitor values.");
                    Thread.Sleep(settleMs);
                    return Image(kind, name, reason, Windows.Capture(hwnd));
                }

                // Monitoring is only switched on when it is not already on (Ctrl+T toggles).
                var wasOn = ReadMonitoring(hwnd, settleMs);
                if (wasOn == true)
                {
                    Thread.Sleep(settleMs);
                    return Image(kind, name, where + "Monitoring was already on; nothing pressed, Ctrl+T not sent." + MonitoringNote(hwnd, settleMs), Windows.Capture(hwnd));
                }

                var before = Windows.OtherWindowsOfProcess(hwnd);
                var previous = Windows.Foreground();
                var keysSent = false;
                // A dialog after switching monitoring on (e.g. "Go online") is closed with Esc and nothing is captured.
                void ThrowOnDialog(string action)
                {
                    var dialog = Windows.OtherWindowsOfProcess(hwnd).Except(before).FirstOrDefault();
                    if (dialog != IntPtr.Zero)
                    {
                        keysSent = true;
                        var closed = Windows.PressEscape(dialog);
                        throw new InvalidOperationException($"TIA Portal opened a dialog after {action}; " +
                            (closed ? "it was closed with Esc." : "it could not be closed, check TIA Portal.") + " Nothing was captured.");
                    }
                }

                try
                {
                    // First path: press the Monitor button through MSAA; TIA Portal is not brought to the front, no keys.
                    var fallback = "Monitor button not pressed through MSAA (not found or the press failed); fell back to Ctrl+T, which brings TIA Portal to the front for a moment. ";
                    if (Windows.PressMonitorButton(hwnd))
                    {
                        Thread.Sleep(settleMs);
                        ThrowOnDialog("pressing the Monitor button");
                        var after = ReadMonitoring(hwnd, settleMs);
                        wasOn = after;
                        if (after != false)
                        {
                            return Image(kind, name, where + "Pressed the Monitor button through MSAA (no focus change, no keys)." +
                                (after == true ? " Monitoring is on." : " Warning: the monitoring state could not be read after the wait."), Windows.Capture(hwnd));
                        }
                        fallback = "Pressed the Monitor button through MSAA, but monitoring still reads as off; fell back to Ctrl+T, which brings TIA Portal to the front for a moment. ";
                    }

                    keysSent = true;
                    if (!Windows.SendMonitorShortcut(hwnd))
                    {
                        Thread.Sleep(settleMs);
                        return Image(kind, name, where + fallback + "TIA Portal could not be brought to the front; no keys were sent.", Windows.Capture(hwnd));
                    }

                    Thread.Sleep(settleMs);
                    ThrowOnDialog("Ctrl+T");

                    var note = wasOn == null
                        ? "Sent Ctrl+T (Monitor all). Warning: the monitoring state could not be read and Ctrl+T toggles monitoring; if the capture shows no monitor values, it was switched off." +
                          (hwnd == main ? " If the editor is undocked into its own window, no window with a title containing '" + name + "' was found; dock the editor or check the capture." : string.Empty)
                        : "Sent Ctrl+T (Monitor all)." + MonitoringNote(hwnd, settleMs);
                    return Image(kind, name, where + fallback + note, Windows.Capture(hwnd));
                }
                finally
                {
                    // Focus is only given back when keys took it; the MSAA press never changes it.
                    if (keysSent && previous != IntPtr.Zero && previous != hwnd)
                    {
                        Windows.Activate(previous);
                    }
                }
            });
        }

        /// <summary>
        /// Reads the Monitor button until it answers; right after ShowInEditor the toolbar is often not drawn yet
        /// (live, V21), so a single read returned null.
        /// </summary>
        internal static bool? ReadMonitoring(IntPtr hwnd, int maxWaitMs)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var state = Windows.IsMonitoring(hwnd);
                if (state != null || watch.ElapsedMilliseconds >= maxWaitMs)
                {
                    return state;
                }
                Thread.Sleep(200);
            }
        }

        private static string MonitoringNote(IntPtr hwnd, int maxWaitMs) => ReadMonitoring(hwnd, maxWaitMs) switch
        {
            true => " Monitoring is on.",
            false => " Warning: monitoring reads as off; the capture may show no monitor values.",
            null => " Warning: the monitoring state could not be read after the wait."
        };

        /// <summary>
        /// Turns the snapshot export into "path = value (type)" lines; the export already writes paths as
        /// 'A.B[2].C' (SW.Interface.Snapshot.xsd). An export without a 'Snapshot' attribute is returned raw.
        /// With <paramref name="members"/>, only members equal to one of the paths or below it are written.
        /// </summary>
        internal static string FormatSnapshot(string xml, IReadOnlyCollection<string>? members = null, int maxRows = int.MaxValue)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Parse(xml);
            }
            catch (XmlException)
            {
                return xml;
            }

            var attributes = doc.Descendants("AttributeList").FirstOrDefault();
            var snapshot = attributes?.Element("Snapshot");
            if (snapshot == null)
            {
                return "No snapshot values in the export:\n" + xml;
            }

            // Section headers (Input, Output, Static...) come as Value elements of type "Section" without a value.
            var values = snapshot.Descendants()
                .Where(e => e.Name.LocalName == "Value" && !string.Equals((string?)e.Attribute("Type"), "Section", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var shown = members == null || members.Count == 0
                ? values
                : values.Where(v => members.Any(m => IsAtOrBelow((string?)v.Attribute("Path") ?? string.Empty, m))).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"Snapshot taken: {attributes!.Element("SnapshotDate")?.Value ?? "unknown"}");
            if (shown.Count < values.Count)
            {
                sb.AppendLine($"Members: {shown.Count} of {values.Count} (filter: {string.Join(", ", members!)})");
            }
            if (shown.Count == 0 && values.Count > 0)
            {
                // Help the caller correct the path without a second, unfiltered call.
                var top = values.Select(v => TopLevel((string?)v.Attribute("Path") ?? string.Empty)).Distinct().ToList();
                sb.AppendLine($"No member matches. Top-level members: {string.Join(", ", top.Take(50))}" + (top.Count > 50 ? $", ... ({top.Count})" : string.Empty));
            }
            if (shown.Count > maxRows)
            {
                sb.AppendLine($"truncated: true ({shown.Count - maxRows} of {shown.Count} rows not shown, limit {maxRows}; narrow with 'members' or raise 'maxRows')");
            }
            foreach (var value in shown.Take(maxRows))
            {
                sb.AppendLine($"{(string?)value.Attribute("Path")} = {value.Value} ({(string?)value.Attribute("Type")})");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>True when path is the member itself or lies below it ('A.B' matches 'A.B', 'A.B.C' and 'A.B[1]', not 'A.BC').</summary>
        internal static bool IsAtOrBelow(string path, string member)
        {
            member = member.Trim().TrimEnd('.');
            if (member.Length == 0 || !path.StartsWith(member, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return path.Length == member.Length || path[member.Length] == '.' || path[member.Length] == '[';
        }

        private static string TopLevel(string path)
        {
            var end = path.IndexOfAny(new[] { '.', '[' });
            return end < 0 ? path : path.Substring(0, end);
        }

        internal static WatchTarget ParseTarget(string kind) => kind.Trim().ToLowerInvariant() switch
        {
            "watchtable" or "" => WatchTarget.WatchTable,
            "tagtable" => WatchTarget.TagTable,
            "block" => WatchTarget.Block,
            _ => throw new ArgumentException($"Unknown kind '{kind}'. Use 'watchTable', 'tagTable' or 'block'.")
        };

        internal static List<string> AllowedTargets() =>
            SplitNames(Environment.GetEnvironmentVariable(AllowedTargetsVariable) ?? string.Empty);

        private static string Key(int? portalId, string? plc) => $"{portalId}|{plc?.Trim()}";

        private static List<string> SplitNames(string names) =>
            (names ?? string.Empty).Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).Distinct().ToList();

        private static CallToolResult Image(string kind, string name, string note, byte[] png) => new()
        {
            Content =
            [
                new TextContentBlock { Text = $"{kind} '{name}'. {note}" },
                ImageContentBlock.FromBytes(png, "image/png")
            ]
        };

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
