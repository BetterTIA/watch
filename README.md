# BetterTIA Watch

**Read-only live values from a running TIA Portal V21, for AI agents.** An MCP server (stdio) that lets an agent
check the online state of a PLC, read the actual values of data blocks and look at a monitored watch table, tag table
or block - without someone taking screenshots by hand.

It is the fourth tool of the BetterTIA set, next to Reader, Writer and Editor.

> Status: early. The tools build and are tested against a fake TIA Portal; the online behaviour still waits for a
> test against a simulator or test PLC (see below).

## Tools

| Tool | What it does | Source |
| --- | --- | --- |
| `watch_online` | Online state of a PLC (`OnlineProvider.State`); `action` `goOnline` connects to an allowed `target` address over a named `pcInterface`, `goOffline` disconnects | Openness |
| `watch_db` | Actual values of one or more data blocks: `ValueService.CreateSnapshot()`, then `InterfaceSnapshot.Export()`; the SimaticML export is returned as text. Requires the PLC to be online | Openness |
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
- **Ctrl+T is a toggle.** The server cannot tell whether monitoring is already on; one call sends it at most once,
  and the answer warns that a second call may switch monitoring off.
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

## Verified offline, not yet online

Checked against TIA Portal V21 without a PLC: where the services live (`InterfaceSnapshot` on the block,
`ValueService` on the block interface), that Ctrl+T reaches every STEP 7 editor through `SendInput`, and that
`PrintWindow` captures a restored window. Still to be confirmed with a simulator or test PLC:

- `GoOnline(ConfigurationAddress)` with an address created on the chosen PC interface, and whether it needs a
  configured connection path first.
- The structure of a snapshot export with values (to be parsed into name/value rows), and whether the project is
  marked as changed.
- That Ctrl+T switches monitoring on, how long values take to appear (`settleMs`, default 3000 ms), and that monitor
  values are readable in the capture.
- Instance DBs of GRAPH blocks, technology objects; other monitors and DPI scaling.

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

Add the server to `.mcp.json` under the key `BTIA_Watch`:

```json
"BTIA_Watch": {
  "type": "stdio",
  "command": "<repository>\\src\\BtiaWatch\\bin\\Release\\net48\\BtiaWatch.exe",
  "args": []
}
```

## License

MIT, see [LICENSE](LICENSE).
