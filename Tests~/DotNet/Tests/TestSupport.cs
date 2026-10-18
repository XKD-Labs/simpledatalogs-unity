using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

using XKDLabs.SimpleDataLogs.Core;

namespace XKDLabs.SimpleDataLogs.Tests
{
    /// <summary>A clock tests move by hand, so no test ever sleeps.</summary>
    internal sealed class FakeClock
    {
        /// <summary>Starts at a fixed moment.</summary>
        public FakeClock()
        {
            UtcNow = new DateTime(2026, 10, 12, 9, 14, 3, 120, DateTimeKind.Utc);
        }

        /// <summary>The current fake time.</summary>
        public DateTime UtcNow { get; set; }

        /// <summary>Moves time forward.</summary>
        public void Advance(TimeSpan duration)
        {
            UtcNow += duration;
        }
    }

    /// <summary>Keeps every SDK log line for assertions.</summary>
    internal sealed class RecordingLogSink : ISdkLogSink
    {
        /// <summary>Every line so far, with its level.</summary>
        public List<KeyValuePair<SdkLogLevel, string>> Lines { get; } = new List<KeyValuePair<SdkLogLevel, string>>();

        /// <summary>Records one line.</summary>
        public void Write(SdkLogLevel level, string message)
        {
            lock (Lines)
            {
                Lines.Add(new KeyValuePair<SdkLogLevel, string>(level, message));
            }
        }

        /// <summary>True when some line contains the text.</summary>
        public bool Contains(string text)
        {
            lock (Lines)
            {
                return Lines.Any(line => line.Value.Contains(text, StringComparison.Ordinal));
            }
        }

        /// <summary>How many lines contain the text.</summary>
        public int Count(string text)
        {
            lock (Lines)
            {
                return Lines.Count(line => line.Value.Contains(text, StringComparison.Ordinal));
            }
        }
    }

    /// <summary>A transport that answers from a script, synchronously or when the test releases it.</summary>
    internal sealed class FakeTransport : IIngestTransport
    {
        /// <summary>Answers not yet released, for deferred mode.</summary>
        private readonly Queue<Action> heldAnswers = new Queue<Action>();

        /// <summary>Every request sent, in order.</summary>
        public List<IngestRequest> Requests { get; } = new List<IngestRequest>();

        /// <summary>Picks the answer for each request; 202 by default.</summary>
        public Func<IngestRequest, IngestResponse> Respond { get; set; } = request => IngestResponse.FromStatus(202, null);

        /// <summary>True to hold answers until ReleaseAnswers is called.</summary>
        public bool ShouldHoldAnswers { get; set; }

        /// <summary>Records the request and answers now, or later in deferred mode.</summary>
        public void Send(IngestRequest request, Action<IngestResponse> onComplete)
        {
            Requests.Add(request);
            IngestResponse response = Respond(request);

            if (ShouldHoldAnswers)
            {
                heldAnswers.Enqueue(() => onComplete(response));
                return;
            }

            onComplete(response);
        }

        /// <summary>Delivers every held answer.</summary>
        public void ReleaseAnswers()
        {
            while (heldAnswers.Count > 0)
            {
                heldAnswers.Dequeue()();
            }
        }

        /// <summary>A script that answers with each status in turn, then 202 forever.</summary>
        public static Func<IngestRequest, IngestResponse> Script(params IngestResponse[] responses)
        {
            var remainingResponses = new Queue<IngestResponse>(responses);

            return request => remainingResponses.Count > 0 ? remainingResponses.Dequeue() : IngestResponse.FromStatus(202, null);
        }
    }

    /// <summary>A fresh folder under the system temp directory, deleted on dispose.</summary>
    internal sealed class TemporaryDirectory : IDisposable
    {
        /// <summary>Creates a unique folder.</summary>
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sdl-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        /// <summary>The folder's path.</summary>
        public string Path { get; }

        /// <summary>Deletes the folder and everything in it.</summary>
        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
                // A leftover temp folder is harmless.
            }
        }
    }

    /// <summary>Builders and readers the tests share.</summary>
    internal static class TestKit
    {
        /// <summary>Options with the production defaults, pointed at a test folder.</summary>
        public static PipelineOptions MakeOptions(string storageDirectory, string ingestUrl = "http://127.0.0.1:1/v1/ingest")
        {
            return new PipelineOptions
            {
                IngestUrl = ingestUrl,
                IngestKey = "sdl_pub_test",
                StorageDirectory = storageDirectory,
            };
        }

        /// <summary>A started pipeline on a fake clock, with jitter pinned to 0.5.</summary>
        public static EventPipeline MakeStartedPipeline(PipelineOptions options, IIngestTransport transport, FakeClock clock, bool isEnabled = true)
        {
            var pipeline = new EventPipeline(options, transport, () => 0.5, isEnabled);
            pipeline.Start(clock.UtcNow);

            return pipeline;
        }

        /// <summary>A client on a fake clock with predictable ids: id-0001, id-0002 ..., or another prefix per launch.</summary>
        public static SimpleDataLogsClient MakeClient(FakeClock clock, string idPrefix = "id-")
        {
            int idCounter = 0;

            return new SimpleDataLogsClient(() => clock.UtcNow, () => 0.5, () => idPrefix + (++idCounter).ToString("D4"));
        }

        /// <summary>A simple event with one property.</summary>
        public static TrackedEvent MakeEvent(string eventName, DateTime occurredAtUtc, string eventId = null, IReadOnlyDictionary<string, object> properties = null)
        {
            return new TrackedEvent(eventName, eventId ?? Guid.NewGuid().ToString(), occurredAtUtc, null, null, properties ?? new Dictionary<string, object> { ["n"] = 1L });
        }

        /// <summary>The request body, decompressed when it was gzipped.</summary>
        public static string ReadBody(IngestRequest request)
        {
            byte[] body = request.GetHeader("Content-Encoding") == "gzip" ? Gunzip(request.Body) : request.Body;

            return Encoding.UTF8.GetString(body);
        }

        /// <summary>Decompresses a gzip body.</summary>
        public static byte[] Gunzip(byte[] compressedBody)
        {
            using (var compressedStream = new MemoryStream(compressedBody))
            using (var gzipStream = new System.IO.Compression.GZipStream(compressedStream, System.IO.Compression.CompressionMode.Decompress))
            using (var plainStream = new MemoryStream())
            {
                gzipStream.CopyTo(plainStream);
                return plainStream.ToArray();
            }
        }

        /// <summary>Parses every NDJSON line of a body.</summary>
        public static List<JsonElement> ParseLines(string ndjsonBody)
        {
            return ndjsonBody
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToList();
        }

        /// <summary>The event ids in a request body.</summary>
        public static List<string> EventIdsIn(IngestRequest request)
        {
            return ParseLines(ReadBody(request)).Select(line => line.GetProperty("event_id").GetString()).ToList();
        }

        /// <summary>Ticks until no request is in flight or the step limit is reached.</summary>
        public static void TickUntilIdle(EventPipeline pipeline, FakeClock clock, int maxSteps = 1000)
        {
            for (int stepIndex = 0; stepIndex < maxSteps; stepIndex++)
            {
                pipeline.Tick(clock.UtcNow);
                if (!pipeline.IsRequestInFlight)
                {
                    return;
                }
            }
        }
    }
}
