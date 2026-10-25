# Changelog

All notable changes to the simpledatalogs Unity package. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the package uses [Semantic Versioning](https://semver.org/).

## [0.1.0] - Unreleased

The first public release.

### Added

- `SimpleDataLogs.Configure`, `Track`, `TrackScreen`, `SetDeviceId`, `SetDefaultProperties`, `SetEnabled` and `Flush` for Unity 2021.3+, on Mono and IL2CPP, including WebGL.
- Every event tracked now carries `app_state` (`active` while the game is unpaused and focused, else `background`) and, while active, `engaged_ms`, so dashboards can split active and passive sessions. `reportsActivity: false` on `Configure` turns it off.
- A partial batch goes out 10 s after its first event, or 5 s in the Editor and development builds, so new events show within seconds.
- A pure C# core with a hand-written JSON encoder: no reflection, no dependencies.
- A crash-safe disk outbox under `Application.persistentDataPath`, with batching, gzip, retries with jittered backoff, `Retry-After`, 401 pauses, 413 splitting and 415 set-asides.
- Bounded storage: at most 10,000 events, 10 MB or 500 batch files.
- Flushes on pause, focus loss and quit.
- Opt-in autocapture of launches, sessions, scene views and logged errors.
- A Quick start sample.
