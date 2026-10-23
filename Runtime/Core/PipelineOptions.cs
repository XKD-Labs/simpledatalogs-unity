using System;

namespace XKDLabs.SimpleDataLogs.Core
{
    /// <summary>Every tunable the pipeline reads; the defaults follow the ingest API, and tests shrink them.</summary>
    internal sealed class PipelineOptions
    {
        /// <summary>The full ingest URL, <c>&lt;endpoint&gt;/v1/ingest</c>.</summary>
        public string IngestUrl { get; set; } = SdkInfo.BuildIngestUrl(SdkInfo.DefaultEndpoint);

        /// <summary>The bearer key sent with every request.</summary>
        public string IngestKey { get; set; } = string.Empty;

        /// <summary>The outbox folder holding the journal, the sealed batches and the autocapture state.</summary>
        public string StorageDirectory { get; set; }

        /// <summary>The User-Agent header, or null where the runtime forbids setting it (WebGL).</summary>
        public string UserAgent { get; set; } = SdkInfo.UserAgent;

        /// <summary>True to gzip bodies of 1,024 bytes or more; off on WebGL, where the browser handles encoding.</summary>
        public bool ShouldGzip { get; set; } = true;

        /// <summary>Seal and send as soon as the journal holds this many events.</summary>
        public int MaxBatchEventCount { get; set; } = 50;

        /// <summary>Seal a partial batch this long after its first event: 10 s in release, 5 s in development builds.</summary>
        public TimeSpan BatchInterval { get; set; } = BatchIntervalFor(isDevelopmentBuild: false);

        /// <summary>5 s in the Editor and development builds, so new events show within seconds; 10 s in release.</summary>
        public static TimeSpan BatchIntervalFor(bool isDevelopmentBuild)
        {
            return TimeSpan.FromSeconds(isDevelopmentBuild ? 5 : 10);
        }

        /// <summary>The largest request body ever sent, uncompressed, including newlines.</summary>
        public int MaxRequestBytes { get; set; } = 256000;

        /// <summary>The most events the outbox keeps before it drops the oldest batches.</summary>
        public int MaxOutboxEventCount { get; set; } = 10000;

        /// <summary>The most bytes the outbox keeps before it drops the oldest batches.</summary>
        public int MaxOutboxBytes { get; set; } = 10 * 1024 * 1024;

        /// <summary>The most sealed batch files kept, so days offline never leave thousands of files.</summary>
        public int MaxSealedBatchCount { get; set; } = 500;

        /// <summary>How long one request may take before it counts as a timeout.</summary>
        public int RequestTimeoutSeconds { get; set; } = 30;

        /// <summary>Extra time past the request timeout before an unanswered send is given up on.</summary>
        public TimeSpan MissingAnswerGrace { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Reads of one batch that may fail in a row before it is set aside.</summary>
        public int MaxBatchReadAttempts { get; set; } = 3;

        /// <summary>Backoff and Retry-After rules for failed sends.</summary>
        public RetryPolicy RetryPolicy { get; set; } = new RetryPolicy();

        /// <summary>The outbox limits derived from these options.</summary>
        public OutboxLimits BuildOutboxLimits()
        {
            return new OutboxLimits
            {
                MaxBatchEventCount = MaxBatchEventCount,
                MaxBatchBytes = MaxRequestBytes,
                MaxTotalEventCount = MaxOutboxEventCount,
                MaxTotalBytes = MaxOutboxBytes,
                MaxSealedBatchCount = MaxSealedBatchCount,
            };
        }
    }
}
