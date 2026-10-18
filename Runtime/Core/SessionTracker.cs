using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace XKDLabs.SimpleDataLogs.Core
{
    /// <summary>A session as stored between launches.</summary>
    internal sealed class TrackedSession
    {
        /// <summary>The id sent as <c>session_id</c>.</summary>
        public string Id { get; set; }

        /// <summary>When the session began.</summary>
        public DateTime StartedAtUtc { get; set; }

        /// <summary>The last moment the app was active, or when it went to the background.</summary>
        public DateTime LastActiveAtUtc { get; set; }

        /// <summary>True while the app is in the background.</summary>
        public bool IsInBackground { get; set; }
    }

    /// <summary>What a session moment produced.</summary>
    internal enum SessionChangeKind
    {
        /// <summary>A session began.</summary>
        Started,

        /// <summary>A session ended at its last activity.</summary>
        Ended,
    }

    /// <summary>One session start or end, in the order it happened.</summary>
    internal sealed class SessionChange
    {
        /// <summary>Records one change.</summary>
        public SessionChange(SessionChangeKind kind, string sessionId, DateTime occurredAtUtc, long durationSeconds)
        {
            Kind = kind;
            SessionId = sessionId;
            OccurredAtUtc = occurredAtUtc;
            DurationSeconds = durationSeconds;
        }

        /// <summary>Started or ended.</summary>
        public SessionChangeKind Kind { get; }

        /// <summary>The session it concerns.</summary>
        public string SessionId { get; }

        /// <summary>When it started, or the last activity it ended at.</summary>
        public DateTime OccurredAtUtc { get; }

        /// <summary>For an end, how long the session lasted.</summary>
        public long DurationSeconds { get; }
    }

    /// <summary>The session state machine: active and background moments in, session starts and ends out.</summary>
    internal sealed class SessionTracker
    {
        /// <summary>Time away that ends a session, as in most app analytics.</summary>
        public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

        /// <summary>Makes new session ids; tests pin it.</summary>
        private readonly Func<string> newSessionId;

        /// <summary>Starts from a stored session, or none.</summary>
        public SessionTracker(TrackedSession storedSession, Func<string> newSessionId)
        {
            Session = storedSession;
            this.newSessionId = newSessionId;
        }

        /// <summary>The current session, if any.</summary>
        public TrackedSession Session { get; private set; }

        /// <summary>The app became active: ends a session idle for the whole timeout, then starts one or resumes the current one.</summary>
        public List<SessionChange> AppBecameActive(DateTime nowUtc)
        {
            var changes = new List<SessionChange>();

            // A session away for the whole timeout ends where its activity stopped.
            if (Session != null && nowUtc - Session.LastActiveAtUtc >= Timeout)
            {
                long durationSeconds = Math.Max(0, (long)Math.Round((Session.LastActiveAtUtc - Session.StartedAtUtc).TotalSeconds));
                changes.Add(new SessionChange(SessionChangeKind.Ended, Session.Id, Session.LastActiveAtUtc, durationSeconds));
                Session = null;
            }

            // No session: start one; otherwise resume it.
            if (Session == null)
            {
                Session = new TrackedSession { Id = newSessionId(), StartedAtUtc = nowUtc, LastActiveAtUtc = nowUtc, IsInBackground = false };
                changes.Add(new SessionChange(SessionChangeKind.Started, Session.Id, nowUtc, 0));
            }
            else
            {
                Session.LastActiveAtUtc = nowUtc;
                Session.IsInBackground = false;
            }

            return changes;
        }

        /// <summary>The app went to the background: remembers when, so the next activity can decide whether the session ended.</summary>
        public void AppWentToBackground(DateTime nowUtc)
        {
            if (Session == null)
            {
                return;
            }

            Session.LastActiveAtUtc = nowUtc;
            Session.IsInBackground = true;
        }
    }

    /// <summary>Reads and writes <c>session.json</c> beside the outbox, so a relaunch within 30 minutes resumes the session.</summary>
    internal static class SessionStore
    {
        /// <summary>The session file's name.</summary>
        public const string FileName = "session.json";

        /// <summary>The epoch stored times count from.</summary>
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>The session a previous launch saved, or null when missing or unreadable.</summary>
        public static TrackedSession Read(string directory)
        {
            try
            {
                string sessionFilePath = Path.Combine(directory, FileName);
                if (!File.Exists(sessionFilePath))
                {
                    return null;
                }

                // A damaged file means no session to resume.
                if (!FlatJsonReader.TryParse(File.ReadAllText(sessionFilePath, Encoding.UTF8), out Dictionary<string, object> values))
                {
                    return null;
                }

                // Every field must be present with the right kind; a single & runs every lookup.
                bool hasEveryField = values.TryGetValue("id", out object idValue) & values.TryGetValue("started_at_ms", out object startedValue)
                    & values.TryGetValue("last_active_at_ms", out object lastActiveValue) & values.TryGetValue("is_in_background", out object backgroundValue);

                if (!hasEveryField || !(idValue is string) || !(startedValue is double) || !(lastActiveValue is double) || !(backgroundValue is bool))
                {
                    return null;
                }

                return new TrackedSession
                {
                    Id = (string)idValue,
                    StartedAtUtc = UnixEpoch.AddMilliseconds((double)startedValue),
                    LastActiveAtUtc = UnixEpoch.AddMilliseconds((double)lastActiveValue),
                    IsInBackground = (bool)backgroundValue,
                };
            }
            catch (Exception readError)
            {
                SdkLog.Warning($"Could not read the stored session: {readError.Message}");
                return null;
            }
        }

        /// <summary>Saves the session, or deletes the file when there is none; failures only cost resumption.</summary>
        public static void Write(string directory, TrackedSession session)
        {
            string sessionFilePath = Path.Combine(directory, FileName);

            try
            {
                if (session == null)
                {
                    File.Delete(sessionFilePath);
                    return;
                }

                // Write a temporary file and swap it in, so a crash never leaves half a session.
                Directory.CreateDirectory(directory);
                string temporaryPath = sessionFilePath + ".tmp";
                File.WriteAllText(temporaryPath, Encode(session), new UTF8Encoding(false));
                File.Delete(sessionFilePath);
                File.Move(temporaryPath, sessionFilePath);
            }
            catch (Exception writeError)
            {
                SdkLog.Warning($"Could not save the session: {writeError.Message}");
            }
        }

        // Encodes the session as one flat JSON object.
        private static string Encode(TrackedSession session)
        {
            var jsonBuilder = new StringBuilder();

            jsonBuilder.Append("{\"id\":");
            EventEncoder.AppendJsonString(jsonBuilder, session.Id);
            jsonBuilder.Append(",\"started_at_ms\":");
            jsonBuilder.Append(ToEpochMilliseconds(session.StartedAtUtc).ToString(CultureInfo.InvariantCulture));
            jsonBuilder.Append(",\"last_active_at_ms\":");
            jsonBuilder.Append(ToEpochMilliseconds(session.LastActiveAtUtc).ToString(CultureInfo.InvariantCulture));
            jsonBuilder.Append(",\"is_in_background\":");
            jsonBuilder.Append(session.IsInBackground ? "true" : "false");
            jsonBuilder.Append('}');

            return jsonBuilder.ToString();
        }

        // Whole milliseconds since 1970.
        private static long ToEpochMilliseconds(DateTime momentUtc)
        {
            return (long)(momentUtc - UnixEpoch).TotalMilliseconds;
        }
    }
}
