# BetterTIA Watch

**Read-only live values from a running TIA Portal V21, for AI agents.** An MCP server (stdio) that lets an agent
check the online state of a PLC, read the actual values of data blocks and look at a monitored watch table, tag table
or block - without someone taking screenshots by hand.

It is the fourth tool of the BetterTIA set, next to Reader, Writer and Editor.

> Status: skeleton. The tools build and are tested against a fake TIA Portal; the parts marked *to be confirmed*
> below wait for a test against a running TIA Portal.

## Tools

| Tool | What it does | Source |
| --- | --- | --- |
| `watch_online` | Online state of a PLC (`OnlineProvider.State`); with `action` `goOnline` / `goOffline` it opens or closes the TIA Portal online connection first | Openness |
| `watch_db` | Actual values of one or more data blocks: `ValueService.CreateSnapshot()`, then `InterfaceSnapshot.Export()`; the SimaticML export is returned as text | Openness |
| `watch_screen` | Opens a watch table, tag table or block (`ShowInEditor()`), sends the "Monitor all" keyboard shortcut to the TIA Portal window and returns a PNG capture of the window (`PrintWindow`) as MCP image content | Openness + Win32 |

Common parameters: `plc` (PLC software name, optional when the project has one PLC) and `portalId` (process id,
optional when one TIA Portal is running). The server attaches to a running TIA Portal; it never starts one.

## Read-only by design

- Nothing writes values to the PLC. The value-loading methods of `ValueService` are not called anywhere in the code,
  and no parameter can reach them. A test scans the sources for them.
- Fail-safe data blocks are refused by `watch_db`.
- Side effects that remain: `watch_db` stores the snapshot in the offline project, so TIA Portal shows the project as
  changed (the server never saves it); `watch_screen` opens an editor and brings the TIA Portal window to the front
  to send the shortcut. Keys are only sent when TIA Portal really is the foreground window.

## To be confirmed against a running TIA Portal

- The keyboard shortcut for "Monitor all" (`monitorShortcut`, SendKeys notation). The default is empty, which sends
  no keys; set it per call until the default is filled in.
- The wait after opening the editor and after the shortcut (`settleMs`, default 2000 ms).
- Whether `CreateSnapshot()` needs an online connection, and where the `InterfaceSnapshot` service is offered
  (block or block interface; both are tried).
- The structure of the snapshot export, which `watch_db` will parse into name/value rows.

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
