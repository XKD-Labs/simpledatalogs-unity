using System;
using System.Collections.Generic;
using System.IO;

using NUnit.Framework;

using XKDLabs.SimpleDataLogs.Core;

namespace XKDLabs.SimpleDataLogs.Tests
{
    /// <summary>The session state machine, its file, the error rate limiter and the state-file reader.</summary>
    [TestFixture]
    public sealed class SessionTrackerTests
    {
        /// <summary>The launch moment.</summary>
        private static readonly DateTime LaunchUtc = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

        /// <summary>The first activity starts a session.</summary>
        [Test]
        public void FirstActivityStartsASession()
        {
            var sessionTracker = new SessionTracker(null, MakeIdSource());

            List<SessionChange> changes = sessionTracker.AppBecameActive(LaunchUtc);

            Assert.That(changes.Count, Is.EqualTo(1));
            Assert.That(changes[0].Kind, Is.EqualTo(SessionChangeKind.Started));
            Assert.That(changes[0].SessionId, Is.EqualTo("session-1"));
            Assert.That(sessionTracker.Session.StartedAtUtc, Is.EqualTo(LaunchUtc));
        }

        /// <summary>Coming back within 30 minutes resumes the same session.</summary>
        [Test]
        public void ShortAbsencesResume()
        {
            var sessionTracker = new SessionTracker(null, MakeIdSource());
            sessionTracker.AppBecameActive(LaunchUtc);
            sessionTracker.AppWentToBackground(LaunchUtc.AddMinutes(5));

            List<SessionChange> changes = sessionTracker.AppBecameActive(LaunchUtc.AddMinutes(34).AddSeconds(59));

            Assert.That(changes, Is.Empty);
            Assert.That(sessionTracker.Session.Id, Is.EqualTo("session-1"));
            Assert.That(sessionTracker.Session.IsInBackground, Is.False);
        }

        /// <summary>Thirty minutes away ends the session at its last activity, with its duration, then starts a new one.</summary>
        [Test]
        public void LongAbsencesEndAndStartASession()
        {
            var sessionTracker = new SessionTracker(null, MakeIdSource());
            sessionTracker.AppBecameActive(LaunchUtc);
            sessionTracker.AppWentToBackground(LaunchUtc.AddMinutes(12));

            DateTime returnUtc = LaunchUtc.AddMinutes(42);
            List<SessionChange> changes = sessionTracker.AppBecameActive(returnUtc);

            Assert.That(changes.Count, Is.EqualTo(2));
            Assert.That(changes[0].Kind, Is.EqualTo(SessionChangeKind.Ended));
            Assert.That(changes[0].SessionId, Is.EqualTo("session-1"));
            Assert.That(changes[0].OccurredAtUtc, Is.EqualTo(LaunchUtc.AddMinutes(12)));
            Assert.That(changes[0].DurationSeconds, Is.EqualTo(720));
            Assert.That(changes[1].Kind, Is.EqualTo(SessionChangeKind.Started));
            Assert.That(changes[1].SessionId, Is.EqualTo("session-2"));
            Assert.That(changes[1].OccurredAtUtc, Is.EqualTo(returnUtc));
        }

        /// <summary>Background without a session does nothing.</summary>
        [Test]
        public void BackgroundWithoutASessionIsANoOp()
        {
            var sessionTracker = new SessionTracker(null, MakeIdSource());

            sessionTracker.AppWentToBackground(LaunchUtc);

            Assert.That(sessionTracker.Session, Is.Null);
        }

        /// <summary>The session file round-trips, and a damaged file reads as no session.</summary>
        [Test]
        public void SessionFileRoundTrips()
        {
            using var storage = new TemporaryDirectory();
            var session = new TrackedSession
            {
                Id = "abc\"def",
                StartedAtUtc = LaunchUtc,
                LastActiveAtUtc = LaunchUtc.AddMinutes(3).AddMilliseconds(250),
                IsInBackground = true,
            };

            SessionStore.Write(storage.Path, session);
            TrackedSession readSession = SessionStore.Read(storage.Path);

            Assert.That(readSession.Id, Is.EqualTo(session.Id));
            Assert.That(readSession.StartedAtUtc, Is.EqualTo(session.StartedAtUtc));
            Assert.That(readSession.LastActiveAtUtc, Is.EqualTo(session.LastActiveAtUtc));
            Assert.That(readSession.IsInBackground, Is.True);

            // A damaged file is ignored, and null deletes the file.
            File.WriteAllText(Path.Combine(storage.Path, SessionStore.FileName), "{\"id\":");
            Assert.That(SessionStore.Read(storage.Path), Is.Null);

            SessionStore.Write(storage.Path, null);
            Assert.That(File.Exists(Path.Combine(storage.Path, SessionStore.FileName)), Is.False);
        }

        /// <summary>At most 5 reports per message and 50 per session, until a reset.</summary>
        [Test]
        public void ErrorRateLimiterCapsPerMessageAndPerSession()
        {
            var errorRateLimiter = new ErrorRateLimiter();

            int repeatedReports = 0;
            for (int attemptIndex = 0; attemptIndex < 10; attemptIndex++)
            {
                repeatedReports += errorRateLimiter.ShouldReport("same") ? 1 : 0;
            }

            int distinctReports = 0;
            for (int messageIndex = 0; messageIndex < 100; messageIndex++)
            {
                distinctReports += errorRateLimiter.ShouldReport("message " + messageIndex) ? 1 : 0;
            }

            Assert.That(repeatedReports, Is.EqualTo(5));
            Assert.That(distinctReports, Is.EqualTo(45));

            errorRateLimiter.Reset();
            Assert.That(errorRateLimiter.ShouldReport("same"), Is.True);
        }

        /// <summary>The state-file reader takes flat objects and refuses anything else.</summary>
        [Test]
        public void FlatJsonReaderParsesFlatObjectsOnly()
        {
            Assert.That(FlatJsonReader.TryParse("{\"a\":\"x\\ny\\u0041\",\"b\":-1.5e2,\"c\":true,\"d\":false}", out Dictionary<string, object> values), Is.True);
            Assert.That(values["a"], Is.EqualTo("x\nyA"));
            Assert.That(values["b"], Is.EqualTo(-150.0));
            Assert.That(values["c"], Is.EqualTo(true));
            Assert.That(values["d"], Is.EqualTo(false));

            Assert.That(FlatJsonReader.TryParse(" { } ", out _), Is.True);
            Assert.That(FlatJsonReader.TryParse("{\"a\":{}}", out _), Is.False);
            Assert.That(FlatJsonReader.TryParse("{\"a\":1} trailing", out _), Is.False);
            Assert.That(FlatJsonReader.TryParse("", out _), Is.False);
            Assert.That(FlatJsonReader.TryParse(null, out _), Is.False);
        }

        // Makes ids session-1, session-2 ...
        private static Func<string> MakeIdSource()
        {
            int idCounter = 0;

            return () => "session-" + (++idCounter);
        }
    }
}
