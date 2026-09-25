using System;
using System.Security.Cryptography;
using System.Text;

namespace PrefabEditor
{
    // Pure-BCL JWT plumbing shared by the auth verifier and the offline test harness.
    // No game or MelonLoader types here - this file must compile standalone so the
    // exact shipped verify path can be exercised outside the game.
    public static class Jwt
    {
        public static byte[] FromB64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            int m = s.Length % 4;
            if (m == 2) s += "==";
            else if (m == 3) s += "=";
            return Convert.FromBase64String(s);
        }

        // RS256 = RSA PKCS#1 v1.5 over SHA256(header.payload). Verified through
        // RSACryptoServiceProvider.FromXmlString, the one RSA surface the game's
        // Mono runtime is proven to implement (live-verified on the VPS).
        public static bool VerifyRs256(byte[] signingInput, byte[] sig, string pubXml)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(signingInput);
                RSACryptoServiceProvider rsa = new RSACryptoServiceProvider();
                rsa.FromXmlString(pubXml);
                return rsa.VerifyHash(hash, CryptoConfig.MapNameToOID("SHA256"), sig);
            }
        }

        // Minimal scanners for flat JWT header/payload JSON. Good enough for tokens
        // we mint ourselves; never used on hostile nested documents (a failed scan
        // just fails the auth check, which fails closed).
        public static string JsonStr(string json, string key)
        {
            string k = "\"" + key + "\"";
            int i = json.IndexOf(k);
            if (i < 0) return "";
            i = json.IndexOf(':', i + k.Length);
            if (i < 0) return "";
            i++;
            while (i < json.Length && json[i] == ' ') i++;
            if (i >= json.Length || json[i] != '"') return "";
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < json.Length && json[i] != '"')
            {
                if (json[i] == '\\' && i + 1 < json.Length) { sb.Append(json[i + 1]); i += 2; }
                else { sb.Append(json[i]); i++; }
            }
            return sb.ToString();
        }

        public static long JsonLong(string json, string key)
        {
            string k = "\"" + key + "\"";
            int i = json.IndexOf(k);
            if (i < 0) return 0;
            i = json.IndexOf(':', i + k.Length);
            if (i < 0) return 0;
            i++;
            while (i < json.Length && json[i] == ' ') i++;
            int start = i;
            while (i < json.Length && char.IsDigit(json[i])) i++;
            long v;
            return long.TryParse(json.Substring(start, i - start), out v) ? v : 0;
        }

        public static long NowUnix()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }
    }
}
