"""Exercise production gizmo input with deterministic camera/math and no-op rendering."""
from pathlib import Path
import subprocess,tempfile
source=Path(__file__).resolve().parents[1]/'client/player-kit/mod/src/EditGizmo.cs'
harness=r'''
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using PrefabEditorMod;
namespace PrefabEditorMod {public class EditSelection {public void DrawGizmo(Camera c){}}}
namespace UnityEngine.Rendering {public enum BlendMode{SrcAlpha,OneMinusSrcAlpha}public enum CullMode{Off}public enum CompareFunction{Always}}
namespace UnityEngine.InputSystem {
 public enum Key{Escape,LeftCtrl,RightCtrl,LeftShift,RightShift,LeftAlt,RightAlt}
 public class Button{public bool isPressed,wasPressedThisFrame;}
 public class Mouse{public static Mouse current=new Mouse();public Button leftButton=new Button();}
 public class Keyboard{public static Keyboard current;public Button this[Key k]{get{return new Button();}}}
}
namespace UnityEngine {
 public struct Vector2 {public float x,y;public Vector2(float a,float b){x=a;y=b;}public static Vector2 zero{get{return new Vector2();}}public float sqrMagnitude{get{return x*x+y*y;}}public static Vector2 operator +(Vector2 a,Vector2 b){return new Vector2(a.x+b.x,a.y+b.y);}public static Vector2 operator -(Vector2 a,Vector2 b){return new Vector2(a.x-b.x,a.y-b.y);}public static Vector2 operator *(Vector2 a,float b){return new Vector2(a.x*b,a.y*b);}public static float Distance(Vector2 a,Vector2 b){return (float)Math.Sqrt((a-b).sqrMagnitude);}public static float Dot(Vector2 a,Vector2 b){return a.x*b.x+a.y*b.y;}}
 public struct Vector3 {
  public float x,y,z;public Vector3(float a,float b,float c){x=a;y=b;z=c;}public float sqrMagnitude{get{return Dot(this,this);}}public float magnitude{get{return (float)Math.Sqrt(sqrMagnitude);}}public Vector3 normalized{get{return this*(1f/magnitude);}}
  public static Vector3 right{get{return new Vector3(1,0,0);}}public static Vector3 up{get{return new Vector3(0,1,0);}}public static Vector3 forward{get{return new Vector3(0,0,1);}}
  public static Vector3 operator +(Vector3 a,Vector3 b){return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);}public static Vector3 operator -(Vector3 a,Vector3 b){return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);}public static Vector3 operator *(Vector3 a,float b){return new Vector3(a.x*b,a.y*b,a.z*b);}public static Vector3 operator /(Vector3 a,float b){return a*(1f/b);}
  public static float Dot(Vector3 a,Vector3 b){return a.x*b.x+a.y*b.y+a.z*b.z;}public static Vector3 Cross(Vector3 a,Vector3 b){return new Vector3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);}public static float Distance(Vector3 a,Vector3 b){return (a-b).magnitude;}public static Vector3 ProjectOnPlane(Vector3 a,Vector3 n){return a-n*(Dot(a,n)/n.sqrMagnitude);}public static Vector3 ClampMagnitude(Vector3 a,float m){return a.magnitude>m?a.normalized*m:a;}
 }
 public struct Quaternion {public Vector3 eulerAngles{get{return new Vector3();}}public static Quaternion identity{get{return new Quaternion();}}public static Quaternion AngleAxis(float a,Vector3 v){return identity;}public static Quaternion operator *(Quaternion a,Quaternion b){return identity;}public static Vector3 operator *(Quaternion a,Vector3 b){return b;}}
 public struct Ray {public Vector3 origin,direction;public Ray(Vector3 o,Vector3 d){origin=o;direction=d.normalized;}public Vector3 GetPoint(float n){return origin+direction*n;}}
 public struct Plane {public Vector3 normal;float offset;public Plane(Vector3 n,Vector3 p){normal=n.normalized;offset=-Vector3.Dot(normal,p);}public bool Raycast(Ray r,out float t){float d=Vector3.Dot(normal,r.direction);t=Math.Abs(d)<1e-6?0:-(Vector3.Dot(normal,r.origin)+offset)/d;return Math.Abs(d)>1e-6 && t>=0;}}
 public class Transform{public Vector3 position,forward=Vector3.forward,right=Vector3.right,up=Vector3.up;public Quaternion rotation;}
 public class Camera{public Transform transform=new Transform();public int pixelHeight=1000;public float fieldOfView=90;public object projectionMatrix,worldToCameraMatrix;public Vector3 WorldToScreenPoint(Vector3 p){var d=p-transform.position;float z=Vector3.Dot(d,transform.forward);float scale=500f/z;return new Vector3(500+Vector3.Dot(d,transform.right)*scale,500+Vector3.Dot(d,transform.up)*scale,z);}public Ray ScreenPointToRay(Vector3 p){return new Ray(transform.position,transform.forward+transform.right*((p.x-500)/500f)+transform.up*((p.y-500)/500f));}}
 public static class Mathf{public const float PI=(float)Math.PI,Deg2Rad=PI/180,Rad2Deg=180/PI;public static float Abs(float a){return Math.Abs(a);}public static float Max(float a,float b){return Math.Max(a,b);}public static float Clamp(float a,float lo,float hi){return Math.Max(lo,Math.Min(hi,a));}public static float Clamp01(float a){return Clamp(a,0,1);}public static float Round(float a){return (float)Math.Round(a);}public static float Tan(float a){return (float)Math.Tan(a);}public static float Sin(float a){return (float)Math.Sin(a);}public static float Cos(float a){return (float)Math.Cos(a);}public static float Atan2(float a,float b){return (float)Math.Atan2(a,b);}public static float DeltaAngle(float a,float b){return b-a;}}
 public struct Color{public float a;public Color(float r,float g,float b,float alpha){a=alpha;}public static Color white{get{return new Color(1,1,1,1);}}public static Color Lerp(Color a,Color b,float f){return b;}}
 public struct Bounds{public Vector3 min,max;}
 public class MonoBehaviour{public T GetComponent<T>() where T:new(){return new T();}}
 public class Shader{public static Shader Find(string s){return new Shader();}}
 public enum HideFlags{HideAndDontSave}
 public class Material{public HideFlags hideFlags;public Material(Shader s){}public void SetInt(string n,int i){}public void SetPass(int i){}}
 public static class GL{public const int LINES=1,QUADS=2,TRIANGLES=3;public static object modelview;public static void PushMatrix(){}public static void PopMatrix(){}public static void LoadProjectionMatrix(object m){}public static void Begin(int i){}public static void End(){}public static void Color(Color c){}public static void Vertex(Vector3 v){}public static void Vertex3(float a,float b,float c){}}
}
class Test {
 static void Check(bool ok,string text){if(!ok)throw new Exception(text);}
 static Camera Cam(){var c=new Camera();c.transform.position=new Vector3(-6,-6,-6);c.transform.forward=new Vector3(1,1,1).normalized;c.transform.right=Vector3.Cross(Vector3.up,c.transform.forward).normalized;c.transform.up=Vector3.Cross(c.transform.forward,c.transform.right).normalized;return c;}
 static Vector2 Handle(Camera c,int h){float ptw=2*Vector3.Distance(c.transform.position,new Vector3())/1000f;object[] args={c,new Vector3(),ptw,h,new Vector3(),new Vector3(),new Vector3(),new Vector3()};typeof(EditGizmo).GetMethod("SquareCorners",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args);Vector3 mid=((Vector3)args[4]+(Vector3)args[6])*.5f;var p=c.WorldToScreenPoint(mid);return new Vector2(p.x,p.y);}
 static void Main(){
  var c=Cam();var t=new Transform();int commits=0,rotations=0,previews=0;Vector3 committed=new Vector3();
  var g=new EditGizmo(null,delegate(Vector3 p){commits++;committed=p;},delegate(Vector3 p){rotations++;},delegate(Vector3 p){previews++;});
  var button=Mouse.current.leftButton;
  for(int h=6;h<=9;h++){
   t.position=new Vector3();var p=Handle(c,h);button.isPressed=true;button.wasPressedThisFrame=true;
   Check(g.Update(c,p,t,false,true,false) && g.Dragging,"square starts drag: "+h);
   Check((int)typeof(EditGizmo).GetField("_locked",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(g)==h,"drawn square matches picked handle");
   button.wasPressedThisFrame=false;var end=p+new Vector2(21,13);g.Update(c,end,t,false,true,false);
   Check(t.position.magnitude>.01,"square moves object");
   if(h==6)Check(Math.Abs(t.position.z)<1e-5,"XY locks Z");if(h==7)Check(Math.Abs(t.position.y)<1e-5,"XZ locks Y");if(h==8)Check(Math.Abs(t.position.x)<1e-5,"YZ locks X");if(h==9)Check(Math.Abs(Vector3.Dot(t.position,c.transform.forward))<1e-5,"center locks camera depth");
   button.isPressed=false;g.Update(c,end,t,false,true,false);Check(commits==h-5 && rotations==0 && !g.Dragging,"square commits one move, never a rotation");
  }
  t.position=new Vector3();button.isPressed=true;button.wasPressedThisFrame=false;Check(!g.Update(c,Handle(c,9),t,false,true,false),"held selection cannot start free move");
  button.wasPressedThisFrame=true;Check(!g.Update(c,new Vector2(800,800),t,false,true,false),"object geometry outside handles cannot start drag");Check(!g.Update(c,Handle(c,9),t,true,true,false),"panel blocks handles");Check(!g.Update(c,Handle(c,9),t,false,false,false),"busy blocks handles");
  g.Update(c,Handle(c,9),t,false,true,false);button.wasPressedThisFrame=false;g.Update(c,new Vector2(520,510),t,false,true,false);g.Cancel(false);Check(t.position.magnitude<1e-5 && commits==4,"cancel restores position without committing");
  g.Draw(c,new Vector3());Check(previews>0,"group preview receives square movements");
  Console.WriteLine("PASS: visible square picking, XY/XZ/YZ constraints, center free move, fresh press, body click, panel/busy guards, one commit and cancellation.");
 }
}
'''
with tempfile.TemporaryDirectory(prefix='prefab-gizmo-') as folder:
    root=Path(folder);(root/'Test.cs').write_text(harness)
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/out:'+str(root/'test.exe'),str(source),str(root/'Test.cs')],check=True)
    subprocess.run([str(root/'test.exe')],check=True)
