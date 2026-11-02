"""Run actual trade command against deterministic native-game stubs."""
from pathlib import Path
import tempfile,subprocess
source=Path(__file__).resolve().parents[1]/'server/server-kit/core/Modules/TradeDeck.cs'
harness=r'''
using System;
using Alta.Networking;
using Alta.Inventory;
using Alta.Trading;
using PrefabEditor.Modules;
namespace Newtonsoft.Json {public static class JsonConvert {public static string SerializeObject(object o){return "{}";}}}
namespace UnityEngine {
 public struct Vector3 {public float x,y,z;public static Vector3 up{get{return new Vector3{y=1};}}public static Vector3 operator +(Vector3 a,Vector3 b){return a;}}
 public struct Quaternion {public static Quaternion identity;}
 public class Transform {public Vector3 position;}
}
namespace Alta.Console {public class CommandAttribute:Attribute {public CommandAttribute(string a,string b){}}}
namespace Alta.Networking {
 public class NetworkPrefab {public uint Hash=123;public string name="Iron";public bool CanBeSpawnedThroughCommand=true;}
 public class NetworkEntity {public bool IsBeingDestroyed;public NetworkPrefab Prefab=new NetworkPrefab();public Pickup CommonPickup;public UnityEngine.Transform transform=new UnityEngine.Transform();public TradeVendor Vendor;public void SceneDestroy(){IsBeingDestroyed=true;}public T[] GetComponentsInChildren<T>(bool active){return Vendor==null?new T[0]:new T[]{(T)(object)Vendor};}}
}
namespace Alta.Inventory {
 public class Item {public bool IsStackable=true;}
 public delegate void DockFilter(Interactor i,Pickup p,ref bool allowed);
 public class PickupDock {
  public event DockFilter DockFilter;public Pickup DockedPickup;public int QuantityStored;public bool HasDockedPickup{get{return DockedPickup!=null;}}public int Capacity=10000;public bool Reject;
  public bool CanDockAmount(int added){return QuantityStored+added<=Capacity;}
  public void DestroyAmountToEmpty(){DockedPickup.Entity.SceneDestroy();DockedPickup=null;QuantityStored=0;}
  public int FilterCount{get{return DockFilter==null?0:DockFilter.GetInvocationList().Length;}}
 }
}
public class Interactor {}
public class Pickup {
 public static bool ReplaceOnDock;
 public NetworkEntity Entity;public Item Item=new Item();
 public int DockInto(Interactor i,PickupDock d,int n,bool instant){if(d.Reject)return 4;d.DockedPickup=this;if(ReplaceOnDock){Entity.SceneDestroy();var image=new NetworkEntity();d.DockedPickup=new Pickup{Entity=image};image.CommonPickup=d.DockedPickup;}d.QuantityStored=n;return 0;}
}
public class ButtonCounter {public int Amount=1,Minimum=1,Maximum=1000000;public object ActivePlayer;public bool Reject;public void ForceAmount(int n){if(!Reject)Amount=n;}}
namespace Alta.Trading {
 public class TradeVendor {
  public PickupDock stockDock=new PickupDock();public ButtonCounter coinCounter=new ButtonCounter(),itemCounter=new ButtonCounter();public int Owner=1;public string TradeState="OpenForSale";public bool isProcessingSale,isEjecting;
  public TradeVendor(){stockDock.DockFilter+=FilterWrongOwnerDock;}
  private void FilterWrongOwnerDock(Interactor i,Pickup p,ref bool a){a=false;}
 }
}
public static class CommonPrefabs {public static Item PouchItem=new Item();}
public class SpawnData {public static SpawnData Default;}
public static class PrefabManager {public static NetworkPrefab Prefab=new NetworkPrefab();public static void PrepareSpawnSetups(){}public static NetworkPrefab GetPrefab(uint hash){return Prefab!=null&&hash==Prefab.Hash?Prefab:null;}}
public static class SpawnHelper {
 public static int Calls;public static NetworkEntity Last;public static bool Stackable=true;
 public static NetworkEntity Spawn(NetworkPrefab p,SpawnData d,object owner,UnityEngine.Vector3 pos,UnityEngine.Quaternion q){Calls++;Last=new NetworkEntity{Prefab=p};Last.CommonPickup=new Pickup{Entity=Last,Item=new Item{IsStackable=Stackable}};return Last;}
}
namespace PrefabEditor.Modules {
 public static class EditJson {public static string Err(string s){return "ERROR:"+s;}}
 public static partial class EditorAdminModule {
  public static NetworkEntity TestRoot;
  static string ResolveRoot(uint id,out NetworkEntity root){root=TestRoot;return root==null?"missing entity":null;}
  static string EntityRecord(NetworkEntity root,UnityEngine.Vector3 p){return "{\"id\":42}";}
 }
}
class Test {
 static void Check(bool v,string m){if(!v)throw new Exception(m);}
 static TradeVendor Reset(){var v=new TradeVendor();EditorAdminModule.TestRoot=new NetworkEntity{Vendor=v};SpawnHelper.Calls=0;SpawnHelper.Stackable=true;Pickup.ReplaceOnDock=false;PrefabManager.Prefab=new NetworkPrefab();return v;}
 static string Set(uint hash=123,int price=50,int quantity=5,int stock=100){return EditorAdminModule.TradeSet(42,hash,price,quantity,stock);}
 static void Main(){
  var v=Reset();Check(Set().StartsWith("{\"ok\":true"),"valid request succeeds");Check(v.stockDock.QuantityStored==100 && v.coinCounter.Amount==50 && v.itemCounter.Amount==5,"all requested values applied");Check(v.stockDock.FilterCount==1,"ownership filter restored");
  int calls=SpawnHelper.Calls;Check(Set(123,60,2,20).StartsWith("{"),"edit existing item");Check(SpawnHelper.Calls==calls && v.stockDock.QuantityStored==20,"same item does not spawn another");
  Check(Set(456).Contains("remove the current stock"),"different item requires empty stock");Check(v.stockDock.QuantityStored==20 && v.coinCounter.Amount==60,"different item rejection preserves old stock and price");
  Check(Set(123,0).StartsWith("ERROR"),"invalid price rejected");Check(v.coinCounter.Amount==60,"invalid price unchanged");
  v=Reset();v.isProcessingSale=true;Check(Set().StartsWith("ERROR") && SpawnHelper.Calls==0,"busy deck cannot mutate");
  v=Reset();v.stockDock.Reject=true;Check(Set().StartsWith("ERROR") && SpawnHelper.Last.IsBeingDestroyed,"dock rejection cleans generated item");Check(v.stockDock.FilterCount==1,"filter restored after rejection");
  v=Reset();v.stockDock.Capacity=2;Check(Set().StartsWith("ERROR") && v.stockDock.QuantityStored==0 && SpawnHelper.Last.IsBeingDestroyed,"capacity failure cleans stock");
  v=Reset();Pickup.ReplaceOnDock=true;v.stockDock.Capacity=2;Check(Set().StartsWith("ERROR") && !v.stockDock.HasDockedPickup,"rollback handles native replacement images");
  v=Reset();v.coinCounter.Reject=true;Check(Set().StartsWith("ERROR") && v.stockDock.QuantityStored==0 && v.itemCounter.Amount==1,"counter rejection rolls back new stock");
  v=Reset();Check(Set().StartsWith("{"),"seed stock");v.coinCounter.Reject=true;Check(Set(123,60,2,20).StartsWith("ERROR") && v.stockDock.QuantityStored==100 && v.itemCounter.Amount==5,"counter rejection restores existing stock");
  v=Reset();SpawnHelper.Stackable=false;Check(Set().StartsWith("ERROR") && SpawnHelper.Last.IsBeingDestroyed,"nonstackable quantity rejected");Check(Set(123,50,1,1).StartsWith("{"),"single nonstackable accepted");
  v=Reset();Check(Set(123,50,1,1).StartsWith("{"),"default one purchase and stock");
  Check(EditorAdminModule.TradeAddStock(42,123,9).StartsWith("{") && v.stockDock.QuantityStored==10 && v.coinCounter.Amount==50 && v.itemCounter.Amount==1,"add stock keeps sale settings");
  v.stockDock.QuantityStored=3;Check(EditorAdminModule.TradeAddStock(42,123,2).StartsWith("{") && v.stockDock.QuantityStored==5,"addition uses latest stock after sales");
  Check(EditorAdminModule.TradeAddStock(42,456,2).StartsWith("ERROR") && v.stockDock.QuantityStored==5,"stale item rejected");
  Check(EditorAdminModule.TradeAddStock(42,123,0).StartsWith("ERROR"),"zero addition rejected");
  v.stockDock.QuantityStored=int.MaxValue;Check(EditorAdminModule.TradeAddStock(42,123,1).StartsWith("ERROR") && v.stockDock.QuantityStored==int.MaxValue,"overflow rejected without mutation");
  v=Reset();Check(EditorAdminModule.TradeAddStock(42,123,1).StartsWith("ERROR") && SpawnHelper.Calls==0,"unconfigured deck requires initial setup");
  v=Reset();PrefabManager.Prefab=null;Check(Set().StartsWith("ERROR") && SpawnHelper.Calls==0,"unknown item rejected");
  v=Reset();EditorAdminModule.TestRoot.Vendor=null;Check(Set().StartsWith("ERROR") && SpawnHelper.Calls==0,"wrong object rejected");
  Console.WriteLine("PASS: native trade command settings, limits, selection, busy state, item preservation, rollback and filter restoration.");
 }
}
'''
with tempfile.TemporaryDirectory(prefix='trade-command-test-') as folder:
 p=Path(folder);(p/'Test.cs').write_text(harness);exe=p/'Test.exe'
 subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/out:'+str(exe),str(source),str(p/'Test.cs')],check=True)
 subprocess.run([str(exe)],check=True)
