# Change Log

## [Unreleased]

### Added

- Skeleton of the MCP server (stdio, .NET Framework 4.8, TIA Portal Openness V21) with three read-only tools:
  `watch_online`, `watch_db`, `watch_screen`.
- TIA Portal access behind `IWatchPortal`; tests run against a fake without TIA Portal.
- Window capture with `PrintWindow`, tested on a window of the test process.
- Source guard test: no value-writing Openness API in the code.
