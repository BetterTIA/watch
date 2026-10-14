# Change Log

## [Unreleased]

### Changed

- `watch_online goOnline` connects only to an address listed in `BTIA_WATCH_ALLOWED_TARGETS`, over a named PG/PC
  interface (`GoOnline(ConfigurationAddress)`); the parameterless `GoOnline()` is never used. Empty list = disabled.
- `watch_db` refuses to run unless the PLC is online, because an offline snapshot is silently empty.
  `InterfaceSnapshot` is taken from the block only (verified location); the fallback is gone.
- `watch_screen` sends Ctrl+T ("Monitor all") with `SendInput`, only over a connection this server opened, at most
  once per call; `monitorShortcut` is replaced by `sendShortcut` (default true). Restores a minimized window, gives the
  focus back after the capture, closes a new TIA Portal dialog with Esc and fails. Default wait 3000 ms.
- Source guard also rejects the parameterless `GoOnline()`.

### Added

- Skeleton of the MCP server (stdio, .NET Framework 4.8, TIA Portal Openness V21) with three read-only tools:
  `watch_online`, `watch_db`, `watch_screen`.
- TIA Portal access behind `IWatchPortal`; tests run against a fake without TIA Portal.
- Window capture with `PrintWindow`, tested on a window of the test process.
- Source guard test: no value-writing Openness API in the code.
