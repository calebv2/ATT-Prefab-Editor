using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;

namespace PrefabEditor.Modules
{
    // Replaces the game's dead Alta-cloud console auth with local RS256: tokens are
    // signed by attcmd.py / tokenSigner.js / attconsole.exe against the per-server
    // key in UserData\att_console. Fail closed on every path.
    public sealed class AuthModule : TavernModule
    {
        public override string Name { get { return "Auth"; } }

        protected override void Init()
        {
            Auth.Apply(Log, Ctx.KeyDir);
        }
    }

    static class Auth
    {
        static ModLog _log;
        static string _dir;

        public static void Apply(ModLog log, string dir)
        {
            _log = log;
            _dir = dir;
            try
            {
                Type t = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => { try { return a.GetType("ServerConsoleManager"); } catch { return null; } })
                    .FirstOrDefault(x => x != null);
                if (t == null) { _log.Msg("ServerConsoleManager not present - console auth not applied."); return; }

                MethodInfo target = t.GetMethod("ValidateConsoleToken", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (target == null) { _log.Error("ValidateConsoleToken not found; console will reject all requests."); return; }

                MethodInfo prefix = typeof(Auth).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
                new HarmonyLib.Harmony("prefabeditor.auth").Patch(target, new HarmonyMethod(prefix));
                _log.Msg("Secure console auth active. Trusted keys: " + Path.Combine(_dir, "trusted"));
            }
            catch (Exception e)
            {
                _log.Error("SecureConsoleAuth failed to apply, console will reject all requests: " + e);
            }
        }

        static bool Prefix(object token, ref Task<bool> __result)
        {
            bool ok = false;
            string uid = "?", kid = "?", jti = "?", reason = "ok";
            try
            {
                // The game repackages the bearer string into an alg:none token and puts the
                // original signed JWT in a "raw" claim. Verify that inner token.
                IDictionary payload = Dict(token, "Payload");
                string raw = Get(payload, "raw");
                string h, p, s;
                if (raw.Length > 0)
                {
                    string[] parts = raw.Split('.');
                    if (parts.Length < 3) { reason = "malformed token"; goto done; }
                    h = parts[0]; p = parts[1]; s = parts[2];
                }
                else
                {
                    h = Str(token, "RawHeader"); p = Str(token, "RawPayload"); s = Str(token, "RawSignature");
                    if (h.Length == 0 || p.Length == 0 || s.Length == 0) { reason = "missing raw token data"; goto done; }
                }

                string headerJson = Encoding.UTF8.GetString(Jwt.FromB64Url(h));
                string payloadJson = Encoding.UTF8.GetString(Jwt.FromB64Url(p));
                string alg = Jwt.JsonStr(headerJson, "alg");
                kid = Jwt.JsonStr(headerJson, "kid");
                uid = Jwt.JsonStr(payloadJson, "UserId");
                jti = Jwt.JsonStr(payloadJson, "jti");

                if (alg != "RS256") { reason = "unsupported alg '" + alg + "'"; goto done; }
                if (kid.Length == 0) { reason = "no kid"; goto done; }

                string pubPath = Path.Combine(Path.Combine(_dir, "trusted"), kid + ".pub.xml");
                if (!File.Exists(pubPath)) { reason = "unknown signing key"; goto done; }

                byte[] signingInput = Encoding.ASCII.GetBytes(h + "." + p);
                byte[] sig = Jwt.FromB64Url(s);
                if (!Jwt.VerifyRs256(signingInput, sig, File.ReadAllText(pubPath))) { reason = "bad signature"; goto done; }

                long expUnix = Jwt.JsonLong(payloadJson, "exp");
                if (expUnix == 0) { reason = "no expiry"; goto done; }
                if (Jwt.NowUnix() > expUnix + 60) { reason = "expired"; goto done; }

                if (uid.Length == 0 || !Allowlisted(uid)) { reason = "identity not allowlisted"; goto done; }

                ok = true;
            }
            catch (Exception e)
            {
                reason = "exception: " + e.Message;
            }

        done:
            Audit(ok, uid, kid, jti, reason);
            __result = Task.FromResult(ok);
            return false;
        }

        static bool Allowlisted(string uid)
        {
            string path = Path.Combine(_dir, "allowlist.txt");
            if (!File.Exists(path)) return false;
            foreach (string line in File.ReadAllLines(path))
                if (line.Trim() == uid) return true;
            return false;
        }

        static void Audit(bool ok, string uid, string kid, string jti, string reason)
        {
            try
            {
                string line = DateTime.UtcNow.ToString("o") + "\t" + (ok ? "ADMIT" : "REJECT")
                    + "\tuid=" + uid + "\tkid=" + kid + "\tjti=" + jti + "\t" + reason;
                File.AppendAllText(Path.Combine(_dir, "audit.log"), line + Environment.NewLine);
            }
            catch { }
            if (!ok) _log.Warning("Console auth rejected (" + reason + ") uid=" + uid);
        }

        static object Prop(object o, string name) { return o.GetType().GetProperty(name).GetValue(o, null); }
        static string Str(object o, string name) { object v = Prop(o, name); return v == null ? "" : v.ToString(); }
        static IDictionary Dict(object o, string name) { return (IDictionary)Prop(o, name); }
        static string Get(IDictionary d, string key) { return (d != null && d.Contains(key) && d[key] != null) ? d[key].ToString() : ""; }
    }
}
