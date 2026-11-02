"""Exercise actual coordinate input bridge with simulated keyboard/GUI events."""
from pathlib import Path
import tempfile, subprocess
source=Path(__file__).resolve().parents[1]/'client/player-kit/mod/src/CoordinateTextField.cs'
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
 public enum EventType {KeyDown,KeyUp,Repaint,Used}
 public class Event {public static Event current=new Event(); public EventType type; public void Use(){type=EventType.Used;}}
 public static class GUI {public static string focus="TeleporterDestination";public static string GetNameOfFocusedControl(){return focus;} public static void SetNextControlName(string n){} }
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
  Console.WriteLine("PASS: manual coordinate replacement, single input delivery, delete, paste, focus and shutdown.");
 }
}
'''
with tempfile.TemporaryDirectory(prefix='coordinate-input-test-') as folder:
 p=Path(folder);(p/'Test.cs').write_text(harness);exe=p/'Test.exe'
 subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/out:'+str(exe),str(source),str(p/'Test.cs')],check=True)
 subprocess.run([str(exe)],check=True)
