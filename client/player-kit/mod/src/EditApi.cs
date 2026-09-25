// The mod talks to the same server `edit` module the web panel drives by calling
// the panel's /api/edit/* endpoints through PanelUrl (local loopback, SSH tunnel,
// or private Tailscale Serve URL). The panel holds RS256 console signing, so the
// mod needs no crypto of its own. The server stays authoritative for every edit.
//
// Requests go through UnityWebRequest inside a MelonCoroutine, so completion callbacks
// run on the main thread - no worker threads, no locks, Unity API is safe to touch in
// the callback. The panel wraps the console's one-line JSON as { ok, data } (runEdit);
// `data` is the edit module's own record. We parse only the handful of fields we use.
//
// C# 5 / net35 - no interpolation, no ?., no expression-bodied members.

using System;
using System.Collections;
using System.Globalization;
using System.Text;
using MelonLoader;
using UnityEngine.Networking;

namespace PrefabEditorMod
{
    // One entity as the edit module reports it (EditorModule.EntityRecord).
    public class EditEntity
    {
        public uint Id;
        public string Prefab = "";
        public uint Hash;
        public float X, Y, Z;      // world position
        public float Ex, Ey, Ez;   // euler
        public string[] Flags = new string[0];

        public bool HasFlag(string f)
        {
            for (int i = 0; i < Flags.Length; i++) if (Flags[i] == f) return true;
            return false;
        }

        public static EditEntity Parse(string obj)
        {
            if (obj == null) return null;
            EditEntity e = new EditEntity();
            e.Id = Json.UInt(obj, "id");
            string pf = Json.Str(obj, "prefab"); if (pf != null) e.Prefab = pf;
            e.Hash = Json.UInt(obj, "hash");
            e.X = Json.Num(obj, "x", 0f); e.Y = Json.Num(obj, "y", 0f); e.Z = Json.Num(obj, "z", 0f);
            e.Ex = Json.Num(obj, "ex", 0f); e.Ey = Json.Num(obj, "ey", 0f); e.Ez = Json.Num(obj, "ez", 0f);
            string flags = Json.Value(obj, "flags");
            if (flags != null) e.Flags = Json.StrArray(flags);
            return e;
        }
    }

    // A parsed edit response: Ok + optional entity, veto/error reason, delete dry-run flag.
    public class EditResult
    {
        public bool Ok;
        public string Err;          // veto / error sentence (server or transport)
        public bool NeedsConfirm;   // delete dry-run answered "add confirm ..."
        public string Note;         // e.g. craftbox "position re-saved"
        public EditEntity Ent;
        public EditEntity[] Entities = new EditEntity[0];
        public int Count;
        public int UndoCount;
        public int RedoCount;
        public bool HasConsoleStatus;
        public bool ConsoleReachable;
        public string UndoLabel = "";
        public string RedoLabel = "";
        public string Raw = "";     // full response body - tostring answers outside the edit wrapper

        public static EditResult Fail(string err)
        {
            EditResult r = new EditResult();
            r.Ok = false; r.Err = err;
            return r;
        }

        // body = the panel's HTTP JSON: {command, ok, data} | {command, ok:false, error}.
        public static EditResult Parse(string body)
        {
            EditResult r = new EditResult();
            if (string.IsNullOrEmpty(body)) { r.Ok = false; r.Err = "empty response"; return r; }
            r.Raw = body;
            string data = Json.Value(body, "data");
            if (data == null)
            {
                // transport-level failure or a non-edit shape (e.g. /api/health)
                r.Ok = Json.Bool(body, "ok", false);
                r.Err = Json.Str(body, "error");
                string console = Json.Value(body, "console");
                if (console != null)
                {
                    r.HasConsoleStatus = true;
                    r.ConsoleReachable = Json.Bool(console, "reachable", false);
                }
                return r;
            }
            r.Ok = Json.Bool(data, "ok", true);
            r.Err = Json.Str(data, "err");
            r.NeedsConfirm = Json.Bool(data, "needsConfirm", false);
            r.Count = (int)Json.Num(data, "count", 0f);
            r.Note = Json.Str(data, "note");
            r.UndoCount = (int)Json.Num(data, "undoCount", 0f);
            r.RedoCount = (int)Json.Num(data, "redoCount", 0f);
            r.UndoLabel = Json.Str(data, "undoLabel") ?? "";
            r.RedoLabel = Json.Str(data, "redoLabel") ?? "";
            string ent = Json.Value(data, "entity");
            if (ent == null) ent = Json.Value(data, "deleted");
            if (ent != null) r.Ent = EditEntity.Parse(ent);
            string entities = Json.Value(data, "entities");
            if (entities != null)
            {
                System.Collections.Generic.List<string> objects = Json.Objects(entities);
                r.Entities = new EditEntity[objects.Count];
                for (int i = 0; i < objects.Count; i++) r.Entities[i] = EditEntity.Parse(objects[i]);
            }
            return r;
        }
    }

    public class EditApi
    {
        readonly string _base;
        readonly Action<string> _log;

        public EditApi(string baseUrl, Action<string> log)
        {
            _base = (baseUrl != null ? baseUrl.TrimEnd('/') : "http://127.0.0.1:1766");
            _log = log;
        }

        public string BaseUrl { get { return _base; } }

        // ---- edit endpoints (bodies match the panel builders exactly) ----

        public void Info(uint id, Action<EditResult> cb)
        {
            Post("/api/edit/info", "{\"id\":" + id + "}", cb);
        }

        public void Move(uint id, float x, float y, float z, Action<EditResult> cb)
        {
            Post("/api/edit/move", "{\"id\":" + id + ",\"x\":" + F(x) + ",\"y\":" + F(y) + ",\"z\":" + F(z) + "}", cb);
        }

        public void MoveMany(string ids, float dx, float dy, float dz, Action<EditResult> cb)
        {
            Post("/api/edit/move-many", "{\"ids\":\"" + ids + "\",\"dx\":" + F(dx) + ",\"dy\":" + F(dy) + ",\"dz\":" + F(dz) + "}", cb);
        }

        public void ArrangeMany(string ids, string axis, string mode, float spacing, Action<EditResult> cb)
        {
            Post("/api/edit/arrange", "{\"ids\":\"" + ids + "\",\"axis\":\"" + axis
                + "\",\"mode\":\"" + mode + "\",\"spacing\":" + F(spacing) + "}", cb);
        }

        public void Rotate(uint id, float ex, float ey, float ez, Action<EditResult> cb)
        {
            Post("/api/edit/rotate", "{\"id\":" + id + ",\"x\":" + F(ex) + ",\"y\":" + F(ey) + ",\"z\":" + F(ez) + "}", cb);
        }

        public void RotateMany(string ids, float ex, float ey, float ez, Action<EditResult> cb)
        {
            Post("/api/edit/rotate-many", "{\"ids\":\"" + ids + "\",\"x\":" + F(ex) + ",\"y\":" + F(ey) + ",\"z\":" + F(ez) + "}", cb);
        }

        public void Transform(uint id, float x, float y, float z, float ex, float ey, float ez, Action<EditResult> cb)
        {
            Post("/api/edit/transform", "{\"id\":" + id + ",\"x\":" + F(x) + ",\"y\":" + F(y) + ",\"z\":" + F(z)
                + ",\"ex\":" + F(ex) + ",\"ey\":" + F(ey) + ",\"ez\":" + F(ez) + "}", cb);
        }

        public void Scale(uint id, float factor, Action<EditResult> cb)
        {
            Post("/api/edit/scale", "{\"id\":" + id + ",\"factor\":" + F(factor) + "}", cb);
        }

        public void Ground(uint id, Action<EditResult> cb)
        {
            Post("/api/edit/ground", "{\"id\":" + id + "}", cb);
        }

        // Two-step: confirm=false = dry-run (server answers needsConfirm or a veto);
        // confirm=true = destroy (server re-checks the vetoes again).
        public void Delete(uint id, bool confirm, Action<EditResult> cb)
        {
            string body = confirm ? "{\"id\":" + id + ",\"confirm\":true}" : "{\"id\":" + id + "}";
            Post("/api/edit/delete", body, cb);
        }

        public void DeleteMany(string ids, bool confirm, Action<EditResult> cb)
        {
            string body = "{\"ids\":\"" + ids + "\",\"confirm\":" + (confirm ? "true" : "false") + "}";
            Post("/api/edit/delete-many", body, cb);
        }

        public void DuplicateMany(string ids, float dx, float dy, float dz, Action<EditResult> cb)
        {
            Post("/api/edit/duplicate-many", "{\"ids\":\"" + ids + "\",\"dx\":" + F(dx)
                + ",\"dy\":" + F(dy) + ",\"dz\":" + F(dz) + "}", cb);
        }

        public void Undo(Action<EditResult> cb)
        {
            Post("/api/edit/undo", "{}", cb);
        }

        public void Redo(Action<EditResult> cb)
        {
            Post("/api/edit/redo", "{}", cb);
        }

        public void History(Action<EditResult> cb)
        {
            Send("GET", "/api/edit/history", null, cb);
        }

        // One-shot save-string export (panel -> blueprint string <id>). The answer is
        // NOT the edit wrapper: top-level {ok,id,string,label,length} - read string
        // and label from r.Raw with Json.Str.
        public void ExportString(uint id, Action<EditResult> cb)
        {
            Post("/api/edit/tostring", "{\"id\":" + id + "}", cb);
        }

        // Respawn an exported save string at a point (edit paste). Standard wrapper
        // back; the NEW entity (fresh id) rides in r.Ent. data is pre-validated to
        // the save-string charset (digits/commas/pipe/./+/-), so it's JSON-safe raw.
        public void Paste(float x, float y, float z, string data, Action<EditResult> cb)
        {
            Post("/api/edit/paste", "{\"x\":" + F(x) + ",\"y\":" + F(y) + ",\"z\":" + F(z)
                + ",\"string\":\"" + data + "\"}", cb);
        }

        // In-place swap (edit replace): the selected entity dies, the string
        // stands at its exact spot+rotation. Standard wrapper back; the NEW
        // entity rides in r.Ent (fresh id). Same pre-validated charset as Paste.
        public void Replace(uint id, string data, Action<EditResult> cb)
        {
            Post("/api/edit/replace", "{\"id\":" + id + ",\"string\":\"" + data + "\"}", cb);
        }

        // Catalog search through the panel (GET /api/prefabs, spawnable only). The
        // answer is top-level {count,results:[{name,hash,spawnable}]} - no "ok"
        // field, so ignore r.Ok and read results from r.Raw (Json.Objects).
        public void Prefabs(string q, int limit, Action<EditResult> cb)
        {
            string esc = Uri.EscapeDataString(q == null ? "" : q);
            Send("GET", "/api/prefabs?spawnable=1&limit=" + limit + "&q=" + esc, null, cb);
        }

        // Read the server's actual spawnable list. This is the same console
        // command Prefabulator uses, so the in-game catalog follows the connected
        // server instead of a potentially stale packaged snapshot.
        public void Prefabs(Action<EditResult> cb)
        {
            Post("/api/command", "{\"command\":\"spawn list\"}", cb);
        }

        public void Players(Action<EditResult> cb)
        {
            Send("GET", "/api/edit/players", null, cb);
        }

        public void Command(string command, Action<EditResult> cb)
        {
            Post("/api/command", "{\"command\":\"" + EscapeJson(command) + "\"}", cb);
        }

        static string EscapeJson(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n");
        }

        // Spawn a catalog prefab at a point (edit spawnat). The hash is the whole
        // token - names can contain spaces, hashes can't. New entity in r.Ent.
        public void SpawnAt(float x, float y, float z, uint prefabHash, Action<EditResult> cb)
        {
            Post("/api/edit/spawnat", "{\"x\":" + F(x) + ",\"y\":" + F(y) + ",\"z\":" + F(z)
                + ",\"prefab\":\"" + prefabHash + "\"}", cb);
        }

        // Reachability probe: GET /api/health (is the tunnel + panel up?).
        public void Health(Action<EditResult> cb)
        {
            Send("GET", "/api/health", null, cb);
        }

        // ---- plumbing ----

        void Post(string path, string body, Action<EditResult> cb)
        {
            Send("POST", path, body, cb);
        }

        void Send(string method, string path, string body, Action<EditResult> cb)
        {
            SendUrl(_base + path, body, "panel", cb, method);
        }

        void SendUrl(string url, string body, string service, Action<EditResult> cb,
            string method = "POST")
        {
            try { MelonCoroutines.Start(RunUrl(url, method, body, service, cb)); }
            catch (Exception e)
            {
                if (_log != null) _log("edit request failed to start: " + e.Message);
                Invoke(cb, EditResult.Fail("could not start request: " + e.Message));
            }
        }

        IEnumerator RunUrl(string url, string method, string body, string service, Action<EditResult> cb)
        {
            UnityWebRequest req = new UnityWebRequest(url, method);
            if (body != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                req.uploadHandler = new UploadHandlerRaw(bytes);
                req.SetRequestHeader("Content-Type", "application/json");
            }
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = 6; // fail fast if the tunnel/panel is down

            yield return req.SendWebRequest();

            EditResult r;
            if (req.result != UnityWebRequest.Result.Success)
            {
                string why = req.error;
                if (string.IsNullOrEmpty(why)) why = "HTTP " + req.responseCode;
                r = service == "workbench"
                    ? EditResult.Fail("resize needs the local String Workbench at 127.0.0.1:1767; run player-kit/workbench/start-workbench.bat and retry (" + why + ")")
                    : EditResult.Fail("panel unreachable: " + why + " (panel running? tunnel up?)");
            }
            else
            {
                string text = "";
                try { text = req.downloadHandler.text; } catch { }
                r = EditResult.Parse(text);
            }
            try { req.Dispose(); } catch { }

            Invoke(cb, r);
        }

        void Invoke(Action<EditResult> cb, EditResult r)
        {
            if (cb == null) return;
            try { cb(r); }
            catch (Exception e) { if (_log != null) _log("edit callback threw: " + e.Message); }
        }

        // Invariant-culture, 3-decimal, never exponential - the panel's coord() validator
        // rejects anything else. Mirrors EditJson.F on the server side.
        static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "0";
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    // A minimal, dependency-free reader for our OWN known JSON shapes. Not a general
    // parser - it pulls a value by key at the top level of one object, tracking string
    // and brace/bracket nesting so nested objects don't confuse a key match.
    internal static class Json
    {
        // The raw substring of the value for `key` at depth-1 of object `obj`
        // ({...} or [...]); null if not found. Value keeps its quotes/braces.
        public static string Value(string obj, string key)
        {
            if (obj == null) return null;
            int n = obj.Length, depth = 0;
            bool inStr = false;
            string want = "\"" + key + "\"";
            for (int i = 0; i < n; i++)
            {
                char c = obj[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"')
                {
                    if (depth == 1 && IsKey(obj, i, want))
                    {
                        int j = i + want.Length;
                        while (j < n && obj[j] != ':') j++;
                        j++;
                        while (j < n && char.IsWhiteSpace(obj[j])) j++;
                        return Read(obj, j);
                    }
                    inStr = true;
                    continue;
                }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
            }
            return null;
        }

        static bool IsKey(string s, int at, string want)
        {
            if (at + want.Length > s.Length) return false;
            for (int k = 0; k < want.Length; k++) if (s[at + k] != want[k]) return false;
            int j = at + want.Length;
            while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            return j < s.Length && s[j] == ':';
        }

        static string Read(string s, int i)
        {
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '"')
            {
                int j = i + 1;
                while (j < s.Length)
                {
                    if (s[j] == '\\') { j += 2; continue; }
                    if (s[j] == '"') break;
                    j++;
                }
                return s.Substring(i, j - i + 1);
            }
            if (c == '{' || c == '[')
            {
                char close = (c == '{') ? '}' : ']';
                int depth = 0, j = i;
                bool inStr = false;
                for (; j < s.Length; j++)
                {
                    char d = s[j];
                    if (inStr)
                    {
                        if (d == '\\') { j++; continue; }
                        if (d == '"') inStr = false;
                        continue;
                    }
                    if (d == '"') { inStr = true; continue; }
                    if (d == c) depth++;
                    else if (d == close) { depth--; if (depth == 0) { j++; break; } }
                }
                return s.Substring(i, j - i);
            }
            int k = i;
            while (k < s.Length && ",}] \t\r\n".IndexOf(s[k]) < 0) k++;
            return s.Substring(i, k - i);
        }

        public static string Str(string obj, string key)
        {
            string v = Value(obj, key);
            if (v == null || v.Length < 2 || v[0] != '"') return null;
            return Unescape(v.Substring(1, v.Length - 2));
        }

        public static bool Bool(string obj, string key, bool dflt)
        {
            string v = Value(obj, key);
            if (v == null) return dflt;
            return v == "true";
        }

        public static float Num(string obj, string key, float dflt)
        {
            string v = Value(obj, key);
            float f;
            if (v != null && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return f;
            return dflt;
        }

        public static uint UInt(string obj, string key)
        {
            string v = Value(obj, key);
            uint u;
            if (v != null && uint.TryParse(v, out u)) return u;
            return 0;
        }

        // Top-level {...} chunks of a JSON array string (depth-aware, string-safe) -
        // enough to walk /api/prefabs results without a real parser.
        public static System.Collections.Generic.List<string> Objects(string arr)
        {
            System.Collections.Generic.List<string> outp = new System.Collections.Generic.List<string>();
            if (arr == null) return outp;
            int n = arr.Length, depth = 0, start = -1;
            bool inStr = false;
            for (int i = 0; i < n; i++)
            {
                char c = arr[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        outp.Add(arr.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }
            return outp;
        }

        public static string[] StrArray(string arr)
        {
            System.Collections.Generic.List<string> outp = new System.Collections.Generic.List<string>();
            if (arr == null) return outp.ToArray();
            int i = 0, n = arr.Length;
            while (i < n)
            {
                if (arr[i] == '"')
                {
                    int j = i + 1; StringBuilder sb = new StringBuilder();
                    while (j < n)
                    {
                        if (arr[j] == '\\' && j + 1 < n) { sb.Append(arr[j + 1]); j += 2; continue; }
                        if (arr[j] == '"') break;
                        sb.Append(arr[j]); j++;
                    }
                    outp.Add(sb.ToString());
                    i = j + 1;
                }
                else i++;
            }
            return outp.ToArray();
        }

        static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char d = s[++i];
                if (d == 'n') sb.Append('\n');
                else if (d == 'r') sb.Append('\r');
                else if (d == 't') sb.Append('\t');
                else if (d == 'u' && i + 4 < s.Length)
                {
                    int code;
                    if (int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                    { sb.Append((char)code); i += 4; }
                    else sb.Append(d);
                }
                else sb.Append(d);
            }
            return sb.ToString();
        }
    }
}
