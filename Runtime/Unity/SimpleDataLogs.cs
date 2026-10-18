#if UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using UnityEngine;
using UnityEngine.SceneManagement;

using XKDLabs.SimpleDataLogs.Core;

namespace XKDLabs.SimpleDataLogs
{
    /// <summary>The public entry point: configure once at launch, then track events from anywhere.</summary>
    public static class SimpleDataLogs
    {
        /// <summary>This SDK's version, sent in every User-Agent.</summary>
        public const string SdkVersion = SdkInfo.Version;

        /// <summary>The production ingest host.</summary>
        public const string DefaultEndpoint = SdkInfo.DefaultEndpoint;

        /// <summary>The outbox folder's name under <c>Application.persistentDataPath</c>.</summary>
        public const string StorageFolderName = "simpledatalogs";

        /// <summary>Serializes every call into the single-threaded client.</summary>
        private static readonly object SyncRoot = new object();

        /// <summary>Supplies backoff jitter; only used under SyncRoot.</summary>
        private static readonly System.Random JitterRandom = new System.Random();

        /// <summary>The engine; created on first use, so settings made before Configure are kept.</summary>
        private static SimpleDataLogsClient client;

        /// <summary>The hidden object that ticks the pipeline and hears lifecycle callbacks.</summary>
        private static SimpleDataLogsRunner runner;

        /// <summary>The thread Configure ran on, the only one allowed to start web requests.</summary>
        private static int mainThreadId = -1;

        /// <summary>The scene already sent at Configure, so its own sceneLoaded is not sent twice.</summary>
        private static int? initialSceneHandle;

        /// <summary>True in a WebGL player, where browsers forbid User-Agent and handle compression.</summary>
        private static bool IsWebGlPlayer
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>The engine, created on first use.</summary>
        private static SimpleDataLogsClient Client
        {
            get
            {
                if (client == null)
                {
                    client = new SimpleDataLogsClient(() => DateTime.UtcNow, JitterRandom.NextDouble, () => Guid.NewGuid().ToString("D"));
                }

                return client;
            }
        }

        /// <summary>Starts the SDK; call once on the main thread at launch, after SetEnabled if the user may have opted out.</summary>
        public static void Configure(string ingestKey, string endpoint = DefaultEndpoint, AutocaptureOptions autocapture = AutocaptureOptions.None)
        {
            RunSafely("Configure", () => ConfigureWith(ingestKey, endpoint, autocapture, null, null));
        }

        /// <summary>Records one event; <c>event_id</c> and <c>ts</c> are stamped now. Values: string, integer, float, double or bool.</summary>
        public static void Track(string eventName, IDictionary<string, object> properties = null)
        {
            RunSafely("Track", () => Client.Track(eventName, properties));
        }

        /// <summary>Records a screen view: <c>screen_view</c> with a <c>screen</c> prop.</summary>
        public static void TrackScreen(string screenName, IDictionary<string, object> properties = null)
        {
            RunSafely("TrackScreen", () => Client.TrackScreen(screenName, properties));
        }

        /// <summary>Sets the anonymous device id on every later event, or clears it with null.</summary>
        public static void SetDeviceId(string deviceId)
        {
            RunSafely("SetDeviceId", () => Client.SetDeviceId(deviceId));
        }

        /// <summary>Replaces the properties merged into every later event, e.g. app_version and platform.</summary>
        public static void SetDefaultProperties(IDictionary<string, object> properties)
        {
            RunSafely("SetDefaultProperties", () => Client.SetDefaultProperties(properties));
        }

        /// <summary>The opt-out: false ignores new events and deletes every unsent event; true collects again.</summary>
        public static void SetEnabled(bool isEnabled)
        {
            RunSafely("SetEnabled", () => Client.SetEnabled(isEnabled));
        }

        /// <summary>Seals everything tracked so far and sends it now (or on the next frame, when called off the main thread).</summary>
        public static void Flush()
        {
            RunSafely("Flush", () => Client.Flush(IsOnMainThread()));
        }

        /// <summary>Configures with a test folder and transport, bypassing persistentDataPath and UnityWebRequest.</summary>
        internal static void ConfigureForTests(string ingestKey, string storageDirectory, IIngestTransport transport, AutocaptureOptions autocapture)
        {
            RunSafely("Configure", () => ConfigureWith(ingestKey, DefaultEndpoint, autocapture, storageDirectory, transport));
        }

        /// <summary>The engine, for tests.</summary>
        internal static SimpleDataLogsClient ClientForTests => client;

        /// <summary>Applies answers and sends whatever is due; the runner calls it from Update.</summary>
        internal static void Tick()
        {
            RunSafely("Tick", () => Client.Tick());
        }

        /// <summary>The app paused (mobile background) or resumed.</summary>
        internal static void HandleApplicationPause(bool isPaused)
        {
            RunSafely("OnApplicationPause", () => HandleActivityChange(!isPaused));
        }

        /// <summary>The app lost or regained focus (desktop, WebGL, and mobile alongside pause).</summary>
        internal static void HandleApplicationFocus(bool hasFocus)
        {
            RunSafely("OnApplicationFocus", () => HandleActivityChange(hasFocus));
        }

        /// <summary>The app is quitting: save the session moment and start a last send.</summary>
        internal static void HandleApplicationQuit()
        {
            RunSafely("OnApplicationQuit", () => HandleActivityChange(false));
        }

        /// <summary>Forgets all state and unhooks Unity callbacks; for tests, and for Enter Play Mode without a domain reload.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetState()
        {
            lock (SyncRoot)
            {
                SceneManager.sceneLoaded -= HandleSceneLoaded;
                Application.logMessageReceived -= HandleLogMessage;
                DestroyRunner();

                client = null;
                mainThreadId = -1;
                initialSceneHandle = null;
            }
        }

        // Builds the pipeline options for this platform, starts the client, then the Unity hooks.
        private static void ConfigureWith(string ingestKey, string endpoint, AutocaptureOptions autocapture, string storageDirectory, IIngestTransport transport)
        {
            // SDK lines go to the Unity console unless a test set its own sink.
            if (SdkLog.Sink == null)
            {
                SdkLog.Sink = new UnityLogSink();
            }

            // Browsers forbid User-Agent and negotiate compression themselves.
            var options = new PipelineOptions
            {
                IngestUrl = SdkInfo.BuildIngestUrl(endpoint),
                IngestKey = (ingestKey ?? string.Empty).Trim(),
                StorageDirectory = storageDirectory ?? Path.Combine(Application.persistentDataPath, StorageFolderName),
                UserAgent = IsWebGlPlayer ? null : SdkInfo.UserAgent,
                ShouldGzip = !IsWebGlPlayer,
            };

            if (!Client.Configure(options, transport ?? new UnityWebRequestTransport(), autocapture))
            {
                return;
            }

            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            StartUnityHooks(autocapture);
        }

        // Creates the runner while playing, and subscribes to scene loads and log messages when asked.
        private static void StartUnityHooks(AutocaptureOptions autocapture)
        {
            if (Application.isPlaying)
            {
                CreateRunner();
            }

            if ((autocapture & AutocaptureOptions.SceneViews) != 0)
            {
                SceneManager.sceneLoaded += HandleSceneLoaded;
                TrackActiveSceneIfLoaded();
            }

            if ((autocapture & AutocaptureOptions.Errors) != 0)
            {
                Application.logMessageReceived += HandleLogMessage;
            }
        }

        // Creates the hidden, persistent object that drives the pipeline.
        private static void CreateRunner()
        {
            var runnerObject = new GameObject("SimpleDataLogs");
            runnerObject.hideFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector;
            UnityEngine.Object.DontDestroyOnLoad(runnerObject);
            runner = runnerObject.AddComponent<SimpleDataLogsRunner>();
        }

        // Destroys the runner object, if any.
        private static void DestroyRunner()
        {
            if (runner == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(runner.gameObject);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(runner.gameObject);
            }

            runner = null;
        }

        // The scene that is already open when Configure runs gets its screen view now.
        private static void TrackActiveSceneIfLoaded()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.isLoaded)
            {
                return;
            }

            initialSceneHandle = activeScene.handle;
            Client.TrackScreen(activeScene.name, null);
        }

        // Sends a screen view for each loaded scene, skipping the one Configure already sent.
        private static void HandleSceneLoaded(Scene loadedScene, LoadSceneMode loadMode)
        {
            RunSafely("SceneViews", () =>
            {
                if (initialSceneHandle == loadedScene.handle)
                {
                    initialSceneHandle = null;
                    return;
                }

                initialSceneHandle = null;
                Client.TrackScreen(loadedScene.name, null);
            });
        }

        // Sends logged exceptions and errors as app_error; the SDK's own lines are skipped before taking the lock.
        private static void HandleLogMessage(string condition, string stackTrace, LogType logType)
        {
            bool isErrorLog = logType == LogType.Exception || logType == LogType.Error;
            if (!isErrorLog || SimpleDataLogsClient.IsSdkLogLine(condition))
            {
                return;
            }

            RunSafely("Errors", () => Client.RecordAppError(condition, stackTrace, logType.ToString()));
        }

        // Feeds an active or background moment to sessions; going to the background also flushes.
        private static void HandleActivityChange(bool isActive)
        {
            if (isActive)
            {
                Client.AppBecameActive();
                return;
            }

            Client.AppWentToBackground();
            Client.Flush(IsOnMainThread());
        }

        // True on the thread Configure ran on.
        private static bool IsOnMainThread()
        {
            return Thread.CurrentThread.ManagedThreadId == mainThreadId;
        }

        // Runs one public call under the lock; any failure is logged, never thrown into the game.
        private static void RunSafely(string operationName, Action operation)
        {
            // Console lines wait until the call returns, so log handlers that call the SDK never re-enter it.
            UnityLogSink.BeginHold();

            try
            {
                lock (SyncRoot)
                {
                    operation();
                }
            }
            catch (Exception operationError)
            {
                SdkLog.Error($"{operationName} failed: {operationError.Message}");
            }
            finally
            {
                UnityLogSink.EndHold();
            }
        }
    }
}
#endif
