"""Exercise actual coordinate input bridge with simulated keyboard/GUI events."""
from pathlib import Path
import tempfile, subprocess, sys
source=Path(sys.argv[1]) if len(sys.argv)>1 else Path(__file__).resolve().parents[1]/'client/player-kit/mod/src/CoordinateTextField.cs'
harness=r'''
using System;
using PrefabEditorMod;
namespace UnityEngine.InputSystem {
 public class Button {public bool isPressed,wasPressedThisFrame;}
 public class Keyboard {
  public static Keyboard current=new Keyboard(); public event Action<char> onTextInput;
  public void Type(char c){if(onTextInput!=null)onTextInput(c);}
  public Button ctrlKey=new Button(),altKey=new Button(),shiftKey=new Button(),aKey=new Button(),vKey=new Button(),cKey=new Button(),xKey=new Button(),backspaceKey=new Button(),deleteKey=new Button(),leftArrowKey=new Button(),rightArrowKey=new Button(),homeKey=new Button(),endKey=new Button();
 }
}
namespace UnityEngine {
 public enum EventType {KeyDown,KeyUp,Layout,Repaint,Used}
 public enum KeyCode {None,A,V,C,X,Backspace,Delete,LeftArrow,RightArrow,Home,End}
 public static class Time {public static int frameCount;}
 public class Event {public static Event current=new Event(); public EventType type; public char character; public KeyCode keyCode; public bool control,command,alt,shift; public void Use(){type=EventType.Used;}}
 public static class GUI {public static bool enabled=true;public static string focus="TeleporterDestination";public static string GetNameOfFocusedControl(){return focus;} public static void SetNextControlName(string n){} }
 public static class GUILayout {public static string TextField(string s){return s;} }
 public static class GUIUtility {public static int keyboardControl;public static string systemCopyBuffer; public static TextEditor editor=new TextEditor();public static object GetStateObject(Type t,int id){return editor;} }
 public class TextEditor {
  public string text=""; bool all;
  public void Insert(char c){ReplaceSelection(c.ToString());} public void ReplaceSelection(string s){text=all?s:text+s;all=false;} public void SelectAll(){all=true;}
  public void Backspace(){if(all){text="";all=false;}else if(text.Length>0)text=text.Substring(0,text.Length-1);} public void Delete(){Backspace();}
  public void Copy(){GUIUtility.systemCopyBuffer=text;} public void Cut(){Copy();SelectAll();Backspace();}
  public void MoveLeft(){} public void MoveRight(){} public void SelectLeft(){} public void SelectRight(){} public void MoveTextStart(){} public void MoveTextEnd(){}
 }
}
class Test {
 static void Check(bool b,string m){if(!b)throw new Exception(m);}
 static void Frame(){UnityEngine.Time.frameCount++;UnityEngine.Event.current=new UnityEngine.Event{type=UnityEngine.EventType.Repaint};}
 static string Native(CoordinateTextField field,string value,char character=default(char),UnityEngine.KeyCode code=UnityEngine.KeyCode.None,bool control=false,bool command=false){
  UnityEngine.Event.current=new UnityEngine.Event{type=UnityEngine.EventType.KeyDown,character=character,keyCode=code,control=control,command=command};
  value=field.Draw(value);Check(UnityEngine.Event.current.type==UnityEngine.EventType.Used,"focused native key is consumed after handling");
  UnityEngine.Event.current=new UnityEngine.Event{type=UnityEngine.EventType.Repaint};return value;
 }
 static string Passes(CoordinateTextField field,string value){
  UnityEngine.Event.current.type=UnityEngine.EventType.Layout;value=field.Draw(value);
  UnityEngine.Event.current.type=UnityEngine.EventType.Repaint;return field.Draw(value);
 }
 static void Main(){
  var field=new CoordinateTextField();var kb=UnityEngine.InputSystem.Keyboard.current;
  field.Tick(true);string value=field.Draw("-596.447,275.657,240.869");
  kb.ctrlKey.isPressed=true;kb.aKey.wasPressedThisFrame=true;field.Tick(true);value=field.Draw(value);kb.ctrlKey.isPressed=false;kb.aKey.wasPressedThisFrame=false;
  foreach(char c in "10.5,-20,30")kb.Type(c); value=field.Draw(value);Check(value=="10.5,-20,30","manual replacement");
  UnityEngine.Event.current.type=UnityEngine.EventType.KeyDown;value=field.Draw(value);Check(value=="10.5,-20,30" && UnityEngine.Event.current.type==UnityEngine.EventType.Used,"no native duplicate");
  kb.backspaceKey.wasPressedThisFrame=true;field.Tick(true);value=field.Draw(value);kb.backspaceKey.wasPressedThisFrame=false;Check(value=="10.5,-20,3","backspace");
  kb.ctrlKey.isPressed=true;kb.aKey.wasPressedThisFrame=true;kb.vKey.wasPressedThisFrame=true;UnityEngine.GUIUtility.systemCopyBuffer=" 1,2,3\r\n";field.Tick(true);value=field.Draw(value);Check(value=="1,2,3","select all and clipboard paste");kb.ctrlKey.isPressed=false;kb.aKey.wasPressedThisFrame=false;kb.vKey.wasPressedThisFrame=false;
  UnityEngine.GUI.focus="";field.Draw(value);kb.Type('9');UnityEngine.GUI.focus="TeleporterDestination";Check(field.Draw(value)==value,"unfocused input ignored");
  kb.Type('8');field.Tick(false);Check(field.Draw(value)==value,"closing discards pending input");kb.Type('7');Check(field.Draw(value)==value,"closing unsubscribes keyboard");
  var search=new CoordinateTextField("TradeItemSearch",false);var priceField=new CoordinateTextField("TradePrice");
  search.Tick(true);priceField.Tick(true);UnityEngine.GUI.focus="TradeItemSearch";search.Draw("");priceField.Draw("1");
  foreach(char c in "Iron Ingot")kb.Type(c);Check(search.Draw("")=="Iron Ingot","trade search accepts item names");Check(priceField.Draw("1")=="1","other field cannot steal input");
  search.Stop();priceField.Stop();
  // Broadcast accepts ordinary text when IMGUI is the only character source.
  var broadcast=new CoordinateTextField("AdminBroadcast",false);Frame();broadcast.Tick(true);UnityEngine.GUI.focus="AdminBroadcast";value=broadcast.Draw("");
  foreach(char c in "Hello, town! 500 gold.")value=Native(broadcast,value,c);
  Check(value=="Hello, town! 500 gold.","broadcast legacy-only typing with spaces/punctuation");
  // Repeat letters are distinct inputs; delivery through both streams is not.
  Frame();value=broadcast.Draw("");foreach(char c in "Welcome all!")kb.Type(c);value=broadcast.Draw(value);
  Check(value=="Welcome all!","broadcast InputSystem-only text");
  Frame();value=broadcast.Draw("");kb.Type('l');kb.Type('l');value=Passes(broadcast,value);
  value=Native(broadcast,value,'l');value=Native(broadcast,value,'l');
  Check(value=="ll","dual stream after primary Draw does not duplicate repeated letters");
  Frame();value=broadcast.Draw("");value=Native(broadcast,value,'o');value=Native(broadcast,value,'o');value=Passes(broadcast,value);
  kb.Type('o');kb.Type('o');value=broadcast.Draw(value);
  Check(value=="oo","dual stream reverse order does not duplicate repeated letters");
  Frame();kb.Type('o');value=broadcast.Draw(value);value=Native(broadcast,value,'o');
  Check(value=="ooo","same character in a later frame remains a new input");
  // Keyboard shortcuts can also arrive through both event paths.
  Frame();UnityEngine.GUIUtility.systemCopyBuffer="Announcement";kb.ctrlKey.isPressed=true;kb.aKey.wasPressedThisFrame=true;kb.vKey.wasPressedThisFrame=true;
  broadcast.Tick(true);value=broadcast.Draw(value);kb.ctrlKey.isPressed=false;kb.aKey.wasPressedThisFrame=false;kb.vKey.wasPressedThisFrame=false;
  value=Native(broadcast,value,default(char),UnityEngine.KeyCode.A,true);value=Native(broadcast,value,default(char),UnityEngine.KeyCode.V,true);
  Check(value=="Announcement","dual select-all/paste is applied once");
  Frame();value=Native(broadcast,value,default(char),UnityEngine.KeyCode.Backspace);kb.backspaceKey.wasPressedThisFrame=true;broadcast.Tick(true);value=broadcast.Draw(value);kb.backspaceKey.wasPressedThisFrame=false;
  Check(value=="Announcemen","reverse-order backspace is applied once");
  Frame();value=Native(broadcast,value,default(char),UnityEngine.KeyCode.A,false,true);UnityEngine.GUIUtility.systemCopyBuffer="From clipboard\n";
  value=Native(broadcast,value,default(char),UnityEngine.KeyCode.V,false,true);Check(value=="From clipboard","legacy command shortcuts work");
  // Focus, disabled controls, and shutdown must not leak broadcast input.
  Frame();UnityEngine.GUI.focus="AnotherField";broadcast.Draw(value);kb.Type('x');UnityEngine.GUI.focus="AdminBroadcast";Check(broadcast.Draw(value)==value,"broadcast ignores another field's characters");
  Frame();UnityEngine.GUI.enabled=false;broadcast.Draw(value);kb.Type('x');UnityEngine.GUI.enabled=true;Check(broadcast.Draw(value)==value,"disabled field does not receive text");
  Frame();kb.Type('x');broadcast.Tick(false);Check(broadcast.Draw(value)==value,"inactive broadcast discards pending characters");
  kb.Type('y');Check(broadcast.Draw(value)==value,"inactive broadcast unsubscribes");broadcast.Stop();
  Console.WriteLine("PASS: coordinate editing, broadcast legacy/new/dual input, repeat letters, shortcuts, focus, disabled controls and shutdown.");
 }
}
'''
with tempfile.TemporaryDirectory(prefix='coordinate-input-test-') as folder:
 p=Path(folder);(p/'Test.cs').write_text(harness);exe=p/'Test.exe'
 subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/out:'+str(exe),str(source),str(p/'Test.cs')],check=True)
 subprocess.run([str(exe)],check=True)
