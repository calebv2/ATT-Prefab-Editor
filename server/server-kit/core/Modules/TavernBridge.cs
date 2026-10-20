using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Alta.Console;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PrefabEditor.Modules
{
    public static partial class EditorAdminModule
    {
        static string bridgeUserData;
        internal static void InitializeBridge(string userData) { bridgeUserData = userData; }

        [Command("capabilities", "Report Prefab Editor launcher integration support")]
        public static string Capabilities()
        { return "{\"ok\":true,\"tavernBridge\":1,\"version\":\"1.6.1\"}"; }

        static string BoardFile { get { return Path.Combine(bridgeUserData, "InfoBoards.json"); } }
        static JObject BoardDocument()
        {
            if (new FileInfo(BoardFile).Length > 4000000) throw new Exception("InfoBoards.json is too large");
            JObject document = JObject.Parse(File.ReadAllText(BoardFile));
            if (!(document["boards"] is JObject)) throw new Exception("InfoBoards.json must contain a boards object");
            return document;
        }

        [Command("infoboards", "Read server InfoBoards settings for the editor")]
        public static string InfoBoards()
        {
            try {
                if (!File.Exists(BoardFile)) return "{\"ok\":true,\"configured\":false,\"boards\":[]}";
                var boards = new JArray();
                foreach (var property in ((JObject)BoardDocument()["boards"]).Properties()) {
                    JObject board = property.Value as JObject;
                    if (board == null) continue;
                    boards.Add(new JObject(new JProperty("key", property.Name), new JProperty("channel", board["channel"]),
                        new JProperty("text", board["text"]), new JProperty("rotation", board["rotation"])));
                }
                return new JObject(new JProperty("ok", true), new JProperty("configured", true), new JProperty("boards", boards)).ToString(Formatting.None);
            } catch (Exception e) { return EditJson.Err(e.Message); }
        }

        static string BoardText(JToken value, bool nonempty)
        {
            if (value == null || value.Type != JTokenType.String) throw new Exception("board text must be a string");
            string text = (string)value;
            if (text.Length > 5000 || (nonempty && text.Length == 0)) throw new Exception("board text must contain " + (nonempty ? "1" : "0") + "-5000 characters");
            return text;
        }

        [Command("infoboardset", "Update a registered server InfoBoard using base64 JSON")]
        public static string InfoBoardSet(string encoded)
        {
            string temporary = null;
            try {
                if (encoded == null || encoded.Length > 210000) throw new Exception("board update is too large");
                JObject body = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
                string key = (string)body["key"];
                if (key == null || !Regex.IsMatch(key, @"^-?\d+\.\d,-?\d+\.\d,-?\d+\.\d$")) throw new Exception("invalid board position key");
                if (!File.Exists(BoardFile)) throw new Exception("Install LiveInfoBoards on the server first");
                JObject document = BoardDocument();
                JObject board = document["boards"][key] as JObject;
                if (board == null) throw new Exception("This board is not registered; load it in-world, then refresh");
                string text = BoardText(body["text"], false);
                JToken rotation = body["rotation"];
                if (rotation == null) throw new Exception("rotation settings are missing");
                if (rotation.Type != JTokenType.Null) {
                    JObject r = rotation as JObject;
                    if (r == null || r["enabled"] == null || r["enabled"].Type != JTokenType.Boolean ||
                        r["interval"] == null || r["interval"].Type != JTokenType.Integer || (long)r["interval"] < 1 ||
                        (long)r["interval"] > 86400 || ((string)r["unit"] != "seconds" && (string)r["unit"] != "minutes") ||
                        !(r["messages"] is JArray) || ((JArray)r["messages"]).Count > 20) throw new Exception("invalid rotation settings");
                    foreach (JToken message in (JArray)r["messages"]) BoardText(message, true);
                }
                board["text"] = text;
                if (rotation.Type == JTokenType.Null) board.Remove("rotation"); else board["rotation"] = rotation;
                temporary = BoardFile + ".prefab-" + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, document.ToString(Formatting.Indented));
                File.Replace(temporary, BoardFile, BoardFile + ".prefab-backup");
                return new JObject(new JProperty("ok", true), new JProperty("key", key), new JProperty("board", board)).ToString(Formatting.None);
            } catch (Exception e) { return EditJson.Err(e.Message); }
            finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
