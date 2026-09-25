using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MelonLoader;

namespace PrefabEditor
{
    // Main-thread work queue. Background threads (console requests, sockets, timers)
    // Post() closures; the kernel drains a bounded batch per frame so game-thread-only
    // APIs are safe to call from anywhere without ever stalling a frame.
    public sealed class Dispatcher
    {
        readonly Queue<Action> _queue = new Queue<Action>();
        readonly object _gate = new object();
        readonly MelonLogger.Instance _log;

        public Dispatcher(MelonLogger.Instance log) { _log = log; }

        public void Post(Action work)
        {
            if (work == null) return;
            lock (_gate) _queue.Enqueue(work);
        }

        public int Pending { get { lock (_gate) return _queue.Count; } }

        public void Drain(int maxPerFrame)
        {
            for (int i = 0; i < maxPerFrame; i++)
            {
                Action work;
                lock (_gate)
                {
                    if (_queue.Count == 0) return;
                    work = _queue.Dequeue();
                }
                try { work(); }
                catch (Exception e) { _log.Error("Dispatched work threw: " + e); }
            }
        }
    }

    // Module enable flags, read once at boot from UserData\PrefabEditor\config.json.
    // Missing file -> defaults written out; missing key -> enabled. Parsed with a
    // key-scan (C#5-safe, no JSON library in the game's runtime worth trusting).
    public sealed class CoreConfig
    {
        readonly Dictionary<string, bool> _flags = new Dictionary<string, bool>();

        public bool ModuleEnabled(string name)
        {
            bool on;
            return _flags.TryGetValue(name, out on) ? on : true;
        }

        public static CoreConfig LoadOrCreate(string coreDir, string[] moduleNames, MelonLogger.Instance log)
        {
            CoreConfig cfg = new CoreConfig();
            string path = Path.Combine(coreDir, "config.json");
            try
            {
                Directory.CreateDirectory(coreDir);
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, DefaultJson(moduleNames));
                    log.Msg("Default config written: " + path);
                    return cfg; // all enabled
                }
                string json = File.ReadAllText(path);
                foreach (string name in moduleNames)
                {
                    bool value;
                    if (TryScanBool(json, name, out value)) cfg._flags[name] = value;
                }
            }
            catch (Exception e)
            {
                log.Error("Config load failed (" + path + "), all modules enabled: " + e.Message);
            }
            return cfg;
        }

        static string DefaultJson(string[] moduleNames)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n  \"Modules\": {\n");
            for (int i = 0; i < moduleNames.Length; i++)
            {
                sb.Append("    \"").Append(moduleNames[i]).Append("\": true");
                sb.Append(i < moduleNames.Length - 1 ? ",\n" : "\n");
            }
            sb.Append("  }\n}\n");
            return sb.ToString();
        }

        // Finds "key" : true|false anywhere in the document. Module names are fixed
        // identifiers that never collide as substrings of one another's quoted form.
        static bool TryScanBool(string json, string key, out bool value)
        {
            value = true;
            string quoted = "\"" + key + "\"";
            int i = json.IndexOf(quoted, StringComparison.Ordinal);
            if (i < 0) return false;
            i = json.IndexOf(':', i + quoted.Length);
            if (i < 0) return false;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (string.CompareOrdinal(json, i, "true", 0, 4) == 0) { value = true; return true; }
            if (string.CompareOrdinal(json, i, "false", 0, 5) == 0) { value = false; return true; }
            return false;
        }
    }

    // Everything a module gets to see. One shared instance, built by the kernel.
    public sealed class CoreContext
    {
        public string GameDir;
        public string UserDataDir;
        public string KeyDir;    // UserData\att_console  (console trust store - compat path)
        public string CoreDir;   // UserData\PrefabEditor   (this mod's own config/state)
        public CoreConfig Config;
        public Dispatcher Dispatcher;
        public MelonLogger.Instance RawLog;
    }

    // Base for everything the kernel hosts. Init runs once on the main thread at
    // late-start; OnUpdate every frame (keep it allocation-free and near-empty);
    // Shutdown in reverse init order at application quit.
    public abstract class TavernModule
    {
        protected CoreContext Ctx;
        protected ModLog Log;

        public abstract string Name { get; }
        public bool Initialized { get; private set; }

        public void Boot(CoreContext ctx)
        {
            Ctx = ctx;
            MelonLogger.Instance raw = ctx.RawLog;
            Log = new ModLog(Name,
                delegate(string s) { raw.Msg(s); },
                delegate(string s) { raw.Warning(s); },
                delegate(string s) { raw.Error(s); });
            Init();
            Initialized = true;
        }

        protected abstract void Init();

        // Runs after EVERY enabled module's Init. The console web server starts here
        // so future modules (P2 /events, /api) can contribute their web modules
        // during Init - EmbedIO can't take new modules once the server is running.
        public virtual void PostInit() { }

        public virtual void OnUpdate() { }
        public virtual void Shutdown() { }
    }
}
