using System;
using System.Collections.Generic;
using System.IO;

namespace XKDLabs.SimpleDataLogs.Core
{
    /// <summary>
    /// The engine behind the static facade: settings, event stamping, autocapture state and the pipeline.
    /// Not thread-safe; the facade serializes every call, and tests drive it directly with a fake clock.
    /// </summary>
    internal sealed class SimpleDataLogsClient
    {
        /// <summary>The event TrackScreen sends.</summary>
        public const string ScreenViewEventName = "screen_view";

        /// <summary>The marker written on the first launch, beside the outbox.</summary>
        public const string FirstLaunchMarkerName = "first-launch.marker";

        /// <summary>The most characters kept of an error's message and of its stack.</summary>
        public const int MaxErrorTextLength = 4000;

        /// <summary>Reads the current time in UTC.</summary>
        private readonly Func<DateTime> utcNow;

        /// <summary>Supplies the full-jitter fraction in [0, 1).</summary>
        private readonly Func<double> randomFraction;

        /// <summary>Makes event and session ids.</summary>
        private readonly Func<string> newUuid;

        /// <summary>Caps app_error events per message and per session.</summary>
        private readonly ErrorRateLimiter errorRateLimiter = new ErrorRateLimiter();

        /// <summary>Properties merged into every event's props.</summary>
        private Dictionary<string, object> defaultProperties = new Dictionary<string, object>(StringComparer.Ordinal);

        /// <summary>The app's foreground time between events, once the Unity layer reports the app's state.</summary>
        private ActivityTracker activityTracker;

        /// <summary>The session state machine, when session autocapture is on.</summary>
        private SessionTracker sessionTracker;

        /// <summary>Wires the client to its clock, randomness and id source; nothing is sent until Configure.</summary>
        public SimpleDataLogsClient(Func<DateTime> utcNow, Func<double> randomFraction, Func<string> newUuid)
        {
            this.utcNow = utcNow;
            this.randomFraction = randomFraction;
            this.newUuid = newUuid;
        }

        /// <summary>The pipeline, once configured.</summary>
        public EventPipeline Pipeline { get; private set; }

        /// <summary>True once Configure ran.</summary>
        public bool IsConfigured => Pipeline != null;

        /// <summary>False after an opt-out.</summary>
        public bool IsEnabled { get; private set; } = true;

        /// <summary>The anonymous device id on every event, if set.</summary>
        public string DeviceId { get; private set; }

        /// <summary>The current session id, when session autocapture is on.</summary>
        public string SessionId { get; private set; }

        /// <summary>The autocapture features turned on.</summary>
        public AutocaptureOptions Autocapture { get; private set; }

        /// <summary>The outbox folder, once configured.</summary>
        public string StorageDirectory { get; private set; }

        /// <summary>Starts the pipeline and autocapture once; returns false (with a warning) on a second call.</summary>
        public bool Configure(PipelineOptions options, IIngestTransport transport, AutocaptureOptions autocapture)
        {
            if (IsConfigured)
            {
                SdkLog.Warning("Configure was called again; the first configuration stays in effect");
                return false;
            }

            WarnAboutSuspiciousKey(options.IngestKey);

            // Recover the outbox first, so leftovers from the last launch go out before anything new.
            DateTime nowUtc = utcNow();
            StorageDirectory = options.StorageDirectory;
            Pipeline = new EventPipeline(options, transport, randomFraction, IsEnabled);
            Pipeline.Start(nowUtc);

            StartAutocapture(autocapture, nowUtc);
            return true;
        }

        /// <summary>Records one event, stamping event_id and ts now; it never throws.</summary>
        public void Track(string eventName, IDictionary<string, object> properties)
        {
            // An empty name cannot become an event.
            if (string.IsNullOrEmpty(eventName))
            {
                SdkLog.Error("Track was called with an empty event name; event dropped");
                return;
            }

            // Tracking before Configure is a setup bug worth a log line.
            if (!IsConfigured)
            {
                SdkLog.Error($"Track({eventName}) was called before Configure; event dropped");
                return;
            }

            if (!IsEnabled)
            {
                return;
            }

            // Drop unsupported values, then let the call site win over app_state and the defaults.
            Dictionary<string, object> eventProperties = PropertyNormalizer.Normalize(properties, eventName);
            DateTime occurredAtUtc = utcNow();
            Dictionary<string, object> stampedDefaults = activityTracker == null ? defaultProperties : PropertyNormalizer.Merge(defaultProperties, activityTracker.Stamp(occurredAtUtc));
            JournalEvent(eventName, PropertyNormalizer.Merge(stampedDefaults, eventProperties), occurredAtUtc, SessionId);
        }

        /// <summary>Records <c>screen_view</c> with a <c>screen</c> prop.</summary>
        public void TrackScreen(string screenName, IDictionary<string, object> properties)
        {
            if (string.IsNullOrEmpty(screenName))
            {
                SdkLog.Error("TrackScreen was called with an empty screen name; event dropped");
                return;
            }

            // The screen name wins over a same-named property.
            var screenProperties = properties == null
                ? new Dictionary<string, object>(StringComparer.Ordinal)
                : new Dictionary<string, object>(properties, StringComparer.Ordinal);
            screenProperties["screen"] = screenName;

            Track(ScreenViewEventName, screenProperties);
        }

        /// <summary>Sets the anonymous device id on every later event, or clears it with null.</summary>
        public void SetDeviceId(string deviceId)
        {
            DeviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
        }

        /// <summary>Replaces the properties merged into every later event.</summary>
        public void SetDefaultProperties(IDictionary<string, object> properties)
        {
            defaultProperties = PropertyNormalizer.Normalize(properties, "default properties");
        }

        /// <summary>Turns collection on or off; off also deletes every event not yet delivered.</summary>
        public void SetEnabled(bool isEnabled)
        {
            IsEnabled = isEnabled;
            Pipeline?.SetEnabled(isEnabled);
        }

        /// <summary>Seals everything tracked so far, and sends it now when the caller may start requests.</summary>
        public void Flush(bool shouldSendNow)
        {
            Pipeline?.Flush(utcNow(), shouldSendNow);
        }

        /// <summary>Applies answers and sends whatever is due.</summary>
        public void Tick()
        {
            Pipeline?.Tick(utcNow());
        }

        /// <summary>The app came to the front or left it; the first call turns on <c>app_state</c> and <c>engaged_ms</c>.</summary>
        public void SetAppActive(bool isActive)
        {
            if (activityTracker == null)
            {
                activityTracker = new ActivityTracker(isActive, utcNow());
                return;
            }

            activityTracker.SetActive(isActive, utcNow());
        }

        /// <summary>The app became active: may end a stale session and start a new one.</summary>
        public void AppBecameActive()
        {
            if (sessionTracker == null)
            {
                return;
            }

            ApplySessionChanges(sessionTracker.AppBecameActive(utcNow()));
        }

        /// <summary>The app went to the background: saves when, so a later launch can end the session.</summary>
        public void AppWentToBackground()
        {
            if (sessionTracker == null)
            {
                return;
            }

            sessionTracker.AppWentToBackground(utcNow());
            SessionStore.Write(StorageDirectory, sessionTracker.Session);
        }

        /// <summary>Sends a logged exception or error as <c>app_error</c>, within the rate limits; SDK lines are skipped.</summary>
        public void RecordAppError(string message, string stackTrace, string logType)
        {
            // Only when asked for, and never for the SDK's own log lines, which would loop.
            bool isErrorCaptureOn = (Autocapture & AutocaptureOptions.Errors) != 0;
            if (!isErrorCaptureOn || !IsConfigured || !IsEnabled || IsSdkLogLine(message))
            {
                return;
            }

            if (!errorRateLimiter.ShouldReport(message))
            {
                return;
            }

            var errorProperties = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["message"] = Truncate(message),
                ["stack"] = Truncate(stackTrace),
                ["log_type"] = logType ?? string.Empty,
            };

            TrackAutocaptured("app_error", errorProperties, utcNow(), SessionId);
        }

        /// <summary>True for a line the SDK itself logged.</summary>
        public static bool IsSdkLogLine(string message)
        {
            return message != null && message.StartsWith(SdkLog.Tag, StringComparison.Ordinal);
        }

        // Turns on the asked-for autocapture: the session first, so app_launched carries it.
        private void StartAutocapture(AutocaptureOptions autocapture, DateTime nowUtc)
        {
            Autocapture = autocapture;

            if ((autocapture & AutocaptureOptions.Sessions) != 0)
            {
                sessionTracker = new SessionTracker(SessionStore.Read(StorageDirectory), newUuid);
                ApplySessionChanges(sessionTracker.AppBecameActive(nowUtc));
            }

            if ((autocapture & AutocaptureOptions.AppLaunches) != 0)
            {
                var launchProperties = new Dictionary<string, object>(StringComparer.Ordinal) { ["is_first_launch"] = MarkFirstLaunch() };
                TrackAutocaptured("app_launched", launchProperties, nowUtc, SessionId);
            }
        }

        // Sends what a session moment produced, each event with its own session id, then saves the session.
        private void ApplySessionChanges(List<SessionChange> sessionChanges)
        {
            foreach (SessionChange sessionChange in sessionChanges)
            {
                if (sessionChange.Kind == SessionChangeKind.Ended)
                {
                    var endProperties = new Dictionary<string, object>(StringComparer.Ordinal) { ["duration_s"] = sessionChange.DurationSeconds };
                    TrackAutocaptured("session_end", endProperties, sessionChange.OccurredAtUtc, sessionChange.SessionId);
                    continue;
                }

                // A new session gets a fresh error budget.
                SessionId = sessionChange.SessionId;
                errorRateLimiter.Reset();
                TrackAutocaptured("session_start", new Dictionary<string, object>(), sessionChange.OccurredAtUtc, sessionChange.SessionId);
            }

            SessionId = sessionTracker.Session?.Id;
            SessionStore.Write(StorageDirectory, sessionTracker.Session);
        }

        // Journals an autocaptured event with its own time and session id.
        private void TrackAutocaptured(string eventName, Dictionary<string, object> properties, DateTime occurredAtUtc, string eventSessionId)
        {
            if (!IsConfigured || !IsEnabled)
            {
                return;
            }

            JournalEvent(eventName, PropertyNormalizer.Merge(defaultProperties, properties), occurredAtUtc, eventSessionId);
        }

        // Stamps a new event id and hands the event to the pipeline.
        private void JournalEvent(string eventName, Dictionary<string, object> mergedProperties, DateTime occurredAtUtc, string eventSessionId)
        {
            var trackedEvent = new TrackedEvent(eventName, newUuid(), occurredAtUtc, DeviceId, eventSessionId, mergedProperties);

            Pipeline.Track(trackedEvent, utcNow());
        }

        // True on the very first launch; writes the marker so later launches say false.
        private bool MarkFirstLaunch()
        {
            string markerPath = Path.Combine(StorageDirectory, FirstLaunchMarkerName);

            try
            {
                if (File.Exists(markerPath))
                {
                    return false;
                }

                Directory.CreateDirectory(StorageDirectory);
                File.WriteAllBytes(markerPath, new byte[0]);
            }
            catch (Exception markerError)
            {
                SdkLog.Warning($"Could not write the first-launch marker: {markerError.Message}");
            }

            return true;
        }

        // Logs an empty key, or a secret key that belongs on servers only.
        private static void WarnAboutSuspiciousKey(string ingestKey)
        {
            if (string.IsNullOrEmpty(ingestKey))
            {
                SdkLog.Error("Configure was given an empty key; ingest will answer 401");
            }
            else if (ingestKey.StartsWith(SdkInfo.SecretKeyPrefix, StringComparison.Ordinal))
            {
                SdkLog.Warning("A secret sdl_sec_ key was configured; apps should ship a public sdl_pub_ key");
            }
        }

        // Keeps at most the first 4,000 characters.
        private static string Truncate(string text)
        {
            if (text == null)
            {
                return string.Empty;
            }

            return text.Length <= MaxErrorTextLength ? text : text.Substring(0, MaxErrorTextLength);
        }
    }
}
