# simpledatalogs for Unity

The Unity SDK for [simpledatalogs](https://simpledatalogs.com): track events from games and apps, and see them as counters and charts.

- Unity 2021.3 or newer, for Android, iOS, desktop and WebGL players.
- Mono and IL2CPP, with no reflection and no dependencies.
- Follows every [sender rule](https://simpledatalogs.com/docs/api/sender-rules) of the ingest API.
- Full guide: [simpledatalogs.com/docs/sdks/unity](https://simpledatalogs.com/docs/sdks/unity).

## Install

- **Git URL:** Window → Package Manager → **+** → "Add package from git URL…", then enter:

Early access: the `v0.1.0` tag and the OpenUPM listing come with the first release. Until then, use the git URL without `#v0.1.0`.

  ```
  https://github.com/XKD-Labs/simpledatalogs-unity.git#v0.1.0
  ```

- **OpenUPM:**

  ```sh
  openupm add com.xkdlabs.simpledatalogs
  ```

- **manifest.json:** add `"com.xkdlabs.simpledatalogs": "https://github.com/XKD-Labs/simpledatalogs-unity.git#v0.1.0"` to `dependencies`.

The Package Manager also offers a **Quick start** sample with a ready-made MonoBehaviour.

## Quick start

```csharp
using System.Collections.Generic;
using UnityEngine;
using XKDLabs.SimpleDataLogs;

public sealed class Analytics : MonoBehaviour
{
    // Once, at launch: the opt-out first, then identity and default properties, then the key.
    private void Awake()
    {
        SimpleDataLogs.SetEnabled(userAllowsUsageStatistics);
        SimpleDataLogs.SetDeviceId(anonymousDeviceId);
        SimpleDataLogs.SetDefaultProperties(new Dictionary<string, object>
        {
            ["app_version"] = Application.version,
            ["platform"] = Application.platform.ToString(),
        });
        SimpleDataLogs.Configure("sdl_pub_...");
    }
}

// Anywhere; it returns at once and never throws.
SimpleDataLogs.Track("level_complete", new Dictionary<string, object> { ["level"] = 3, ["duration_s"] = 41, ["score"] = 1200 });
```

Use a **public key** (`sdl_pub_...`) in games. The SDK logs a warning if it sees a secret key.
Call `Configure` on the main thread. `Track` and the other calls work from any thread.

To send to another ingest endpoint, such as a local one:

```csharp
SimpleDataLogs.Configure("sdl_pub_...", endpoint: "http://localhost:4001");
```

## Events for games

```csharp
// A level starts and ends.
SimpleDataLogs.Track("level_start", new Dictionary<string, object> { ["level"] = 3 });
SimpleDataLogs.Track("level_complete", new Dictionary<string, object> { ["level"] = 3, ["duration_s"] = 41, ["score"] = 1200 });

// A purchase went through.
SimpleDataLogs.Track("purchase", new Dictionary<string, object> { ["sku"] = "gem_pack_small", ["price"] = 0.99, ["currency"] = "USD" });

// The player moves through the tutorial.
SimpleDataLogs.Track("tutorial_step", new Dictionary<string, object> { ["step"] = 2 });

// A screen that is not its own scene, such as a menu panel.
SimpleDataLogs.TrackScreen("Shop");
```

Property values are strings, integers, floats, doubles or booleans. Anything else (null, lists, dictionaries, objects, NaN, infinity) is dropped with a console line.

## Active and background

Every event tracked now says whether someone was playing, so the Sessions tab can tell active sessions from passive ones:

- `app_state` is `active` while the game is unpaused and focused, else `background`.
- `engaged_ms` is the time in front since the previous event.
- Mark background work yourself with `SimpleDataLogs.Track("sync_finished", new Dictionary<string, object> { ["app_state"] = "background" })`; your value wins.
- Turn it off with `SimpleDataLogs.Configure(key, reportsActivity: false)`.

## Autocapture

One argument sends launches, sessions, scene views and errors without any `Track` call. It is off until you pass it.

```csharp
SimpleDataLogs.Configure("sdl_pub_...", autocapture: AutocaptureOptions.All);
```

| Option | What you get |
|---|---|
| `AppLaunches` | `app_launched` on every launch, with `is_first_launch` |
| `Sessions` | `session_start`, a `session_id` on every event, and `session_end` (`duration_s`) once the game has been away 30 minutes; pause and focus count as away |
| `SceneViews` | `screen_view` with `screen` = the scene name, each time a scene loads (additive loads too) |
| `Errors` | `app_error` (`message`, `stack`, `log_type`) for logged exceptions and errors; at most 5 per distinct message and 50 per session |

`All` is all four; combine flags to pick, e.g. `AutocaptureOptions.Sessions | AutocaptureOptions.SceneViews`.

- Nothing starts on its own: no `RuntimeInitializeOnLoadMethod` sends anything before you call `Configure`.
- The opt-out applies: with `SetEnabled(false)`, nothing is captured.

## API

| Call | What it does |
|---|---|
| `Configure(ingestKey, endpoint, autocapture)` | Starts the SDK once; `autocapture` defaults to none; `endpoint` defaults to `https://edge.simpledatalogs.com`. Later calls are ignored. |
| `Track(eventName, properties)` | Records one event. `event_id` and `ts` are stamped now, when the event happens. |
| `TrackScreen(screenName, properties)` | Records `screen_view` with a `screen` prop. |
| `SetDeviceId(deviceId)` | Sets (or clears, with `null`) the `device_id` on every later event. |
| `SetDefaultProperties(properties)` | Replaces the properties merged into every event's `props`; the event's own properties win on a clash. |
| `SetEnabled(isEnabled)` | The opt-out. `false` ignores new events and **deletes every unsent event**; `true` collects again. |
| `Flush()` | Seals and sends everything now. Undelivered events stay on disk. |

`SimpleDataLogs.SdkVersion` and `SimpleDataLogs.DefaultEndpoint` are constants.

Call `SetEnabled` **before** `Configure`. A player who opted out then never sends a byte, and whatever an earlier launch left on disk is deleted instead of sent.
The opt-out is not persisted by the SDK; your game's own setting is the source of truth, so pass it on every launch.

## Privacy

- The SDK sends only what you track, plus the device id and session id when you set or enable them.
- It never reads `SystemInfo.deviceUniqueIdentifier`, advertising ids or anything else about the device. Pick your own anonymous id; the sample keeps a random one in `PlayerPrefs`.
- Error autocapture sends log messages as they are, so keep personal data out of exception messages, or leave `Errors` off.

## How it behaves

- **Batching.** Events go out in batches of 50, or after the first event of a partial batch has waited 10 s (5 s in the Editor and development builds). Every request is under 256 KB, and bodies of 1 KB or more are gzipped (except on WebGL, where the browser handles encoding).
- **Crash-safe outbox.** Each event is appended to a journal on disk as it is tracked.
  - Batches are sealed into files and deleted only after a `202`.
  - The next launch resends anything left over, including a journal cut short by a crash.
  - A batch file that fails to read three times in a row is renamed `*.unreadable` and skipped, so it cannot block newer batches.
  - Files live in `Application.persistentDataPath/simpledatalogs/`.
- **Bounded.** The outbox keeps at most 10,000 events, 10 MB or 500 batch files. Past that, the oldest batches are dropped, with a console line. While a backoff or a 401 pause holds sending, the batch timer seals nothing, so days offline never leave one file per event.
- **`415`** sets the batch aside as `.unsupported` (never resent unchanged) and the next batch goes.
- **Retries.** Timeouts, network errors, `429`, `503` and any other non-`202` answer are retried with exponential backoff and full jitter, from 1 s up to 5 min, never sooner than 250 ms. `Retry-After` is honored, as seconds or an HTTP-date.
- **Bad key (`401`).** Sending pauses and one configuration error is logged; no event is lost.
  - The SDK re-probes with its smallest batch one minute later, then every 10 minutes, and again on the next launch.
  - Only an answer that proves the key works (`202`, `413` or `415`) ends the pause. A probe lost to the network or a `503` keeps it, and the next probe honors any `Retry-After`.
- **Too large (`413`).** The batch is split in half and resent. A single event that is still too large is dropped, with a console line.
- **Lifecycle.** A hidden `DontDestroyOnLoad` object ticks the SDK a few times a second (ignoring `Time.timeScale`) and flushes when the game pauses, loses focus or quits. Anything a flush cannot deliver in time stays on disk for the next launch.
- **Fail open.** No call throws into your game. Problems are logged to the Unity console with the prefix `[simpledatalogs]`.

## Platform notes

- **IL2CPP and code stripping.** The SDK uses no reflection and a hand-written JSON encoder, so it needs no `link.xml`.
- **WebGL.** Requests go through the browser's `fetch`, so the SDK skips the `User-Agent` header and gzip. The outbox lives in the browser's IndexedDB-backed `persistentDataPath`.
- **Enter Play Mode Options.** With domain reload off, the SDK resets its static state when play mode starts, so call `Configure` again in each play session.

## Development

The pure C# core in `Runtime/Core` has no UnityEngine references and is tested with .NET 8 and NUnit, in Docker:

```sh
docker run --rm -v "$PWD":/w -w "/w/Tests~/DotNet" mcr.microsoft.com/dotnet/sdk:8.0.414 dotnet test
```

That also compiles the core at C# 9 against .NET Standard 2.1, and type-checks the Unity layer against API stubs. The editor tests in `Tests/` run inside Unity; see [CONTRIBUTING.md](CONTRIBUTING.md).

## Links

- Docs: https://simpledatalogs.com/docs
- Changelog: [CHANGELOG.md](CHANGELOG.md)
- Security: [SECURITY.md](SECURITY.md)
- License: [MIT](LICENSE)
