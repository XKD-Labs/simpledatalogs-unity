namespace XKDLabs.SimpleDataLogs.Core
{
    /// <summary>The SDK's version, endpoint and wire constants.</summary>
    internal static class SdkInfo
    {
        /// <summary>This SDK's version, sent in every User-Agent.</summary>
        public const string Version = "0.1.0";

        /// <summary>The production ingest host.</summary>
        public const string DefaultEndpoint = "https://edge.simpledatalogs.com";

        /// <summary>The User-Agent every request carries where the runtime allows one.</summary>
        public const string UserAgent = "simpledatalogs-unity/" + Version;

        /// <summary>The body type of every batch.</summary>
        public const string NdjsonContentType = "application/x-ndjson";

        /// <summary>The path appended to the endpoint.</summary>
        public const string IngestPath = "/v1/ingest";

        /// <summary>The prefix of secret keys, which belong on servers only.</summary>
        public const string SecretKeyPrefix = "sdl_sec_";

        /// <summary>The ingest URL for an endpoint: one trailing slash trimmed, then <c>/v1/ingest</c>.</summary>
        public static string BuildIngestUrl(string endpoint)
        {
            string trimmedEndpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim();

            if (trimmedEndpoint.EndsWith("/"))
            {
                trimmedEndpoint = trimmedEndpoint.Substring(0, trimmedEndpoint.Length - 1);
            }

            return trimmedEndpoint + IngestPath;
        }
    }
}
