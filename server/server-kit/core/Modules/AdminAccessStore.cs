using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PrefabEditor.Modules
{
    internal sealed class AdminAccessRecord
    {
        public string Id, Name, Digest;
        public bool Revoked;
        public string[] Tabs;
    }
    internal sealed class AdminAccessStore
    {
        internal static readonly string[] TabNames = { "select", "arrange", "build", "world", "admin", "boards", "objectLoader", "teleporter", "trading", "settings" };
        internal readonly object Gate = new object();
        readonly string file;
        List<AdminAccessRecord> records = new List<AdminAccessRecord>();
        string loadError;
        internal AdminAccessStore(string path)
        {
            file = path;
            try
            {
                if (File.Exists(file))
                {
                    records = JsonConvert.DeserializeObject<List<AdminAccessRecord>>(File.ReadAllText(file));
                    if (records == null || records.Any(r => r == null || !Regex.IsMatch(r.Id ?? "", "^[a-f0-9]{32}$")
                        || string.IsNullOrEmpty(r.Name) || !Regex.IsMatch(r.Digest ?? "", "^[a-f0-9]{64}$")
                        || r.Tabs == null || r.Tabs.Any(t => !TabNames.Contains(t)))
                        || records.Select(r => r.Id).Distinct().Count() != records.Count)
                        throw new Exception("Invalid admin access file");
                }
            }
            catch { records = new List<AdminAccessRecord>(); loadError = "Admin access file could not be loaded. Restore its backup before changing access."; }
        }
        internal static string Hash(string value)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
        static bool Equal(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0; for (int i=0;i<a.Length;i++) diff |= a[i]^b[i]; return diff == 0;
        }
        internal AdminAccessRecord Authenticate(string token)
        {
            lock (Gate)
            {
                if (loadError != null || token == null || token.Length > 180) return null;
                string[] parts = token.Split('.');
                if (parts.Length != 3 || parts[0] != "pfa1" || !Regex.IsMatch(parts[1], "^[a-f0-9]{32}$")
                    || !Regex.IsMatch(parts[2], "^[a-f0-9]{64}$")) return null;
                var record = records.FirstOrDefault(r => r.Id == parts[1] && !r.Revoked);
                return record != null && Equal(record.Digest, Hash(token)) ? record : null;
            }
        }
        internal AdminAccessRecord Current(string id, string digest)
        { lock (Gate) return records.FirstOrDefault(r => r.Id == id && !r.Revoked && Equal(r.Digest, digest)); }
        internal JObject List()
        {
            lock (Gate)
            {
                if (loadError != null) throw new Exception(loadError);
                return new JObject(new JProperty("ok",true),new JProperty("admins",new JArray(records.Select(r => new JObject(
                    new JProperty("id",r.Id),new JProperty("name",r.Name),new JProperty("revoked",r.Revoked),new JProperty("tabs",new JArray(r.Tabs)))))));
            }
        }
        internal JObject Change(JObject request)
        {
            lock (Gate)
            {
                if (loadError != null) throw new Exception(loadError);
                string action = (string)request["action"], id = (string)request["id"];
                // Mutate a copy; failed persistence must not change the live authorization state.
                var next = JsonConvert.DeserializeObject<List<AdminAccessRecord>>(JsonConvert.SerializeObject(records));
                var record = next.FirstOrDefault(r => r.Id == id);
                string token = null;
                if (action == "create")
                {
                    record = new AdminAccessRecord { Id=Guid.NewGuid().ToString("N") };
                    next.Add(record);
                }
                else if (record == null) throw new Exception("Admin not found");
                if (action == "create" || action == "update")
                {
                    string name = ((string)request["name"] ?? "").Trim();
                    if (name.Length < 1 || name.Length > 80 || name.Any(char.IsControl)) throw new Exception("Enter a name of 1-80 characters");
                    if (next.Any(r => r.Id != record.Id && string.Equals(r.Name,name,StringComparison.OrdinalIgnoreCase))) throw new Exception("That admin name already exists");
                    JArray tabs = request["tabs"] as JArray;
                    if (tabs == null || tabs.Any(t => t.Type != JTokenType.String || !TabNames.Contains((string)t))) throw new Exception("Choose valid tabs");
                    if (record.Revoked) throw new Exception("Replace the token before updating a revoked admin");
                    record.Name=name; record.Tabs=tabs.Select(t=>(string)t).Distinct().ToArray();
                }
                else if (action == "revoke") record.Revoked=true;
                else if (action != "rotate") throw new Exception("Unknown access action");
                if (action == "create" || action == "rotate")
                {
                    byte[] secret=new byte[32]; using (var rng=RandomNumberGenerator.Create()) rng.GetBytes(secret);
                    token="pfa1."+record.Id+"."+BitConverter.ToString(secret).Replace("-", "").ToLowerInvariant();
                    record.Digest=Hash(token); record.Revoked=false;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                string temp=file+".tmp"; File.WriteAllText(temp,JsonConvert.SerializeObject(next,Formatting.Indented));
                if(File.Exists(file)) File.Replace(temp,file,file+".bak"); else File.Move(temp,file);
                records=next;
                var result=new JObject(new JProperty("ok",true),new JProperty("id",record.Id));
                if(token!=null) result["token"]=token;
                return result;
            }
        }
        internal static bool Allowed(string command, string[] tabs)
        {
            if(string.IsNullOrWhiteSpace(command) || command.Length>512000 || command.IndexOfAny(new[]{'\r','\n','\0',';','|','&','`'})>=0) return false;
            string[] words=command.Trim().Split(new[]{' ','\t'},StringSplitOptions.RemoveEmptyEntries);
            string key=string.Join(" ",words.Take(2).ToArray()).ToLowerInvariant();
            if(command == "help" || command == "edit capabilities" || command == "edit players" || command == "spawn list") return true;
            if(key=="edit scan" || key=="edit scanat" || key=="edit info" || key=="blueprint string") return tabs.Length>0;
            string tab=null;
            switch(key)
            {
                case "edit delete": case "edit deletegroup": tab="select"; break;
                case "edit move": case "edit movegroup": case "edit rotate": case "edit rotategroup": case "edit transform":
                case "edit scale": case "edit ground": case "edit arrangegroup": tab="arrange"; break;
                case "edit spawnat": case "edit paste": case "edit replace": case "edit duplicategroup": case "spawn exact": tab="build"; break;
                case "edit teleport": case "edit teleportplayer": case "edit nudge": case "player get-home": case "player set-home": case "player teleport": tab="world"; break;
                case "edit kick": case "edit kill": case "edit ban": case "edit bans": case "edit unban": case "edit broadcast":
                case "edit setserverspawn": case "edit resetserverspawn": case "time set": tab="admin"; break;
                case "edit infoboards": case "edit infoboardset": tab="boards"; break;
                case "edit linkteleporter": tab="teleporter"; break;
                case "edit tradeinfo": case "edit tradeset": case "edit tradeaddstock": tab="trading"; break;
                case "edit undo": case "edit redo": case "edit history":
                    return new[]{"select","arrange","build"}.All(t=>tabs.Contains(t));
            }
            if(tab!=null) return tabs.Contains(tab);
            if(key=="settings changesetting" && tabs.Contains("settings") && words.Length==5 && words[2]=="server")
            {
                if(new[]{"DropAllOnDeath","IsPVPEnabled","HungerDealsDamage"}.Contains(words[3])) return words[4]=="true" || words[4]=="false";
                float value;
                return new[]{"RespawnTimeSeconds","TimeSpeedMultiplier","XPX","DamageX","PVPMultiplier","PVPCrippleMultiplier","HungerTick","CommunityStorageMultiplier"}.Contains(words[3])
                    && float.TryParse(words[4],NumberStyles.Float,CultureInfo.InvariantCulture,out value) && !float.IsNaN(value) && !float.IsInfinity(value) && value>=0;
            }
            return false;
        }
    }
}
