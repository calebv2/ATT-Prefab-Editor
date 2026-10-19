using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Alta.Console;
using Alta.Core.Threading;
using Alta.Api.DataTransferModels.Models.Responses;
using Fleck;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PrefabEditor.Modules
{
    // Personal sessions never enter the native unrestricted owner-authentication path.
    // Both admission and queued execution consult the current persisted access record.
    internal static class AdminAccess
    {
        sealed class Session
        {
            internal WebSocketCommandHandler Handler;
            internal IWebSocketConnection Socket;
            internal string Id, Digest;
            internal CommandContext Context;
            internal int Pending;
        }
        static readonly object gate = new object();
        static readonly Dictionary<WebSocketCommandHandler,Session> sessions = new Dictionary<WebSocketCommandHandler,Session>();
        static AdminAccessStore store;
        static HarmonyLib.Harmony harmony;
        static int pending;
        static FieldInfo socketField;
        static MethodInfo sendMethod;
        static object resultType;
        static bool ready;
        internal static void Initialize(string directory, Action<string> log)
        {
            store=new AdminAccessStore(Path.Combine(directory,"admin-access.json"));
            try
            {
                Type type=typeof(WebSocketCommandHandler);
                socketField=AccessTools.Field(type,"connection");
                sendMethod=type.GetMethods(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).First(m=>m.Name=="Send" && m.GetParameters().Length==4);
                resultType=Enum.ToObject(sendMethod.GetParameters()[0].ParameterType,2);
                harmony=new HarmonyLib.Harmony("prefabeditor.admin-access");
                var opened=new HarmonyMethod(typeof(AdminAccess),"Opened"); opened.priority=Priority.Last;
                harmony.Patch(AccessTools.Method(type,"OnOpen"),postfix:opened);
                harmony.Patch(AccessTools.Method(type,"OnClose"),prefix:new HarmonyMethod(typeof(AdminAccess),"Closed"));
                harmony.Patch(AccessTools.Method(type,"HandleRequest"),prefix:new HarmonyMethod(typeof(AdminAccess),"OwnerRequest"));
                ready=true;
            }
            catch { ready=false; if(harmony!=null) harmony.UnpatchSelf(); log("Personal admin access unavailable on this game build. Owner access is unchanged."); }
        }
        static void Opened(WebSocketCommandHandler __instance)
        {
            var socket=(IWebSocketConnection)socketField.GetValue(__instance);
            Action<string> native=socket.OnMessage;
            socket.OnMessage=delegate(string message)
            {
                if(message!=null && message.StartsWith("pfa1.",StringComparison.Ordinal))
                {
                    var record=ready?store.Authenticate(message):null;
                    if(record==null) { SystemMessage(socket,"Access denied: personal token is invalid or revoked."); socket.Close(); return; }
                    var session=new Session { Handler=__instance, Socket=socket, Id=record.Id, Digest=record.Digest,
                        Context=new CommandContext(new UserInfoWithPermissions(0,record.Name,true,new[]{"server_owner"}),
                            (GroupPermissions[])Enum.GetValues(typeof(GroupPermissions))) };
                    lock(gate) sessions[__instance]=session;
                    socket.OnMessage=delegate(string wire){ Receive(session,wire); };
                    // Recheck after installing the callback to close the admission/revocation race.
                    if(store.Current(session.Id,session.Digest)==null) { Disconnect(session); return; }
                    SystemMessage(socket,"Connection Succeeded (personal Prefabulator access)");
                    return;
                }
                if(native!=null) native(message);
            };
        }
        static void Closed(WebSocketCommandHandler __instance) { lock(gate) sessions.Remove(__instance); }
        static bool OwnerRequest(WebSocketCommandHandler __instance, string __0)
        {
            Session session; lock(gate) sessions.TryGetValue(__instance,out session);
            if(session!=null) { Receive(session,__0); return false; }
            try
            {
                if(__0==null || __0.Length>16384) return true;
                var request=JObject.Parse(__0); string command=(string)request["content"];
                if(command==null || !command.StartsWith("edit access ",StringComparison.Ordinal)) return true;
                uint id=(uint)request["id"];
                if(!__instance.IsAuthorized) { Reply(__instance,id,null,"Owner authentication required"); return false; }
                if(Interlocked.Increment(ref pending)>128) { Interlocked.Decrement(ref pending); Reply(__instance,id,null,"Server busy; retry later"); return false; }
                MainThreadDispatcher.Run(delegate
                {
                    try
                    {
                        if(!__instance.IsAuthorized) throw new Exception("Owner session disconnected");
                        var action=Decode(command);
                        string verb=(string)action["action"];
                        JObject result=verb=="self"?Self(null):verb=="list"?store.List():store.Change(action);
                        if(verb=="revoke" || verb=="rotate") CloseInvalidSessions();
                        Reply(__instance,id,result,null);
                    }
                    catch(Exception e){ Reply(__instance,id,null,e.Message); }
                    finally { Interlocked.Decrement(ref pending); }
                });
                return false;
            }
            catch { return true; }
        }
        static JObject Decode(string command)
        {
            if(command.Length>12000) throw new Exception("Access request too large");
            return JObject.Parse(new UTF8Encoding(false,true).GetString(Convert.FromBase64String(command.Substring("edit access ".Length))));
        }
        static JObject Self(AdminAccessRecord record)
        {
            return new JObject(new JProperty("ok",true),new JProperty("owner",record==null),new JProperty("name",record==null?"Owner":record.Name),
                new JProperty("tabs",new JArray(record==null?AdminAccessStore.TabNames:record.Tabs)));
        }
        static void Receive(Session session,string wire)
        {
            uint id=0;
            try
            {
                if(wire==null || wire.Length>520000) throw new Exception("Request too large");
                var request=JObject.Parse(wire); id=(uint)request["id"];
                string command=(string)request["content"];
                if(string.IsNullOrEmpty(command)) throw new Exception("Missing command");
                if(Interlocked.Increment(ref session.Pending)>8) { Interlocked.Decrement(ref session.Pending); throw new Exception("Too many pending requests"); }
                if(Interlocked.Increment(ref pending)>128) { Interlocked.Decrement(ref pending); Interlocked.Decrement(ref session.Pending); throw new Exception("Server busy"); }
                uint requestId=id;
                try { MainThreadDispatcher.Run(delegate { Execute(session,requestId,command); }); }
                catch { Interlocked.Decrement(ref pending); Interlocked.Decrement(ref session.Pending); throw; }
            }
            catch(Exception e){ Reply(session.Handler,id,null,e.Message); }
        }
        static async void Execute(Session session,uint id,string command)
        {
            try
            {
                var record=store.Current(session.Id,session.Digest);
                if(record==null) { Disconnect(session); return; }
                if(command.StartsWith("edit access ",StringComparison.Ordinal))
                {
                    if((string)Decode(command)["action"]!="self") throw new Exception("Only the owner can manage admin access");
                    Reply(session.Handler,id,Self(record),null); return;
                }
                if(!AdminAccessStore.Allowed(command,record.Tabs)) throw new Exception("Your admin profile does not allow this action");
                // Use the native parser only after the server's explicit command allowlist.
                var result=await CommandService.Handle(command,session.Context);
                sendMethod.Invoke(session.Handler,new object[]{resultType,result,null,(uint?)id});
            }
            catch(Exception e){ Reply(session.Handler,id,null,e.Message); }
            finally { Interlocked.Decrement(ref pending); Interlocked.Decrement(ref session.Pending); }
        }
        static void Reply(WebSocketCommandHandler handler,uint id,JObject result,string error)
        {
            try
            {
                var socket=(IWebSocketConnection)socketField.GetValue(handler);
                socket.Send(JsonConvert.SerializeObject(new { type="CommandResult",commandId=id,
                    data=new { ResultString=result==null?"":result.ToString(Formatting.None),Exception=error } }));
            }
            catch { }
        }
        static void SystemMessage(IWebSocketConnection socket,string message)
        { try { socket.Send(JsonConvert.SerializeObject(new {type="SystemMessage",data=message})); } catch { } }
        static void Disconnect(Session session)
        {
            lock(gate) sessions.Remove(session.Handler);
            SystemMessage(session.Socket,"Prefabulator access revoked. Ask the server owner for access.");
            try { session.Socket.Close(); } catch { }
        }
        static void CloseInvalidSessions()
        {
            Session[] current;lock(gate) current=sessions.Values.ToArray();
            foreach(var session in current) if(store.Current(session.Id,session.Digest)==null) Disconnect(session);
        }
        internal static void Shutdown()
        {
            ready=false;
            Session[] current;lock(gate) current=sessions.Values.ToArray();
            foreach(var session in current) Disconnect(session);
            if(harmony!=null) harmony.UnpatchSelf();
        }
    }
}
