---
description: The simpledatalogs Unity package for games and apps; install by git URL or OpenUPM, configure, track game events, autocapture sessions, scenes and errors.
---

# Unity

The `com.xkdlabs.simpledatalogs` package tracks events from Unity games and apps. It has no dependencies, uses no reflection, runs on Mono, IL2CPP and WebGL, and keeps a crash-safe outbox on disk, so an event is never lost to a crash or a killed app.

- Unity 2021.3 or newer.
- Android, iOS, desktop and WebGL players.

## Install

In Unity, open **Window → Package Manager**, click **+**, choose **Add package from git URL…** and enter:

Early access: the `v0.1.0` tag and the OpenUPM listing come with the first release. Until then, use the git URL without `#v0.1.0`.

```
https://github.com/XKD-Labs/simpledatalogs-unity.git#v0.1.0
```

Or with [OpenUPM](https://openupm.com):

```sh
openupm add com.xkdlabs.simpledatalogs
```

## Add it

Configure once at launch, on the main thread, for example in `Awake` of a bootstrap object:

```csharp
using System.Collections.Generic;
using UnityEngine;
using XKDLabs.SimpleDataLogs;

public sealed class Analytics : MonoBehaviour
{
    private void Awake()
    {
        // The opt-out first, then identity and default properties, then the key.
        SimpleDataLogs.SetEnabled(userAllowsUsageStatistics);
        SimpleDataLogs.SetDeviceId(anonymousDeviceId);
        SimpleDataLogs.SetDefaultProperties(new Dictionary<string, object>
        {
            ["app_version"] = Application.version,
            ["platform"] = Application.platform.ToString(),
        });
        SimpleDataLogs.Configure("sdl_pub_...", autocapture: AutocaptureOptions.All);
    }
}
```

Use a **public key** (`sdl_pub_...`). Then track from anywhere:

```csharp
SimpleDataLogs.Track("level_complete", new Dictionary<string, object> { ["level"] = 3, ["duration_s"] = 41, ["score"] = 1200 });
SimpleDataLogs.Track("purchase", new Dictionary<string, object> { ["sku"] = "gem_pack_small", ["price"] = 0.99, ["currency"] = "USD" });
```

Values are strings, numbers or booleans. The Package Manager's **Quick start** sample has a ready-made MonoBehaviour.

## In the first minute

- The first batch goes out 30 seconds after the first event, or at once with `SimpleDataLogs.Flush()`.
- Pausing the game or switching away also sends what is waiting.
- Your events appear on the dashboard's Events page; anything the SDK drops is explained in the Unity console, prefixed `[simpledatalogs]`.
- A `401` in the console means the key is wrong or not live yet; events are kept and sent once it works.

## Autocapture

`autocapture` is off unless you pass it. Combine flags to pick.

| Option | What you get |
|---|---|
| `AppLaunches` | `app_launched` on every launch, with `is_first_launch` |
| `Sessions` | `session_start`, a `session_id` on every event, and `session_end` (`duration_s`) after 30 minutes away |
| `SceneViews` | `screen_view` with the scene name each time a scene loads |
| `Errors` | `app_error` (`message`, `stack`) for logged exceptions and errors, rate limited |

Screens that are not scenes, such as a shop panel, use `SimpleDataLogs.TrackScreen("Shop")`.
With `SetEnabled(false)`, nothing is captured and unsent events are deleted.
