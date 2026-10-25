using System;
using System.Collections.Generic;

namespace XKDLabs.SimpleDataLogs.Core
{
    /// <summary>The app's foreground time between events, for <c>app_state</c> and <c>engaged_ms</c> (active and passive sessions).</summary>
    internal sealed class ActivityTracker
    {
        /// <summary>The longest engaged time one event reports: a day, as the server accepts.</summary>
        internal const long MaxEngagedMilliseconds = 86_400_000;

        /// <summary>True while the app is in front.</summary>
        private bool isInFront;

        /// <summary>When the current foreground stretch started, or the last stamp inside it.</summary>
        private DateTime? activeSinceUtc;

        /// <summary>Foreground time from stretches that ended since the last stamp.</summary>
        private long finishedActiveMilliseconds;

        /// <summary>Starts in the given state, e.g. active when the game launched in front.</summary>
        internal ActivityTracker(bool isActive, DateTime atUtc)
        {
            isInFront = isActive;
            activeSinceUtc = isActive ? atUtc : (DateTime?)null;
        }

        /// <summary>The app came to the front, or left it.</summary>
        internal void SetActive(bool isActive, DateTime atUtc)
        {
            if (isActive == isInFront)
            {
                return;
            }

            // Leaving keeps the stretch for the next stamp; coming back starts a new one.
            if (isInFront)
            {
                finishedActiveMilliseconds += CurrentActiveMilliseconds(atUtc);
            }

            isInFront = isActive;
            activeSinceUtc = isActive ? atUtc : (DateTime?)null;
        }

        /// <summary>The props for one event tracked now, counting afresh from it.</summary>
        internal Dictionary<string, object> Stamp(DateTime atUtc)
        {
            long engagedMilliseconds = Math.Min(finishedActiveMilliseconds + CurrentActiveMilliseconds(atUtc), MaxEngagedMilliseconds);
            finishedActiveMilliseconds = 0;
            activeSinceUtc = isInFront ? atUtc : (DateTime?)null;

            var stampedProperties = new Dictionary<string, object>(StringComparer.Ordinal) { ["app_state"] = isInFront ? "active" : "background" };

            if (isInFront)
            {
                stampedProperties["engaged_ms"] = engagedMilliseconds;
            }

            return stampedProperties;
        }

        // The current stretch's length up to now, never negative.
        private long CurrentActiveMilliseconds(DateTime atUtc)
        {
            if (activeSinceUtc == null)
            {
                return 0;
            }

            return Math.Max((long)(atUtc - activeSinceUtc.Value).TotalMilliseconds, 0);
        }
    }
}
