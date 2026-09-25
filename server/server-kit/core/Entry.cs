using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Alta.Console;
using MelonLoader;

[assembly: MelonInfo(typeof(PrefabEditor.Entry), "PrefabEditorCore", "1.2.0", "ATT Prefab Editor")]
[assembly: MelonGame(null, null)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]

// PrefabEditorCore - the server half of ATT Prefab Editor. One DLL for a
// self-hosted A Township Tale server: local RS256 console auth (the Alta cloud
// that used to mint console tokens is gone), a dedicated loopback console port,
// and the `edit` + `blueprint` command modules the web panel and in-game editor
// drive. Keys/allowlist self-provision into UserData\att_console on first boot.

namespace PrefabEditor
{
    public class Entry : MelonMod
    {
        CoreContext _ctx;
        TavernModule[] _modules;
        bool _up;

        public override void OnInitializeMelon()
        {
            MelonEvents.OnApplicationLateStart.Subscribe(Boot, 0, false);
        }

        void Boot()
        {
            MelonLogger.Instance log = LoggerInstance;
            try
            {
                string modsDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string gameDir = Directory.GetParent(modsDir).FullName;
                string userData = Path.Combine(gameDir, "UserData");

                _ctx = new CoreContext();
                _ctx.GameDir = gameDir;
                _ctx.UserDataDir = userData;
                _ctx.KeyDir = Path.Combine(userData, "att_console");
                _ctx.CoreDir = Path.Combine(userData, "PrefabEditor");
                _ctx.RawLog = log;
                _ctx.Dispatcher = new Dispatcher(log);

                _modules = new TavernModule[]
                {
                    new Modules.AuthModule(),
                    new Modules.ConsoleHostModule(),
                    new Modules.BlueprintsModule(),
                    new Modules.EditorModule()
                };

                _ctx.Config = CoreConfig.LoadOrCreate(_ctx.CoreDir, ModuleNames(), log);

                // Before Auth: a fresh install gets its keys while a provisioned
                // server is untouched, so the console admits the owner on first boot.
                try { Provisioner.Run(_ctx.KeyDir, gameDir, delegate(string s) { log.Msg("[Provision] " + s); }); }
                catch (Exception e) { log.Error("[Provision] failed (console will reject until keys exist): " + e); }

                List<string> status = new List<string>();
                foreach (TavernModule m in _modules)
                {
                    if (!_ctx.Config.ModuleEnabled(m.Name))
                    {
                        status.Add(m.Name + " off");
                        log.Msg("[" + m.Name + "] disabled by config.");
                        continue;
                    }
                    try
                    {
                        m.Boot(_ctx);
                        status.Add(m.Name + " on");
                    }
                    catch (Exception e)
                    {
                        status.Add(m.Name + " FAILED");
                        log.Error("[" + m.Name + "] init failed: " + e);
                    }
                }

                foreach (TavernModule m in _modules)
                {
                    if (!m.Initialized) continue;
                    try { m.PostInit(); }
                    catch (Exception e) { log.Error("[" + m.Name + "] post-init failed: " + e); }
                }

                RegisterCommandModules(log);

                _up = true;
                log.Msg("PrefabEditorCore 1.1.0 up: " + string.Join(", ", status.ToArray()));
            }
            catch (Exception e)
            {
                log.Error("PrefabEditorCore boot failed: " + e);
            }
        }

        static string[] ModuleNames()
        {
            return new string[] { "Auth", "ConsoleHost", "Blueprint", "Editor" };
        }

        // Console command registration is assembly-scoped in the game
        // (CommandCollection.Collect), so it lives here, not in the modules.
        // Both command modules enabled -> the proven Collect(assembly) path
        // (this assembly carries only `blueprint` and `edit`).
        // A subset -> per-type ProcessModule, best effort, so the flags stay real.
        void RegisterCommandModules(MelonLogger.Instance log)
        {
            bool blueprint = _ctx.Config.ModuleEnabled("Blueprint");
            bool editor = _ctx.Config.ModuleEnabled("Editor");
            Assembly asm = typeof(Entry).Assembly;

            try
            {
                if (blueprint && editor)
                {
                    CommandService.CommandCollection.Collect(asm);
                    log.Msg("Console command modules registered: blueprint, edit.");
                    return;
                }
                if (!blueprint && !editor)
                {
                    log.Msg("Command modules disabled - nothing registered with the console.");
                    return;
                }

                List<Type> types = new List<Type>();
                if (blueprint) types.Add(typeof(Modules.BlueprintAdminModule));
                if (editor) types.Add(typeof(Modules.EditorAdminModule));

                CommandCollection collection = CommandService.CommandCollection;
                int registered = 0;
                foreach (Type t in types)
                {
                    try
                    {
                        Alta.Console.Module m = collection.ProcessModule(t, asm, null);
                        if (m != null)
                        {
                            if (!collection.Modules.Contains(m)) collection.Modules.Add(m);
                            registered++;
                        }
                        else
                        {
                            log.Warning("ProcessModule returned nothing for " + t.Name + ".");
                        }
                    }
                    catch (Exception e)
                    {
                        log.Error("Registering " + t.Name + " failed: " + e.Message);
                    }
                }
                log.Msg("Console command modules registered per-type: " + registered + "/" + types.Count + ".");
            }
            catch (Exception e)
            {
                log.Error("Command registration failed: " + e);
            }
        }

        public override void OnUpdate()
        {
            if (!_up) return;
            _ctx.Dispatcher.Drain(64);
            for (int i = 0; i < _modules.Length; i++)
                if (_modules[i].Initialized) _modules[i].OnUpdate();
        }

        public override void OnApplicationQuit()
        {
            if (_modules == null) return;
            for (int i = _modules.Length - 1; i >= 0; i--)
            {
                if (!_modules[i].Initialized) continue;
                try { _modules[i].Shutdown(); }
                catch (Exception e) { LoggerInstance.Error("[" + _modules[i].Name + "] shutdown failed: " + e.Message); }
            }
            try { CommandService.CommandCollection.Unload(typeof(Entry).Assembly); } catch { }
        }
    }
}
