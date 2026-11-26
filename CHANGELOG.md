# Change Log

## Unreleased

### Added

- `watch_db` parameter `maxRows` (default 500, 1 to 20000): at most that many rows per block. A longer block is cut
  with a line `truncated: true (N of M rows not shown, limit L; ...)`; `members` narrows the answer first.
- `watch_db` ends with `projectMarkedModified: true|false|unknown` (`Project.IsModified` read before and after the
  snapshots). The snapshot is stored in the offline project and Openness has no undo, so the state cannot be restored;
  the answer says whether the snapshot or an earlier change caused it. The project is never saved.
- `watch_screen` finds an undocked (floating) editor: a visible top-level window of the TIA Portal process whose title
  contains the object name is used for the capture, the Monitor button and Ctrl+T. Not yet checked against TIA Portal;
  when no such window is found, the main window is used and the warning for an unreadable monitoring state says so.

### Changed

- `watch_screen` switches monitoring on by pressing the editor's Monitor button through MSAA (`accDoDefaultAction`),
  so TIA Portal is not brought to the front and the user keeps the focus. Ctrl+T (with `SetForegroundWindow` and
  `SendInput`) is only the fallback when no button can be pressed or monitoring still reads as off; the result says
  which path was used. Same rule as before: nothing is pressed or sent unless this server opened the connection.
  Not yet checked against TIA Portal.
- `watch_db` leaves out section rows (`Type="Section"`: Input, Output, Static...), so only values remain; they do not
  count in `Members: x of y` either.
- README: adopted connections, the side effect of creating a PG/PC address on `goOnline`, and what is tested only
  against the fake `IWatchPortal` (F-DB refusal).

### Fixed

- Outdated source comment on `GoOnline` ("not yet tried against a PLC"); it was verified live with 0.1.3.

## 0.1.5

### Added

- `watch_db` on a technology object: Openness V21 refuses `Interface` on `TechnologicalInstanceDB`
  ("'Interface' is not supported by type", live test), which is the only way to `ValueService`. The call now asks the
  object itself for `ValueService` and, when there is none, says that technology objects give no actual values and
  points to a watch table with `watch_screen` or to BTIA_Reader `read_technology_object`.

## 0.1.4

### Added

- `watch_db` reads technology objects (`PID_Temp`, `PID_Compact`, axes...): their instance DBs sit in the technology
  objects folder (`PlcSoftware.TechnologicalObjectGroup`), not among the program blocks, so they were reported as
  "not found". In Openness V21 `TechnologicalInstanceDB` derives from `InstanceDB`; the snapshot is taken the same way.
  Not yet checked against a PLC.
- `watch_db` parameter `members`: comma-separated member paths; only members equal to a path or below it are
  returned (case-insensitive, whole path segments: `A.B` matches `A.B.C` and `A.B[2]`, not `A.BC`), with a line
  `Members: <shown> of <all>`. Without a match the top-level members are listed. Keeps snapshots of large instance
  DBs with multi-instances short.

## 0.1.3

### Fixed

- `watch_online goOnline` failed with "Error when calling method 'GoOnline'" when TIA Portal was still online from an
  earlier server process, and `watch_screen` then refused Ctrl+T. When the PLC is already `Online`, goOnline now checks
  the target against the allowed list as before, skips `GoOnline()`, adopts the connection as opened by this server and
  returns `State: Online (already online; connection adopted for <target>)`. Limitation: Openness V21 does not
  expose the address an open connection uses (`OnlineProvider.Configuration` only lists modes, PC interfaces and
  `IsConfigured`), so the adopted connection is not compared with `target`.

## 0.1.2

### Fixed

- The monitoring state is read correctly when monitoring is off: TIA then turns the toolbar button into a push button
  instead of clearing CHECKED.

## 0.1.0 to 0.1.1

The commits of these two versions are not tagged, so the split between them is not recorded.

### Added

- Skeleton of the MCP server (stdio, .NET Framework 4.8, TIA Portal Openness V21) with three read-only tools:
  `watch_online`, `watch_db`, `watch_screen`.
- TIA Portal access behind `IWatchPortal`; tests run against a fake without TIA Portal.
- Window capture with `PrintWindow`, tested on a window of the test process.
- Source guard test: no value-writing Openness API in the code.

### Changed

- `watch_online goOnline` connects only to an address listed in `BTIA_WATCH_ALLOWED_TARGETS`, over a named PG/PC
  interface (`GoOnline(ConfigurationAddress)`); the parameterless `GoOnline()` is never used. Empty list = disabled.
- `watch_db` refuses to run unless the PLC is online, because an offline snapshot is silently empty.
  `InterfaceSnapshot` is taken from the block only (verified location); the fallback is gone.
- `watch_screen` sends Ctrl+T ("Monitor all") with `SendInput`, only over a connection this server opened, at most
  once per call; `monitorShortcut` is replaced by `sendShortcut` (default true). Restores a minimized window, gives the
  focus back after the capture, closes a new TIA Portal dialog with Esc and fails. Default wait 3000 ms.
- Source guard also rejects the parameterless `GoOnline()`.
- `watch_screen` reads whether the editor already monitors (pressed state of its "Monitor" toolbar button over MSAA,
  about 0.5-0.8 s) and sends Ctrl+T only when it does not; after the wait it reads the state again and warns when
  monitoring is still off. When the button cannot be found, Ctrl+T is sent once with a warning, as before.
- `watch_screen` retries the monitoring state read every 200 ms until the editor toolbar answers, for at most
  `settleMs`, before deciding on Ctrl+T (the toolbar is not drawn right after `ShowInEditor()`, live, V21).

### Fixed

- `watch_db` returned only the block name: the values sit in the read-only `Snapshot` attribute, which
  `InterfaceSnapshot.Export` leaves out unless `ExportOptions.WithReadOnly` is set (verified online, V21). The tool now
  returns `Snapshot taken: <time>` and one `path = value (type)` line per member (`A.B[2].C`); an export without
  values is returned raw.
