using System;
using System.IO;
using System.Reflection;

namespace PrefabEditorMod
{
    internal static class ConnectionSettings
    {
        internal static string GameDirectory
        {
            get { return Directory.GetParent(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)).FullName; }
        }

        internal static string ReadUrl(string fallback, Action<string> log)
        {
            try
            {
                string file = Path.Combine(GameDirectory, "UserData/PrefabEditorConnection.json");
                if (!File.Exists(file)) return fallback;
                string value = Json.Str(File.ReadAllText(file), "panelUrl");
                Uri uri;
                if (!Uri.TryCreate(value, UriKind.Absolute, out uri)
                    || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
                    || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/"
                    || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                    throw new Exception("invalid editor address; restart Prefab Editor from Prefabulator, then restart the game");
                return uri.AbsoluteUri.TrimEnd('/');
            }
            catch (Exception e) { if (log != null) log("Connection settings: " + e.Message); return fallback; }
        }

    }
}
