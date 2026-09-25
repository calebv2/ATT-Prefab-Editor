using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Alta.WebServer;
using EmbedIO;

namespace PrefabEditor.Modules
{
    // The game hosts /console on the same EmbedIO server (port 1761) as /cache, which
    // serves world/save data, so console traffic contends with region loading. This
    // stands up a second EmbedIO server on a dedicated port hosting only the console.
    // The auth patch on ServerConsoleManager.ValidateConsoleToken applies regardless
    // of which server serves it.
    //
    // Loopback only: the console is a local trust surface (bot + attcmd run on the
    // same host, and Docker host networking shares its loopback). The old wildcard
    // bind left ufw as the only shield - never again. New capability = new PATH on
    // this server, never a new exposed port.
    public sealed class ConsoleHostModule : TavernModule
    {
        public override string Name { get { return "ConsoleHost"; } }

        protected override void Init()
        {
            // Server starts in PostInit: every other module's Init has run by then,
            // so P2+ modules can contribute web modules (paths) before EmbedIO locks.
        }

        public override void PostInit()
        {
            ConsolePort.Apply(Log, Ctx.KeyDir);
        }

        public override void Shutdown()
        {
            ConsolePort.Stop();
        }
    }

    static class ConsolePort
    {
        const int DefaultPort = Provisioner.DefaultConsolePort; // 1764

        static ModLog _log;
        static EmbedIO.WebServer _server;
        static readonly System.Collections.Generic.List<IWebModule> Pending =
            new System.Collections.Generic.List<IWebModule>();

        // Other modules add their web modules (paths) during Init; the server is
        // built with all of them in PostInit. New capability = new path here.
        public static void Contribute(IWebModule module)
        {
            lock (Pending)
            {
                if (module != null) Pending.Add(module);
            }
        }

        public static void Apply(ModLog log, string dir)
        {
            _log = log;
            try
            {
                if (FindType("ServerConsoleManager") == null)
                {
                    _log.Msg("No ServerConsoleManager present - dedicated console port not started.");
                    return;
                }

                int port = ReadPort(dir);
                string url = "http://127.0.0.1:" + port;

                _server = new EmbedIO.WebServer(delegate(WebServerOptions options)
                {
                    options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO);
                });
                _server.WithModule(new AltaConsoleModule("/console"));
                lock (Pending)
                {
                    foreach (IWebModule m in Pending) _server.WithModule(m);
                    if (Pending.Count > 0) _log.Msg(Pending.Count + " contributed web module(s) attached.");
                }

                Thread t = new Thread(Run);
                t.IsBackground = true;
                t.Start();

                WritePort(dir, port);
                _log.Msg("Dedicated console server on 127.0.0.1:" + port + " (/console isolated from /cache on 1761).");
            }
            catch (Exception e)
            {
                _log.Error("Failed to start (console still reachable on 1761): " + e);
            }
        }

        public static void Stop()
        {
            try { if (_server != null) _server.Dispose(); }
            catch { }
            _server = null;
        }

        static void Run()
        {
            try { _server.RunAsync().GetAwaiter().GetResult(); }
            catch (Exception e) { if (_log != null) _log.Error("Dedicated console server stopped: " + e); }
        }

        static Type FindType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { Type t = a.GetType(name); if (t != null) return t; }
                catch { }
            }
            return null;
        }

        static int ReadPort(string dir)
        {
            try
            {
                string path = Path.Combine(dir, "console_port.txt");
                if (File.Exists(path))
                {
                    int p;
                    if (int.TryParse(File.ReadAllText(path).Trim(), out p) && p > 0 && p < 65536)
                        return p;
                }
            }
            catch { }
            return DefaultPort;
        }

        static void WritePort(string dir, int port)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "console_port.txt"), port.ToString());
            }
            catch { }
        }
    }
}
