using System;
using System.Collections.Generic;

using UnityEngine;

using XKDLabs.SimpleDataLogs;

/// <summary>Configures simpledatalogs once at launch and shows typical game events; wire its methods to your own game code.</summary>
public sealed class QuickStartExample : MonoBehaviour
{
    /// <summary>Your public ingest key from the dashboard (Keys page); never ship a secret sdl_sec_ key.</summary>
    [SerializeField]
    private string ingestKey = "sdl_pub_replace_me";

    /// <summary>Your game's own privacy setting; pass it on every launch.</summary>
    [SerializeField]
    private bool userAllowsUsageStatistics = true;

    /// <summary>The PlayerPrefs key holding the anonymous device id.</summary>
    private const string DeviceIdPreferenceKey = "simpledatalogs_device_id";

    /// <summary>True once Configure ran, so reloading this scene never configures twice.</summary>
    private static bool hasConfigured;

    /// <summary>When the current level started, in unscaled seconds.</summary>
    private float levelStartedAtSeconds;

    // Sets the opt-out, identity and defaults first, then configures with every autocapture option.
    private void Awake()
    {
        if (hasConfigured)
        {
            return;
        }

        hasConfigured = true;

        SimpleDataLogs.SetEnabled(userAllowsUsageStatistics);
        SimpleDataLogs.SetDeviceId(LoadOrCreateAnonymousDeviceId());
        SimpleDataLogs.SetDefaultProperties(new Dictionary<string, object>
        {
            ["app_version"] = Application.version,
            ["platform"] = Application.platform.ToString(),
        });
        SimpleDataLogs.Configure(ingestKey, autocapture: AutocaptureOptions.All);
    }

    /// <summary>Call when a level begins.</summary>
    public void StartLevel(int level)
    {
        levelStartedAtSeconds = Time.unscaledTime;
        SimpleDataLogs.Track("level_start", new Dictionary<string, object> { ["level"] = level });
    }

    /// <summary>Call when a level is beaten.</summary>
    public void CompleteLevel(int level, int score)
    {
        float durationSeconds = Time.unscaledTime - levelStartedAtSeconds;

        SimpleDataLogs.Track("level_complete", new Dictionary<string, object>
        {
            ["level"] = level,
            ["duration_s"] = Mathf.Round(durationSeconds),
            ["score"] = score,
        });
    }

    /// <summary>Call when a purchase succeeds.</summary>
    public void RecordPurchase(string sku, double price, string currency)
    {
        SimpleDataLogs.Track("purchase", new Dictionary<string, object>
        {
            ["sku"] = sku,
            ["price"] = price,
            ["currency"] = currency,
        });
    }

    /// <summary>Call as the player moves through the tutorial.</summary>
    public void RecordTutorialStep(int step)
    {
        SimpleDataLogs.Track("tutorial_step", new Dictionary<string, object> { ["step"] = step });
    }

    // A random id kept in PlayerPrefs: anonymous, and reset when the game's data is cleared.
    private static string LoadOrCreateAnonymousDeviceId()
    {
        string deviceId = PlayerPrefs.GetString(DeviceIdPreferenceKey, string.Empty);
        if (!string.IsNullOrEmpty(deviceId))
        {
            return deviceId;
        }

        deviceId = Guid.NewGuid().ToString("D");
        PlayerPrefs.SetString(DeviceIdPreferenceKey, deviceId);
        PlayerPrefs.Save();

        return deviceId;
    }
}
