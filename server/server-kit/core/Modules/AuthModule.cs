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
        const string HarmonyId = "prefabeditor.auth";

        static ModLog _log;
        static string _dir;

        // True when another prefix (TavernLib's HS256 owner-token gate) also sits on
        // ValidateConsoleToken. Only then may we DECLINE a token that isn't ours and
        // let them answer. With no one behind us, declining would fall through to the
        // game's dead Alta-cloud validator, so we stay fail-closed instead.
        static bool _chained;

        // The verdict our prefix reached, handed to the postfix so it can re-assert it
        // after any other prefix has had its say. ThreadStatic because the console
        // serves requests concurrently; prefix and postfix bracket the same call on the
        // same thread, so this never crosses request boundaries.
        [ThreadStatic] static bool _weDecided;
        [ThreadStatic] static bool _ourVerdict;

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
                MethodInfo postfix = typeof(Auth).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic);

                // TavernLib (TavernLauncher's server mod) has its own prefix here for its
                // HS256 console_token.txt, which the launcher window uses. Every prefix runs
                // even after one returns false, and each overwrites __result, so the last
                // one to run wins. Priority.First puts us ahead of theirs; the postfix runs
                // after every prefix and re-asserts our verdict for RS256 tokens only.
                HarmonyMethod hm = new HarmonyMethod(prefix);
                hm.priority = Priority.First;
                new HarmonyLib.Harmony(HarmonyId).Patch(target, hm, new HarmonyMethod(postfix));

                CheckChain(target);

                _log.Msg("Secure console auth active (" + (_chained ? "chained with TavernLib" : "sole gate")
                    + "). Trusted keys: " + Path.Combine(_dir, "trusted"));
            }
            catch (Exception e)
            {
                _log.Error("SecureConsoleAuth failed to apply, console will reject all requests: " + e);
            }
        }

        // TavernLib's gate cannot decline: it hard-rejects any token without a
        // server_owner claim. If a future TavernLib outranks our priority it runs first,
        // and without the postfix every RS256 token would be refused. Say so loudly.
        static void CheckChain(MethodBase target)
        {
            try
            {
                Patches info = HarmonyLib.Harmony.GetPatchInfo(target);
                if (info == null || info.Prefixes == null) return;

                foreach (Patch p in info.Prefixes)
                {
                    if (p.owner == HarmonyId) continue;
                    _chained = true;
                    if (p.priority >= Priority.First)
                        _log.Warning("Console auth: prefix '" + p.owner + "' has priority " + p.priority
                            + " and runs before ours. The postfix still decides RS256 tokens.");
                }
            }
            catch (Exception e)
            {
                _log.Warning("Could not verify console auth patch order: " + e.Message);
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
                    if (parts.Length < 3) { reason = "malformed token"; goto notOurs; }
                    h = parts[0]; p = parts[1]; s = parts[2];
                }
                else
                {
                    h = Str(token, "RawHeader"); p = Str(token, "RawPayload"); s = Str(token, "RawSignature");
                    if (h.Length == 0 || p.Length == 0 || s.Length == 0) { reason = "missing raw token data"; goto notOurs; }
                }

                string headerJson = Encoding.UTF8.GetString(Jwt.FromB64Url(h));
                string payloadJson = Encoding.UTF8.GetString(Jwt.FromB64Url(p));
                string alg = Jwt.JsonStr(headerJson, "alg");
                kid = Jwt.JsonStr(headerJson, "kid");
                uid = Jwt.JsonStr(payloadJson, "UserId");
                jti = Jwt.JsonStr(payloadJson, "jti");

                // Anything we cannot positively identify as one of ours is "not ours",
                // not "invalid". TavernLib's tokens are HS256 and carry no kid.
                if (alg != "RS256") { reason = "not ours (alg '" + alg + "')"; goto notOurs; }

                // Past this line the token IS ours, so every failure is a real rejection.
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
                goto done;

            notOurs:
                // TavernLib is behind us: decline without touching __result so its gate
                // decides. It still hard-rejects anything without a valid server_owner
                // HMAC, so passing the token on is not the same as opening the door.
                if (_chained) { _weDecided = false; return true; }
                // Sole gate: nothing behind us but the game's dead Alta-cloud validator,
                // so refuse rather than fall through to it.
                goto done;
            }
            catch (Exception e)
            {
                reason = "exception: " + e.Message;
            }

        done:
            Audit(ok, uid, kid, jti, reason);
            _weDecided = true;
            _ourVerdict = ok;
            __result = Task.FromResult(ok);
            return false;
        }

        // The last word, for tokens the prefix identified as ours. TavernLib's own
        // tokens fall through untouched and keep its verdict.
        static void Postfix(ref Task<bool> __result)
        {
            if (!_weDecided) return;
            _weDecided = false;
            __result = Task.FromResult(_ourVerdict);
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
