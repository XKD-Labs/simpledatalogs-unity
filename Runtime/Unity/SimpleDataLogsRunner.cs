#if UNITY_5_3_OR_NEWER
using UnityEngine;

namespace XKDLabs.SimpleDataLogs
{
    /// <summary>The hidden object Configure creates: it ticks the pipeline and forwards lifecycle callbacks. Never add it by hand.</summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class SimpleDataLogsRunner : MonoBehaviour
    {
        /// <summary>How often Update ticks the pipeline, in unscaled seconds.</summary>
        private const float TickIntervalSeconds = 0.2f;

        /// <summary>Time left until the next tick.</summary>
        private float secondsUntilNextTick;

        // Ticks the pipeline a few times a second, ignoring Time.timeScale so paused games still send.
        private void Update()
        {
            secondsUntilNextTick -= Time.unscaledDeltaTime;
            if (secondsUntilNextTick > 0)
            {
                return;
            }

            secondsUntilNextTick = TickIntervalSeconds;
            SimpleDataLogs.Tick();
        }

        // Mobile background and foreground.
        private void OnApplicationPause(bool isPaused)
        {
            SimpleDataLogs.HandleApplicationPause(isPaused);
        }

        // Desktop and WebGL focus changes.
        private void OnApplicationFocus(bool hasFocus)
        {
            SimpleDataLogs.HandleApplicationFocus(hasFocus);
        }

        // The last chance to start a send; whatever does not finish stays on disk.
        private void OnApplicationQuit()
        {
            SimpleDataLogs.HandleApplicationQuit();
        }
    }
}
#endif
