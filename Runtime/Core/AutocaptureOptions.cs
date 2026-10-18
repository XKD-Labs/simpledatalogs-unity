using System;

namespace XKDLabs.SimpleDataLogs
{
    /// <summary>What Configure captures without any Track call; off unless asked for.</summary>
    [Flags]
    public enum AutocaptureOptions
    {
        /// <summary>Nothing is captured automatically.</summary>
        None = 0,

        /// <summary><c>app_launched</c> on every launch, with <c>is_first_launch</c> on the first.</summary>
        AppLaunches = 1 << 0,

        /// <summary><c>session_start</c>, <c>session_end</c> and a <c>session_id</c> on every event; a session ends after 30 minutes away.</summary>
        Sessions = 1 << 1,

        /// <summary><c>screen_view</c> with the scene name each time a scene loads.</summary>
        SceneViews = 1 << 2,

        /// <summary><c>app_error</c> for logged exceptions and errors, rate limited.</summary>
        Errors = 1 << 3,

        /// <summary>Everything: the one-line opt-in.</summary>
        All = AppLaunches | Sessions | SceneViews | Errors,
    }
}
