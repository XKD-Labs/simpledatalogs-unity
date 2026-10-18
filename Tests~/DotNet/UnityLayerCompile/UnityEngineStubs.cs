// Minimal stand-ins for the UnityEngine APIs the package uses, with Unity 2021.3 signatures.
// They only let the compiler type-check the Unity layer outside the editor; none of this ever runs.
#pragma warning disable CS0067
#pragma warning disable CS1591

using System;

namespace UnityEngine
{
    /// <summary>Stub of UnityEngine.Object.</summary>
    public class Object
    {
        /// <summary>Stub.</summary>
        public HideFlags hideFlags { get; set; }

        /// <summary>Stub.</summary>
        public static void Destroy(Object target)
        {
        }

        /// <summary>Stub.</summary>
        public static void DestroyImmediate(Object target)
        {
        }

        /// <summary>Stub.</summary>
        public static void DontDestroyOnLoad(Object target)
        {
        }
    }

    /// <summary>Stub of Component.</summary>
    public class Component : Object
    {
        /// <summary>Stub.</summary>
        public GameObject gameObject => null;
    }

    /// <summary>Stub of Behaviour.</summary>
    public class Behaviour : Component
    {
    }

    /// <summary>Stub of MonoBehaviour.</summary>
    public class MonoBehaviour : Behaviour
    {
    }

    /// <summary>Stub of GameObject.</summary>
    public sealed class GameObject : Object
    {
        /// <summary>Stub.</summary>
        public GameObject(string name)
        {
        }

        /// <summary>Stub.</summary>
        public T AddComponent<T>() where T : Component
        {
            return null;
        }
    }

    /// <summary>Stub of HideFlags.</summary>
    [Flags]
    public enum HideFlags
    {
        None = 0,
        HideInHierarchy = 1,
        HideInInspector = 2,
        DontSaveInEditor = 4,
        NotEditable = 8,
        DontSaveInBuild = 16,
        DontUnloadUnusedAsset = 32,
        DontSave = 52,
        HideAndDontSave = 61,
    }

    /// <summary>Stub of LogType.</summary>
    public enum LogType
    {
        Error = 0,
        Assert = 1,
        Warning = 2,
        Log = 3,
        Exception = 4,
    }

    /// <summary>Stub of RuntimePlatform.</summary>
    public enum RuntimePlatform
    {
        OSXEditor = 0,
        WindowsPlayer = 2,
        IPhonePlayer = 8,
        Android = 11,
        WebGLPlayer = 17,
    }

    /// <summary>Stub of Debug.</summary>
    public static class Debug
    {
        /// <summary>Stub.</summary>
        public static void Log(object message)
        {
        }

        /// <summary>Stub.</summary>
        public static void LogWarning(object message)
        {
        }

        /// <summary>Stub.</summary>
        public static void LogError(object message)
        {
        }
    }

    /// <summary>Stub of Application.</summary>
    public static class Application
    {
        /// <summary>Stub of Application.LogCallback.</summary>
        public delegate void LogCallback(string condition, string stackTrace, LogType type);

        /// <summary>Stub.</summary>
        public static event LogCallback logMessageReceived;

        /// <summary>Stub.</summary>
        public static string persistentDataPath => string.Empty;

        /// <summary>Stub.</summary>
        public static string version => string.Empty;

        /// <summary>Stub.</summary>
        public static RuntimePlatform platform => RuntimePlatform.OSXEditor;

        /// <summary>Stub.</summary>
        public static bool isPlaying => false;
    }

    /// <summary>Stub of Time.</summary>
    public static class Time
    {
        /// <summary>Stub.</summary>
        public static float unscaledDeltaTime => 0;

        /// <summary>Stub.</summary>
        public static float unscaledTime => 0;
    }

    /// <summary>Stub of Mathf.</summary>
    public static class Mathf
    {
        /// <summary>Stub.</summary>
        public static float Round(float value)
        {
            return value;
        }
    }

    /// <summary>Stub of PlayerPrefs.</summary>
    public static class PlayerPrefs
    {
        /// <summary>Stub.</summary>
        public static string GetString(string key, string defaultValue)
        {
            return defaultValue;
        }

        /// <summary>Stub.</summary>
        public static void SetString(string key, string value)
        {
        }

        /// <summary>Stub.</summary>
        public static void Save()
        {
        }
    }

    /// <summary>Stub of AsyncOperation.</summary>
    public class AsyncOperation
    {
        /// <summary>Stub.</summary>
        public event Action<AsyncOperation> completed;
    }

    /// <summary>Stub of SerializeField.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute
    {
    }

    /// <summary>Stub of AddComponentMenu.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class AddComponentMenu : Attribute
    {
        /// <summary>Stub.</summary>
        public AddComponentMenu(string menuName)
        {
        }
    }

    /// <summary>Stub of DisallowMultipleComponent.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class DisallowMultipleComponent : Attribute
    {
    }

    /// <summary>Stub of RuntimeInitializeLoadType.</summary>
    public enum RuntimeInitializeLoadType
    {
        AfterSceneLoad = 0,
        BeforeSceneLoad = 1,
        AfterAssembliesLoaded = 2,
        BeforeSplashScreen = 3,
        SubsystemRegistration = 4,
    }

    /// <summary>Stub of RuntimeInitializeOnLoadMethodAttribute.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        /// <summary>Stub.</summary>
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType loadType)
        {
        }
    }
}

namespace UnityEngine.Events
{
    /// <summary>Stub of UnityAction with two arguments.</summary>
    public delegate void UnityAction<T0, T1>(T0 arg0, T1 arg1);
}

namespace UnityEngine.SceneManagement
{
    using UnityEngine.Events;

    /// <summary>Stub of LoadSceneMode.</summary>
    public enum LoadSceneMode
    {
        Single = 0,
        Additive = 1,
    }

    /// <summary>Stub of Scene.</summary>
    public struct Scene
    {
        /// <summary>Stub.</summary>
        public string name => string.Empty;

        /// <summary>Stub.</summary>
        public bool isLoaded => false;

        /// <summary>Stub.</summary>
        public int handle => 0;
    }

    /// <summary>Stub of SceneManager.</summary>
    public static class SceneManager
    {
        /// <summary>Stub.</summary>
        public static event UnityAction<Scene, LoadSceneMode> sceneLoaded;

        /// <summary>Stub.</summary>
        public static Scene GetActiveScene()
        {
            return default;
        }
    }
}

namespace UnityEngine.Networking
{
    /// <summary>Stub of UploadHandler.</summary>
    public class UploadHandler : IDisposable
    {
        /// <summary>Stub.</summary>
        public string contentType { get; set; }

        /// <summary>Stub.</summary>
        public void Dispose()
        {
        }
    }

    /// <summary>Stub of UploadHandlerRaw.</summary>
    public sealed class UploadHandlerRaw : UploadHandler
    {
        /// <summary>Stub.</summary>
        public UploadHandlerRaw(byte[] data)
        {
        }
    }

    /// <summary>Stub of DownloadHandler.</summary>
    public class DownloadHandler : IDisposable
    {
        /// <summary>Stub.</summary>
        public void Dispose()
        {
        }
    }

    /// <summary>Stub of DownloadHandlerBuffer.</summary>
    public sealed class DownloadHandlerBuffer : DownloadHandler
    {
    }

    /// <summary>Stub of UnityWebRequestAsyncOperation.</summary>
    public sealed class UnityWebRequestAsyncOperation : AsyncOperation
    {
    }

    /// <summary>Stub of UnityWebRequest.</summary>
    public sealed class UnityWebRequest : IDisposable
    {
        /// <summary>Stub.</summary>
        public const string kHttpVerbPOST = "POST";

        /// <summary>Stub.</summary>
        public UnityWebRequest(string url, string method)
        {
        }

        /// <summary>Stub of UnityWebRequest.Result.</summary>
        public enum Result
        {
            InProgress = 0,
            Success = 1,
            ConnectionError = 2,
            ProtocolError = 3,
            DataProcessingError = 4,
        }

        /// <summary>Stub.</summary>
        public UploadHandler uploadHandler { get; set; }

        /// <summary>Stub.</summary>
        public DownloadHandler downloadHandler { get; set; }

        /// <summary>Stub.</summary>
        public int timeout { get; set; }

        /// <summary>Stub.</summary>
        public bool useHttpContinue { get; set; }

        /// <summary>Stub.</summary>
        public Result result => Result.InProgress;

        /// <summary>Stub.</summary>
        public long responseCode => 0;

        /// <summary>Stub.</summary>
        public string error => null;

        /// <summary>Stub.</summary>
        public void SetRequestHeader(string name, string value)
        {
        }

        /// <summary>Stub.</summary>
        public string GetResponseHeader(string name)
        {
            return null;
        }

        /// <summary>Stub.</summary>
        public UnityWebRequestAsyncOperation SendWebRequest()
        {
            return new UnityWebRequestAsyncOperation();
        }

        /// <summary>Stub.</summary>
        public void Dispose()
        {
        }
    }
}
