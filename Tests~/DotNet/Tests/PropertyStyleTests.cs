using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

using NUnit.Framework;

using XKDLabs.SimpleDataLogs.Core;

namespace XKDLabs.SimpleDataLogs.Tests
{
    /// <summary>Cheap property-style checks with seeded randomness, so failures reproduce.</summary>
    [TestFixture]
    public sealed class PropertyStyleTests
    {
        /// <summary>Random strings and values always encode to one valid JSON line that reads back the same.</summary>
        [Test]
        public void RandomPropertiesEncodeToValidSingleLines([Values(1, 2, 3, 4, 5)] int seed)
        {
            var random = new Random(seed);
            SdkLog.Sink = new RecordingLogSink();

            for (int caseIndex = 0; caseIndex < 400; caseIndex++)
            {
                // Build a random property map with every supported kind and some unsupported ones.
                var rawProperties = new Dictionary<string, object>();
                int propertyCount = random.Next(0, 8);
                for (int propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
                {
                    rawProperties[RandomText(random, 1, 12, shouldIncludeLoneSurrogates: false)] = RandomValue(random);
                }

                Dictionary<string, object> normalized = PropertyNormalizer.Normalize(rawProperties, "random");
                var trackedEvent = new TrackedEvent(RandomText(random, 1, 20), Guid.NewGuid().ToString(), DateTime.UtcNow, RandomText(random, 0, 10), null, normalized);
                string encodedLine = EventEncoder.EncodeLine(trackedEvent);

                // One line, valid JSON, and every kept value reads back.
                Assert.That(encodedLine.IndexOfAny(new[] { '\n', '\r' }), Is.EqualTo(-1));
                using JsonDocument parsedLine = JsonDocument.Parse(Encoding.UTF8.GetBytes(encodedLine));
                JsonElement parsedProperties = parsedLine.RootElement.GetProperty("props");
                Assert.That(parsedProperties.EnumerateObject().Count(), Is.EqualTo(normalized.Count));

                foreach (KeyValuePair<string, object> keptProperty in normalized)
                {
                    AssertReadsBack(parsedProperties.GetProperty(keptProperty.Key), keptProperty.Value);
                }
            }

            SdkLog.Sink = null;
        }

        /// <summary>Random server answers never lose an event that was not 202'd, and never deliver one twice unless an answer was lost.</summary>
        [Test]
        public void RandomAnswersNeverLoseEvents([Values(11, 12, 13, 14, 15, 16)] int seed)
        {
            var random = new Random(seed);
            var clock = new FakeClock();
            using var storage = new TemporaryDirectory();
            SdkLog.Sink = new RecordingLogSink();

            var trackedEventIds = new HashSet<string>();
            var deliveredEventIds = new List<string>();
            var eventIdsWithLostAnswers = new HashSet<string>();

            // The transport stores on 202, and sometimes stores but loses the answer (an unknown outcome).
            var transport = new FakeTransport
            {
                Respond = request =>
                {
                    List<string> requestEventIds = TestKit.EventIdsIn(request);
                    int answerRoll = random.Next(100);

                    if (answerRoll < 45)
                    {
                        deliveredEventIds.AddRange(requestEventIds);
                        return IngestResponse.FromStatus(202, null);
                    }

                    if (answerRoll < 52)
                    {
                        deliveredEventIds.AddRange(requestEventIds);
                        eventIdsWithLostAnswers.UnionWith(requestEventIds);
                        return IngestResponse.NetworkFailure("answer lost");
                    }

                    if (answerRoll < 60)
                    {
                        return requestEventIds.Count > 1 ? IngestResponse.FromStatus(413, null) : IngestResponse.FromStatus(503, null);
                    }

                    int[] retryableStatuses = { 0, 401, 429, 500, 503, 200, 302 };
                    int statusCode = retryableStatuses[random.Next(retryableStatuses.Length)];
                    string retryAfter = random.Next(3) == 0 ? random.Next(0, 90).ToString() : null;

                    return statusCode == 0 ? IngestResponse.NetworkFailure("offline") : IngestResponse.FromStatus(statusCode, retryAfter);
                },
            };

            PipelineOptions options = TestKit.MakeOptions(storage.Path);
            options.MaxBatchEventCount = 7;
            EventPipeline pipeline = TestKit.MakeStartedPipeline(options, transport, clock);

            // Interleave tracks, time passing and flushes.
            for (int stepIndex = 0; stepIndex < 600; stepIndex++)
            {
                int stepRoll = random.Next(10);
                if (stepRoll < 5)
                {
                    TrackedEvent trackedEvent = TestKit.MakeEvent("step", clock.UtcNow);
                    trackedEventIds.Add(trackedEvent.EventId);
                    pipeline.Track(trackedEvent, clock.UtcNow);
                }
                else if (stepRoll < 9)
                {
                    clock.Advance(TimeSpan.FromSeconds(random.Next(1, 400)));
                    pipeline.Tick(clock.UtcNow);
                }
                else
                {
                    pipeline.Flush(clock.UtcNow, true);
                }
            }

            // Then the server heals: every answer is 202 until the outbox drains.
            transport.Respond = request =>
            {
                deliveredEventIds.AddRange(TestKit.EventIdsIn(request));
                return IngestResponse.FromStatus(202, null);
            };

            for (int drainIndex = 0; drainIndex < 200 && pipeline.PendingEventCount > 0; drainIndex++)
            {
                clock.Advance(TimeSpan.FromMinutes(11));
                pipeline.Flush(clock.UtcNow, true);
            }

            // Nothing lost, and duplicates only where an answer was lost.
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(0));
            Assert.That(new HashSet<string>(deliveredEventIds), Is.EquivalentTo(trackedEventIds));

            List<string> duplicatedEventIds = deliveredEventIds.GroupBy(eventId => eventId).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
            Assert.That(duplicatedEventIds.All(eventIdsWithLostAnswers.Contains), Is.True);

            SdkLog.Sink = null;
        }

        // Checks one parsed value against what was encoded.
        private static void AssertReadsBack(JsonElement parsedValue, object keptValue)
        {
            switch (keptValue)
            {
                case string stringValue:
                    Assert.That(parsedValue.GetString(), Is.EqualTo(WithoutLoneSurrogates(stringValue)));
                    break;
                case bool boolValue:
                    Assert.That(parsedValue.GetBoolean(), Is.EqualTo(boolValue));
                    break;
                case long longValue:
                    Assert.That(parsedValue.GetInt64(), Is.EqualTo(longValue));
                    break;
                case ulong ulongValue:
                    Assert.That(parsedValue.GetUInt64(), Is.EqualTo(ulongValue));
                    break;
                case double doubleValue:
                    Assert.That(parsedValue.GetDouble(), Is.EqualTo(doubleValue));
                    break;
                default:
                    Assert.Fail("Unexpected normalized type " + keptValue.GetType());
                    break;
            }
        }

        // Mirrors the encoder: a lone surrogate becomes U+FFFD.
        private static string WithoutLoneSurrogates(string text)
        {
            var cleanedText = new StringBuilder();
            for (int charIndex = 0; charIndex < text.Length; charIndex++)
            {
                char character = text[charIndex];
                bool isPairStart = char.IsHighSurrogate(character) && charIndex + 1 < text.Length && char.IsLowSurrogate(text[charIndex + 1]);

                if (isPairStart)
                {
                    cleanedText.Append(character).Append(text[charIndex + 1]);
                    charIndex++;
                }
                else
                {
                    cleanedText.Append(char.IsSurrogate(character) ? (char)0xFFFD : character);
                }
            }

            return cleanedText.ToString();
        }

        // Random text mixing ASCII, control characters, quotes, emoji and, unless asked not to, lone surrogates.
        private static string RandomText(Random random, int minimumLength, int maximumLength, bool shouldIncludeLoneSurrogates = true)
        {
            string[] safeFragments = { "a", "Z", "9", " ", "\"", "\\", "\n", "\r", "\t", "\u0001", "\u001f", "\u2028", "\u00e9", "\U0001F600", "{", "}" };
            string[] fragments = shouldIncludeLoneSurrogates ? safeFragments.Concat(new[] { "\uD800", "\uDC00" }).ToArray() : safeFragments;
            var textBuilder = new StringBuilder();
            int fragmentCount = random.Next(minimumLength, maximumLength + 1);

            for (int fragmentIndex = 0; fragmentIndex < fragmentCount; fragmentIndex++)
            {
                textBuilder.Append(fragments[random.Next(fragments.Length)]);
            }

            return textBuilder.ToString();
        }

        // A random value of a supported or unsupported kind.
        private static object RandomValue(Random random)
        {
            switch (random.Next(10))
            {
                case 0:
                    return RandomText(random, 0, 30);
                case 1:
                    return random.Next(int.MinValue, int.MaxValue);
                case 2:
                    return (long)(random.NextDouble() * long.MaxValue) * (random.Next(2) == 0 ? -1 : 1);
                case 3:
                    return random.NextDouble() * Math.Pow(10, random.Next(-20, 20));
                case 4:
                    return (float)(random.NextDouble() * 1000);
                case 5:
                    return random.Next(2) == 0;
                case 6:
                    return new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity }[random.Next(3)];
                case 7:
                    return null;
                case 8:
                    return ulong.MaxValue - (ulong)random.Next(1000);
                default:
                    return new List<int> { 1, 2 };
            }
        }
    }
}
