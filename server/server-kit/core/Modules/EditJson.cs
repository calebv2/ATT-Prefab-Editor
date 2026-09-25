using System;
using System.Globalization;
using System.Text;

// JSON string/number primitives for the edit module's one-line responses.
// Deliberately free of game and Newtonsoft references so the offline suite can
// compile and exercise the exact shipped code (run-tests.ps1 idiom).

namespace PrefabEditor.Modules
{
    public static class EditJson
    {
        // JSON string content: quotes, backslashes and control chars escaped.
        public static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                string repl = null;
                if (c == '"') repl = "\\\"";
                else if (c == '\\') repl = "\\\\";
                else if (c == '\n') repl = "\\n";
                else if (c == '\r') repl = "\\r";
                else if (c == '\t') repl = "\\t";
                else if (c < ' ') repl = "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture);
                if (repl == null)
                {
                    if (sb != null) sb.Append(c);
                    continue;
                }
                if (sb == null) sb = new StringBuilder(s.Substring(0, i), s.Length + 8);
                sb.Append(repl);
            }
            return sb == null ? s : sb.ToString();
        }

        // Invariant-culture float, 3 decimals max - a server locale with comma
        // decimals must never leak into the JSON.
        public static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "0";
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static string Err(string message)
        {
            return "{\"ok\":false,\"err\":\"" + Esc(message) + "\"}";
        }
    }
}
