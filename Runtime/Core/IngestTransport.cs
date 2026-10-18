using System;
using System.Collections.Generic;

namespace XKDLabs.SimpleDataLogs.Core
{
    /// <summary>Sends one batch over HTTP; Unity uses UnityWebRequest, tests use HttpClient or a fake.</summary>
    internal interface IIngestTransport
    {
        /// <summary>Starts the POST and calls onComplete exactly once, on any thread; it must not throw.</summary>
        void Send(IngestRequest request, Action<IngestResponse> onComplete);
    }

    /// <summary>One POST to the ingest endpoint.</summary>
    internal sealed class IngestRequest
    {
        /// <summary>Builds a request; headers are in the order they should be set.</summary>
        public IngestRequest(string url, byte[] body, IReadOnlyList<KeyValuePair<string, string>> headers, int timeoutSeconds)
        {
            Url = url;
            Body = body;
            Headers = headers;
            TimeoutSeconds = timeoutSeconds;
        }

        /// <summary>The full ingest URL.</summary>
        public string Url { get; }

        /// <summary>The NDJSON body, gzipped when the Content-Encoding header says so.</summary>
        public byte[] Body { get; }

        /// <summary>Authorization, Content-Type, and User-Agent and Content-Encoding where they apply.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

        /// <summary>How long the request may take.</summary>
        public int TimeoutSeconds { get; }

        /// <summary>The value of a header, or null when it is not set.</summary>
        public string GetHeader(string headerName)
        {
            foreach (KeyValuePair<string, string> header in Headers)
            {
                if (string.Equals(header.Key, headerName, StringComparison.OrdinalIgnoreCase))
                {
                    return header.Value;
                }
            }

            return null;
        }
    }

    /// <summary>What came back: an HTTP status, or no answer at all.</summary>
    internal sealed class IngestResponse
    {
        // Use FromStatus or NetworkFailure.
        private IngestResponse(int statusCode, string retryAfterHeader, string failureReason)
        {
            StatusCode = statusCode;
            RetryAfterHeader = retryAfterHeader;
            FailureReason = failureReason;
        }

        /// <summary>The HTTP status, or 0 when no HTTP answer arrived.</summary>
        public int StatusCode { get; }

        /// <summary>The raw Retry-After header, if any.</summary>
        public string RetryAfterHeader { get; }

        /// <summary>Why no HTTP answer arrived, for the log.</summary>
        public string FailureReason { get; }

        /// <summary>An HTTP answer.</summary>
        public static IngestResponse FromStatus(int statusCode, string retryAfterHeader)
        {
            return new IngestResponse(statusCode, retryAfterHeader, null);
        }

        /// <summary>A timeout, an offline device or any other failure without an HTTP status.</summary>
        public static IngestResponse NetworkFailure(string failureReason)
        {
            return new IngestResponse(0, null, string.IsNullOrEmpty(failureReason) ? "a network error" : failureReason);
        }
    }

    /// <summary>The ingest response rules, one per kind of answer.</summary>
    internal enum IngestOutcomeKind
    {
        /// <summary>202: durably stored, so the batch leaves the outbox.</summary>
        Accepted,

        /// <summary>401: the key is unknown, revoked or not yet live, so sending pauses and re-probes slowly.</summary>
        InvalidKey,

        /// <summary>413: the body was too large, so the batch is split in half.</summary>
        PayloadTooLarge,

        /// <summary>415: never resend unchanged, so the batch is set aside.</summary>
        Unsupported,

        /// <summary>Anything else, including timeouts and network errors: keep the batch and retry.</summary>
        Retryable,
    }

    /// <summary>What one response means for the batch that was sent.</summary>
    internal readonly struct IngestOutcome
    {
        // Use Classify.
        private IngestOutcome(IngestOutcomeKind kind, double? retryAfterSeconds, string reason)
        {
            Kind = kind;
            RetryAfterSeconds = retryAfterSeconds;
            Reason = reason;
        }

        /// <summary>Which rule applies.</summary>
        public IngestOutcomeKind Kind { get; }

        /// <summary>The server's Retry-After, in seconds from now, for retryable answers.</summary>
        public double? RetryAfterSeconds { get; }

        /// <summary>A short description for the log.</summary>
        public string Reason { get; }

        /// <summary>Maps a response onto the ingest response table.</summary>
        public static IngestOutcome Classify(IngestResponse response, DateTime nowUtc)
        {
            // No HTTP answer means the outcome is unknown, so the batch is retried.
            if (response == null || response.StatusCode == 0)
            {
                string failureReason = response?.FailureReason ?? "no response";
                return new IngestOutcome(IngestOutcomeKind.Retryable, null, failureReason);
            }

            switch (response.StatusCode)
            {
                case 202:
                    return new IngestOutcome(IngestOutcomeKind.Accepted, null, "HTTP 202");
                case 401:
                    return new IngestOutcome(IngestOutcomeKind.InvalidKey, null, "HTTP 401");
                case 413:
                    return new IngestOutcome(IngestOutcomeKind.PayloadTooLarge, null, "HTTP 413");
                case 415:
                    return new IngestOutcome(IngestOutcomeKind.Unsupported, null, "HTTP 415");
                default:
                    // Anything else, including other 2xx codes, was not stored, so it is retried.
                    double? retryAfterSeconds = RetryAfterHeader.ParseDelaySeconds(response.RetryAfterHeader, nowUtc);
                    return new IngestOutcome(IngestOutcomeKind.Retryable, retryAfterSeconds, "HTTP " + response.StatusCode);
            }
        }
    }
}
