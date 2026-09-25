using System;

namespace PrefabEditor
{
    // Per-module logger: MelonLoader already stamps [PrefabEditor]; this adds the
    // module prefix so one log stream stays readable. Delegate-backed (no
    // MelonLoader types) so game-free modules like EventsSocket can be compiled
    // and exercised by the offline harness; the kernel feeds it MelonLogger.
    public sealed class ModLog
    {
        readonly Action<string> _msg;
        readonly Action<string> _warn;
        readonly Action<string> _err;
        readonly string _prefix;

        public ModLog(string moduleName, Action<string> msg, Action<string> warn, Action<string> err)
        {
            _prefix = "[" + moduleName + "] ";
            _msg = msg;
            _warn = warn;
            _err = err;
        }

        public void Msg(string s) { if (_msg != null) _msg(_prefix + s); }
        public void Warning(string s) { if (_warn != null) _warn(_prefix + s); }
        public void Error(string s) { if (_err != null) _err(_prefix + s); }
    }
}
