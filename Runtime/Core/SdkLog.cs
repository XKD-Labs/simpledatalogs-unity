using System;

namespace XKDLabs.SimpleDataLogs.Core
{
    /// <summary>How serious one SDK log line is.</summary>
    internal enum SdkLogLevel
    {
        /// <summary>Normal operation worth knowing about, e.g. an opt-out.</summary>
        Info,

        /// <summary>Something was dropped or skipped, but the SDK keeps working.</summary>
        Warning,

        /// <summary>A setup problem or a lost event the host should fix.</summary>
        Error,
    }

    /// <summary>Where SDK log lines go; the Unity layer forwards them to the console.</summary>
    internal interface ISdkLogSink
    {
        /// <summary>Writes one line at the given level.</summary>
        void Write(SdkLogLevel level, string message);
    }

    /// <summary>The one logger every SDK component writes to; silent until a sink is set.</summary>
    internal static class SdkLog
    {
        /// <summary>The prefix on every SDK line, so hosts can filter them and error autocapture can skip them.</summary>
        public const string Tag = "[simpledatalogs]";

        /// <summary>The current sink, or null for silence.</summary>
        private static volatile ISdkLogSink currentSink;

        /// <summary>The sink lines go to; tests swap in a recording one.</summary>
        public static ISdkLogSink Sink
        {
            get => currentSink;
            set => currentSink = value;
        }

        /// <summary>Logs normal operation.</summary>
        public static void Info(string message)
        {
            Write(SdkLogLevel.Info, message);
        }

        /// <summary>Logs something dropped or skipped.</summary>
        public static void Warning(string message)
        {
            Write(SdkLogLevel.Warning, message);
        }

        /// <summary>Logs a setup problem or a lost event.</summary>
        public static void Error(string message)
        {
            Write(SdkLogLevel.Error, message);
        }

        // Hands one line to the sink; a failing sink is ignored, since logging must never break the host.
        private static void Write(SdkLogLevel level, string message)
        {
            ISdkLogSink sink = currentSink;
            if (sink == null)
            {
                return;
            }

            try
            {
                sink.Write(level, message);
            }
            catch (Exception)
            {
                // Logging is best effort.
            }
        }
    }
}
