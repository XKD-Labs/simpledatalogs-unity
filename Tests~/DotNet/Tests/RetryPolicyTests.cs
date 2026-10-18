using System;

using NUnit.Framework;

using XKDLabs.SimpleDataLogs.Core;

namespace XKDLabs.SimpleDataLogs.Tests
{
    /// <summary>Backoff bounds, Retry-After floors and the response table.</summary>
    [TestFixture]
    public sealed class RetryPolicyTests
    {
        /// <summary>A fixed "now" for HTTP-date parsing.</summary>
        private static readonly DateTime NowUtc = new DateTime(1994, 11, 6, 8, 49, 0, DateTimeKind.Utc);

        /// <summary>The ceiling doubles from 1 s and stops at 5 min, even after absurd failure counts.</summary>
        [Test]
        public void CeilingDoublesUpToFiveMinutes()
        {
            var retryPolicy = new RetryPolicy();

            Assert.That(retryPolicy.BackoffCeilingSeconds(1), Is.EqualTo(1));
            Assert.That(retryPolicy.BackoffCeilingSeconds(2), Is.EqualTo(2));
            Assert.That(retryPolicy.BackoffCeilingSeconds(5), Is.EqualTo(16));
            Assert.That(retryPolicy.BackoffCeilingSeconds(9), Is.EqualTo(256));
            Assert.That(retryPolicy.BackoffCeilingSeconds(10), Is.EqualTo(300));
            Assert.That(retryPolicy.BackoffCeilingSeconds(int.MaxValue), Is.EqualTo(300));
            Assert.That(retryPolicy.BackoffCeilingSeconds(0), Is.EqualTo(1));
        }

        /// <summary>Full jitter scales the ceiling, but never below 250 ms.</summary>
        [Test]
        public void JitterScalesTheCeilingWithAFloor()
        {
            var retryPolicy = new RetryPolicy();

            Assert.That(retryPolicy.DelaySeconds(5, null, 0.5), Is.EqualTo(8));
            Assert.That(retryPolicy.DelaySeconds(5, null, 0), Is.EqualTo(0.25));
            Assert.That(retryPolicy.DelaySeconds(1, null, 0.1), Is.EqualTo(0.25));
            Assert.That(retryPolicy.DelaySeconds(20, null, 0.999), Is.LessThanOrEqualTo(300));
            Assert.That(retryPolicy.DelaySeconds(3, null, 7), Is.EqualTo(4));
        }

        /// <summary>Retry-After is a floor, capped at an hour.</summary>
        [Test]
        public void RetryAfterIsAFloorCappedAtAnHour()
        {
            var retryPolicy = new RetryPolicy();

            Assert.That(retryPolicy.DelaySeconds(1, 120, 0.5), Is.EqualTo(120));
            Assert.That(retryPolicy.DelaySeconds(9, 1, 1), Is.EqualTo(256));
            Assert.That(retryPolicy.DelaySeconds(1, 999999, 0.5), Is.EqualTo(3600));
        }

        /// <summary>401 probes go after a minute, then every 10 minutes, pushed later by Retry-After.</summary>
        [Test]
        public void InvalidKeyProbesFollowTheSchedule()
        {
            var retryPolicy = new RetryPolicy();

            Assert.That(retryPolicy.InvalidKeyProbeDelaySeconds(0, null), Is.EqualTo(60));
            Assert.That(retryPolicy.InvalidKeyProbeDelaySeconds(1, null), Is.EqualTo(600));
            Assert.That(retryPolicy.InvalidKeyProbeDelaySeconds(1, 900), Is.EqualTo(900));
        }

        /// <summary>delay-seconds parses as a bare integer.</summary>
        [TestCase("120", 120)]
        [TestCase("  7 ", 7)]
        [TestCase("0", 0)]
        public void ParsesDelaySeconds(string headerValue, double expectedSeconds)
        {
            Assert.That(RetryAfterHeader.ParseDelaySeconds(headerValue, NowUtc), Is.EqualTo(expectedSeconds));
        }

        /// <summary>All three HTTP-date layouts parse; NowUtc is 37 s before the date.</summary>
        [TestCase("Sun, 06 Nov 1994 08:49:37 GMT")]
        [TestCase("Sunday, 06-Nov-94 08:49:37 GMT")]
        [TestCase("Sun Nov  6 08:49:37 1994")]
        public void ParsesEveryHttpDateLayout(string headerValue)
        {
            Assert.That(RetryAfterHeader.ParseDelaySeconds(headerValue, NowUtc), Is.EqualTo(37));
        }

        /// <summary>A date in the past means retry now.</summary>
        [Test]
        public void PastDatesMeanZero()
        {
            Assert.That(RetryAfterHeader.ParseDelaySeconds("Sun, 06 Nov 1994 08:00:00 GMT", NowUtc), Is.EqualTo(0));
        }

        /// <summary>Missing, blank, negative, fractional and garbage values carry no hint.</summary>
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("-5")]
        [TestCase("1.5")]
        [TestCase("soon")]
        [TestCase("Mon, 99 Nov 1994 08:49:37 GMT")]
        public void GarbageMeansNoHint(string headerValue)
        {
            Assert.That(RetryAfterHeader.ParseDelaySeconds(headerValue, NowUtc), Is.Null);
        }

        /// <summary>Statuses map onto the ingest response table; anything unknown is retried.</summary>
        [TestCase(202, "Accepted")]
        [TestCase(401, "InvalidKey")]
        [TestCase(413, "PayloadTooLarge")]
        [TestCase(415, "Unsupported")]
        [TestCase(200, "Retryable")]
        [TestCase(301, "Retryable")]
        [TestCase(429, "Retryable")]
        [TestCase(500, "Retryable")]
        [TestCase(503, "Retryable")]
        public void ClassifiesStatuses(int statusCode, string expectedKindName)
        {
            IngestOutcome outcome = IngestOutcome.Classify(IngestResponse.FromStatus(statusCode, "30"), NowUtc);

            Assert.That(outcome.Kind.ToString(), Is.EqualTo(expectedKindName));
        }

        /// <summary>A network failure is retried without a hint.</summary>
        [Test]
        public void NetworkFailuresAreRetried()
        {
            IngestOutcome outcome = IngestOutcome.Classify(IngestResponse.NetworkFailure("offline"), NowUtc);

            Assert.That(outcome.Kind, Is.EqualTo(IngestOutcomeKind.Retryable));
            Assert.That(outcome.RetryAfterSeconds, Is.Null);
            Assert.That(outcome.Reason, Is.EqualTo("offline"));
        }

        /// <summary>The endpoint loses one trailing slash and gains /v1/ingest.</summary>
        [TestCase("https://edge.simpledatalogs.com", "https://edge.simpledatalogs.com/v1/ingest")]
        [TestCase("http://localhost:4001/", "http://localhost:4001/v1/ingest")]
        [TestCase(null, "https://edge.simpledatalogs.com/v1/ingest")]
        public void BuildsTheIngestUrl(string endpoint, string expectedUrl)
        {
            Assert.That(SdkInfo.BuildIngestUrl(endpoint), Is.EqualTo(expectedUrl));
        }
    }
}
