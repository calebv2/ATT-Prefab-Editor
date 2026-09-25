using System;
using System.IO;
using System.Text;

namespace PrefabEditor
{
    // The console's token check as plain shipped code: alg -> kid -> trusted key ->
    // RS256 signature -> exp -> allowlist, fail closed. Used by every surface that
    // receives a RAW JWT string (the /events websocket, /api, the offline tests).
    // The /console path keeps AuthModule's Harmony prefix, which must first unwrap
    // the game's alg:none repackaging before running these same checks via Jwt.
    public static class TokenCheck
    {
        // Returns null on success (uid holds the admitted UserId), else the reject reason.
        public static string Check(string keyDir, string token, out string uid)
        {
            uid = "";
            try
            {
                if (token == null) return "no token";
                string[] parts = token.Trim().Split('.');
                if (parts.Length < 3) return "malformed token";
                string h = parts[0], p = parts[1], s = parts[2];

                string headerJson = Encoding.UTF8.GetString(Jwt.FromB64Url(h));
                string payloadJson = Encoding.UTF8.GetString(Jwt.FromB64Url(p));
                string alg = Jwt.JsonStr(headerJson, "alg");
                string kid = Jwt.JsonStr(headerJson, "kid");
                uid = Jwt.JsonStr(payloadJson, "UserId");

                if (alg != "RS256") return "unsupported alg '" + alg + "'";
                if (kid.Length == 0) return "no kid";

                string pubPath = Path.Combine(Path.Combine(keyDir, "trusted"), kid + ".pub.xml");
                if (!File.Exists(pubPath)) return "unknown signing key";

                byte[] signingInput = Encoding.ASCII.GetBytes(h + "." + p);
                byte[] sig = Jwt.FromB64Url(s);
                if (!Jwt.VerifyRs256(signingInput, sig, File.ReadAllText(pubPath))) return "bad signature";

                long expUnix = Jwt.JsonLong(payloadJson, "exp");
                if (expUnix == 0) return "no expiry";
                if (Jwt.NowUnix() > expUnix + 60) return "expired";

                if (uid.Length == 0 || !Allowlisted(keyDir, uid)) return "identity not allowlisted";
                return null;
            }
            catch (Exception e)
            {
                return "exception: " + e.Message;
            }
        }

        static bool Allowlisted(string keyDir, string uid)
        {
            string path = Path.Combine(keyDir, "allowlist.txt");
            if (!File.Exists(path)) return false;
            foreach (string line in File.ReadAllLines(path))
                if (line.Trim() == uid) return true;
            return false;
        }
    }
}
