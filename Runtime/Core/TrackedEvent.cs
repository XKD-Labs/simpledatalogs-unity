using System;
using System.Collections.Generic;

namespace XKDLabs.SimpleDataLogs.Core
{
    /// <summary>One tracked event, stamped at Track time and sent as one NDJSON line.</summary>
    internal sealed class TrackedEvent
    {
        /// <summary>Stamps an event whose properties are already normalized and merged.</summary>
        public TrackedEvent(string eventName, string eventId, DateTime occurredAtUtc, string deviceId, string sessionId, IReadOnlyDictionary<string, object> properties)
        {
            EventName = eventName;
            EventId = eventId;
            OccurredAtUtc = occurredAtUtc;
            DeviceId = deviceId;
            SessionId = sessionId;
            Properties = properties ?? new Dictionary<string, object>();
        }

        /// <summary>The event name, e.g. <c>level_complete</c>.</summary>
        public string EventName { get; }

        /// <summary>The per-event UUID that makes retries safe to dedupe.</summary>
        public string EventId { get; }

        /// <summary>When the host called Track, in UTC.</summary>
        public DateTime OccurredAtUtc { get; }

        /// <summary>The anonymous device id, when the host set one.</summary>
        public string DeviceId { get; }

        /// <summary>The session id, when session autocapture is on.</summary>
        public string SessionId { get; }

        /// <summary>Normalized properties: string, long, ulong, double or bool values.</summary>
        public IReadOnlyDictionary<string, object> Properties { get; }
    }
}
