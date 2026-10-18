using System;
using System.IO;
using System.IO.Compression;

namespace XKDLabs.SimpleDataLogs.Core
{
    /// <summary>Gzips request bodies where the platform can, and reports when it cannot.</summary>
    internal static class BodyCompressor
    {
        /// <summary>Bodies smaller than this go uncompressed; gzip would barely help.</summary>
        public const int MinimumCompressedBodyBytes = 1024;

        /// <summary>Gzips a body, or returns null when compression is unavailable here (some IL2CPP targets lack zlib).</summary>
        public static byte[] TryGzip(byte[] body)
        {
            try
            {
                using (var compressedStream = new MemoryStream())
                {
                    // The gzip stream must close before the buffer holds the full trailer.
                    using (var gzipStream = new GZipStream(compressedStream, CompressionLevel.Optimal, true))
                    {
                        gzipStream.Write(body, 0, body.Length);
                    }

                    return compressedStream.ToArray();
                }
            }
            catch (Exception compressionError)
            {
                SdkLog.Warning($"Gzip is unavailable here ({compressionError.GetType().Name}); sending uncompressed");
                return null;
            }
        }
    }
}
