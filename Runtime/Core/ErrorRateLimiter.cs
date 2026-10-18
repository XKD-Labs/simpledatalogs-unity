using System;
using System.Collections.Generic;

namespace XKDLabs.SimpleDataLogs.Core
{
    /// <summary>Caps <c>app_error</c> events: at most 5 per distinct message and 50 per session.</summary>
    internal sealed class ErrorRateLimiter
    {
        /// <summary>Reports of one message kept per session.</summary>
        public const int MaxReportsPerMessage = 5;

        /// <summary>Reports of any message kept per session.</summary>
        public const int MaxReportsPerSession = 50;

        /// <summary>Reports so far, per message; bounded because the session cap stops new entries.</summary>
        private readonly Dictionary<string, int> reportCountByMessage = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Reports so far this session.</summary>
        private int sessionReportCount;

        /// <summary>Counts one error and says whether it may be sent.</summary>
        public bool ShouldReport(string message)
        {
            string messageKey = message ?? string.Empty;

            // The session budget is checked first, so the dictionary never grows past it.
            if (sessionReportCount >= MaxReportsPerSession)
            {
                return false;
            }

            reportCountByMessage.TryGetValue(messageKey, out int messageReportCount);
            if (messageReportCount >= MaxReportsPerMessage)
            {
                return false;
            }

            reportCountByMessage[messageKey] = messageReportCount + 1;
            sessionReportCount += 1;
            return true;
        }

        /// <summary>Starts a fresh budget, at a new session.</summary>
        public void Reset()
        {
            reportCountByMessage.Clear();
            sessionReportCount = 0;
        }
    }
}
