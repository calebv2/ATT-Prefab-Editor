"""Exercise actual Trading form enable/disable decisions without a running game."""
from pathlib import Path
import tempfile,subprocess
source=(Path(__file__).resolve().parents[1]/'client/player-kit/mod/src/EditSelection.cs').read_text(encoding='utf-8-sig')
a=source.index('        void DrawTradeDeckControls()');b=source.index('        void AddTradeStock()',a)
method=source[a:b]
harness=r'''
using System;
using System.Collections.Generic;
using System.Globalization;
static class GUI {public static bool enabled=true;public static void FocusControl(string n){} }
static class GUILayout {
 public static Dictionary<string,bool> Buttons=new Dictionary<string,bool>();public static List<string> Labels=new List<string>();
 public static void Space(float n){}public static void Label(string t,object style){Labels.Add(t);}public static bool Button(string t){Buttons[t]=GUI.enabled;return false;}
}
class Input {public bool Enabled;public string Draw(string s){Enabled=GUI.enabled;return s;}public void Stop(){} }
class SpawnCatalogEntry {public string Name;public uint Hash;}
class Api {public void Prefabs(Action<object> callback){} }
class Test {
 bool selected=true;uint _id=42,_tradeId=42,_tradeHash=123;bool _tradeBusy,_tradeLoaded,_spawnCatalogAttempted=true,_spawnBusy,_spawnCatalogLoading;
 string _tradeStatus="Panel error",_tradeQuery="123",_tradeItemName="Iron",_tradePrice="1",_tradeQuantity="1",_tradeStock="1",_tradeAddStock="1";
 object _titleStyle=new object(),_mutedStyle=new object();List<int> _group=new List<int>{1};List<SpawnCatalogEntry> _spawnCatalog=new List<SpawnCatalogEntry>();
 Input _tradeSearchInput=new Input(),_tradePriceInput=new Input(),_tradeQuantityInput=new Input(),_tradeStockInput=new Input(),_tradeAddStockInput=new Input();Api _api=new Api();
 bool IsTradeDeckSelected(){return selected;}void ResetTradeForm(){}void LoadTradeDeck(){}void OnPrefabs(object o){}void ApplyTradeDeck(){}void AddTradeStock(){}
 METHOD
 static void Check(bool v,string s){if(!v)throw new Exception(s);}
 static void Main(){
  var t=new Test();t.DrawTradeDeckControls();
  Check(t._tradeSearchInput.Enabled&&t._tradePriceInput.Enabled&&t._tradeQuantityInput.Enabled&&t._tradeStockInput.Enabled,"failed read must not lock new offer entry");
  Check(GUILayout.Buttons["Apply item, price and stock to selected deck"],"valid new offer can be submitted after failed read");
  Check(!GUILayout.Buttons["Add this many to current stock"],"adding stock waits for current server settings");
  Check(GUILayout.Labels.IndexOf("Panel error")<GUILayout.Labels.IndexOf("Item to sell - search by name or enter a prefab ID:"),"status before fields");
  t._tradeHash=0;t.DrawTradeDeckControls();Check(!GUILayout.Buttons["Apply item, price and stock to selected deck"],"choose an item first");
  t._tradeHash=123;t._tradeBusy=true;t.DrawTradeDeckControls();Check(!t._tradeSearchInput.Enabled&&!GUILayout.Buttons["Apply item, price and stock to selected deck"],"pending writes cannot submit twice");
  t._tradeBusy=false;t._tradeLoaded=true;t.DrawTradeDeckControls();Check(GUILayout.Buttons["Add this many to current stock"],"loaded deck can add stock");
  t.selected=false;GUILayout.Buttons.Clear();t.DrawTradeDeckControls();Check(GUILayout.Buttons.Count==0,"wrong selection has no actionable form");
  Console.WriteLine("PASS: Trading form recovery, visible status, valid inputs, pending requests and selection guards.");
 }
}
'''.replace('METHOD',method)
with tempfile.TemporaryDirectory(prefix='trading-ui-test-') as folder:
 p=Path(folder);(p/'Test.cs').write_text(harness);exe=p/'Test.exe'
 subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/warn:0','/out:'+str(exe),str(p/'Test.cs')],check=True)
 subprocess.run([str(exe)],check=True)
