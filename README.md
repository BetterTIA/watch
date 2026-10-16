# BetterTIA Watch

**Read-only live values from a running TIA Portal V21, for AI agents.** An MCP server (stdio) that lets an agent
check the online state of a PLC, read the actual values of data blocks and look at a monitored watch table, tag table
or block - without someone taking screenshots by hand.

It is one of the four tools of the [BetterTIA](https://github.com/BetterTIA) set:

| Tool | Role |
|---|---|
| [reader](https://github.com/BetterTIA/reader) | reads a TIA Portal project through Openness and exports it to a folder (read-only MCP server) |
| [writer](https://github.com/BetterTIA/writer) | writes to a TIA Portal project through Openness (MCP server) |
| [editor](https://github.com/BetterTIA/editor) | draws a network from the export, lets you compose a change and tracks it until it is in the project |
| **watch** (this repository) | reads live values from the running PLC through TIA Portal (read-only MCP server) |

All four are developed, used and tested only with TIA Portal V21.

> Status: early (0.1.x). The tools are tested against a fake TIA Portal and were checked once against a real PLC
> with TIA Portal V21 (see below).

## Tools

| Tool | What it does | Source |
| --- | --- | --- |
| `watch_online` | Online state of a PLC (`OnlineProvider.State`); `action` `goOnline` connects to an allowed `target` address over a named `pcInterface` (or adopts a connection that is already online), `goOffline` disconnects | Openness |
| `watch_db` | Actual values of one or more data blocks: `ValueService.CreateSnapshot()`, then `InterfaceSnapshot.Export()` with read-only attributes; returns the snapshot time and one `path = value (type)` line per member. Technology objects (`PID_Temp`, `PID_Compact`, ...) are found by name, but Openness V21 gives no actual values for them (`Interface` is not supported on `TechnologicalInstanceDB`): the call says so and points to a watch table with `watch_screen`; `members` keeps only the members at or below the given paths (e.g. `State,Config.Input`). Requires the PLC to be online | Openness |
| `watch_screen` | Opens a watch table, tag table or block (`ShowInEditor()`), sends Ctrl+T ("Monitor all") when allowed, and returns a PNG capture of the TIA Portal window (`PrintWindow`) as MCP image content | Openness + Win32 |

Common parameters: `plc` (PLC software name, optional when the project has one PLC) and `portalId` (process id,
optional when one TIA Portal is running). The server attaches to a running TIA Portal; it never starts one.

## Configuration

| Environment variable | Meaning |
| --- | --- |
| `BTIA_WATCH_ALLOWED_TARGETS` | Comma-separated IP addresses `watch_online goOnline` may connect to, e.g. `192.0.2.10,192.0.2.11`. Empty or unset: `goOnline` is refused and the tool only reports the state. |

## Safety rules

- **Going online only to an allowed address.** `goOnline` calls `OnlineProvider.GoOnline(ConfigurationAddress)` with
  the given target on the given PG/PC interface (`ConnectionConfiguration.Modes` -> `PcInterfaces` -> `Addresses`).
  The parameterless `GoOnline()`, which connects over the last used interface to the address stored in the project,
  is never called; a test scans the sources for it.
- **Ctrl+T only over a connection this server opened.** In an offline editor Ctrl+T makes TIA Portal open its
  "Go online" dialog and connect by itself over the last used interface. `watch_screen` therefore sends Ctrl+T only
  when `OnlineProvider.State` is `Online` and the connection was opened by `watch_online goOnline` of this server
  process. Otherwise it captures the editor without the shortcut and says why. `sendShortcut=false` never sends it.
- **Ctrl+T is a toggle.** Before sending it, the server reads the pressed state of the editor's "Monitor" toolbar
  button (MSAA) and sends Ctrl+T only when monitoring is off; afterwards it reads the state again. When the button
  cannot be read, Ctrl+T is sent once with a warning. One call sends it at most once.
- **Adopted connections.** If the PLC is already online (for example from an earlier server process), `goOnline`
  still checks `target` against the allowed list and then adopts the connection. Openness does not expose the address
  of an open connection, so the adopted connection is not compared with `target`.
- **Foreign dialogs.** If a new window of the TIA Portal process appears after Ctrl+T, it is closed with Esc and the
  call fails.
- **Focus.** A minimized TIA Portal window is restored without activation before the capture; for the shortcut
  TIA Portal is brought to the front (no Alt key: it opens the menu bar) and the previous foreground window gets the
  focus back after the capture. Keys are only sent when TIA Portal really is the foreground window.
- **No empty snapshots.** `CreateSnapshot()` offline returns without error and leaves the snapshot empty, so
  `watch_db` refuses to run unless the PLC is online.

## Read-only by design

- Nothing writes values to the PLC. The value-loading methods of `ValueService` are not called anywhere in the code,
  and no parameter can reach them. A test scans the sources for them.
- Fail-safe data blocks are refused by `watch_db`.
- Side effects that remain: `watch_db` stores the snapshot in the offline project, so TIA Portal shows the project as
  changed (the server never saves it); `watch_screen` opens an editor and briefly brings TIA Portal to the front.

## Verified so far

Checked against TIA Portal V21 with a real PLC: `goOnline` over a named PG/PC interface, adopting an existing
connection, `watch_db` values of global DBs with structures and arrays, `watch_screen` on a watch table, a tag table
and a LAD block (monitoring state read, Ctrl+T sent only when off), `goOffline`. Call the tools one after another:
two concurrent `watch_screen` calls fight over the active editor.

Not yet checked: instance DBs of GRAPH blocks, very large DBs, floating editors, other monitors
and DPI scaling.

## Requirements

- Windows, .NET Framework 4.8
- Siemens TIA Portal V21 with Openness, running with a project open
- The user is a member of the Windows group `Siemens TIA Openness`
- User environment variable `TiaPortalLocation` pointing to the TIA Portal V21 installation folder

## Build and test

```text
dotnet build BtiaWatch.slnx -c Release
dotnet test BtiaWatch.slnx -c Release
```

The tests need neither TIA Portal nor a PLC: the tools run against a fake `IWatchPortal`, and the window capture is
tested on a window of the test process.

## Register in Claude Code

A running server locks its `.exe`, so build a copy per version and point the client at that copy, e.g.
`dotnet build src\BtiaWatch -c Release -o dist\0.1.5` (`dist/` is ignored by git). Add the server to `.mcp.json`
under the key `BTIA_Watch`:

```json
"BTIA_Watch": {
  "type": "stdio",
  "command": "<repository>\\dist\\0.1.5\\BtiaWatch.exe",
  "args": [],
  "env": { "BTIA_WATCH_ALLOWED_TARGETS": "192.0.2.10" }
}
```

## License

MIT, see [LICENSE](LICENSE).
