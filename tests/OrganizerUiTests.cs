using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Windows.Forms;
[assembly:TargetFramework(".NETFramework,Version=v4.8")]
class OrganizerUiTests {
 static Type type;
 static object V(Form f,string n){return type.GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);}
 static void Call(Form f,string name){type.GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f,null);}
 static IEnumerable<Control> Desc(Control c){foreach(Control x in c.Controls){yield return x;foreach(Control y in Desc(x))yield return y;}}
 static void Pump(Func<bool> done){var clock=Stopwatch.StartNew();while(!done()&&clock.ElapsedMilliseconds<30000){Application.DoEvents();System.Threading.Thread.Sleep(10);}if(!done())throw new Exception("bounded organizer timeout");}
 static string Hash(string path){using(var s=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant();}
 static string[] Audio(string root){return Directory.GetFiles(root,"*.m4a",SearchOption.AllDirectories);}
 [STAThread]static int Main(string[] args){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);type=Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("MainWindow");string fixtures=Path.GetFullPath(args[1]);if(!File.Exists(Path.Combine(fixtures,"synthetic-fixture.txt"))||File.ReadAllText(Path.Combine(fixtures,"synthetic-fixture.txt"))!="SYNTHETIC_ORGANIZER_UI_ONLY")throw new Exception("Prepared synthetic fixture marker required; never point tests at real music.");var result=new Dictionary<string,object>();bool all=true;
 foreach(string mode in new[]{"cancel","close-preview","confirm","close-main","download-busy"}){
  string root=Path.Combine(fixtures,mode),music=Path.Combine(root,"music");Form form=null;try{
   var setting=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(root,"settings.json")));if(Path.GetFullPath(Convert.ToString(setting["OutputFolder"]))!=Path.GetFullPath(music))throw new Exception("Fixture output must be its synthetic music folder.");var hashes=new JavaScriptSerializer().Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(root,"fixture-hashes.json")));foreach(var pair in hashes)if(Hash(Path.Combine(music,pair.Key))!=pair.Value)throw new Exception("Synthetic input changed before test.");
   form=(Form)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{root},null);form.ShowInTaskbar=false;form.Show();Application.DoEvents();Button organize=(Button)V(form,"organize");Size text=TextRenderer.MeasureText(organize.Text,organize.Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine);result[mode+"_button_text_fits"]=organize.Width>=text.Width+organize.Padding.Horizontal;
   if(mode=="download-busy"){
    type.GetField("sessionToken",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,"SYNTHETIC_LOGIN_FOR_MOCK_ONLY");((TextBox)V(form,"link")).Text="https://music.apple.com/us/song/9";((Button)V(form,"download")).PerformClick();type.GetMethod("StartOrganize",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{form,EventArgs.Empty});result[mode+"_excluded"]=V(form,"orgProcess")==null&&V(form,"process")!=null&&!organize.Enabled;Call(form,"CancelQueue");Pump(delegate{return V(form,"process")==null;});form.Close();form=null;continue;
   }
   bool previewSeen=false,previewRows=false,approved=false,closeDeferred=false,downloadGuard=false;
   using(var action=new System.Windows.Forms.Timer()){
    action.Interval=50;action.Tick+=delegate{string job=Convert.ToString(V(form,"orgJob"));if(!String.IsNullOrEmpty(job))approved|=File.Exists(Path.Combine(job,"approve.flag"));Form preview=null;foreach(Form f in Application.OpenForms)if(f.GetType().Name=="OrganizePreview"){preview=f;break;}if(preview==null)return;previewSeen=true;foreach(Control c in Desc(preview)){ListView list=c as ListView;if(list!=null)previewRows=list.Items.Count==2;}if(mode=="close-preview")preview.Close();else foreach(Control c in Desc(preview)){Button b=c as Button;if(b!=null&&b.Text==(mode=="confirm"?"确认整理":"取消，不移动")){b.PerformClick();break;}}};
    organize.PerformClick();result[mode+"_busy_controls_locked"]=!organize.Enabled&&!((Button)V(form,"download")).Enabled&&!((TextBox)V(form,"link")).Enabled;
    type.GetMethod("StartDownload",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{form,EventArgs.Empty});downloadGuard=V(form,"process")==null;
    if(mode=="close-main"){form.Close();closeDeferred=!form.IsDisposed;}else action.Start();
    Pump(delegate{return !(bool)V(form,"organizing");});action.Stop();
   }
   result[mode+"_download_guard"]=downloadGuard;
   if(mode=="confirm"){result[mode+"_moved_only_after_preview"]=previewSeen&&previewRows&&File.Exists(Path.Combine(music,"Synthetic UI Album","one.m4a"))&&File.Exists(Path.Combine(music,"Synthetic UI Album","two.m4a"))&&!File.Exists(Path.Combine(music,"one.m4a"));result[mode+"_journal_present"]=Directory.Exists(Path.Combine(music,".applemusic-organizer"))&&Directory.GetFiles(Path.Combine(music,".applemusic-organizer"),"*.jsonl").Length==1;}
   else{result[mode+"_no_music_moved"]=File.Exists(Path.Combine(music,"one.m4a"))&&File.Exists(Path.Combine(music,"two.m4a"))&&!Directory.Exists(Path.Combine(music,"Synthetic UI Album"));result[mode+"_no_approval"]=!approved;if(mode=="close-main")result[mode+"_close_deferred_until_safe"]=closeDeferred;else result[mode+"_preview_was_shown"]=previewSeen&&previewRows;}
   result[mode+"_private_plan_cleaned"]=Directory.GetDirectories(Path.Combine(root,"private","organize-jobs")).Length==0;result[mode+"_exact_audio_count"]=Audio(music).Length==2;bool bytes=true;foreach(var pair in hashes){string file=mode=="confirm"?Path.Combine(music,"Synthetic UI Album",pair.Key):Path.Combine(music,pair.Key);bytes&=File.Exists(file)&&Hash(file)==pair.Value;}result[mode+"_all_audio_bytes_preserved"]=bytes;form.Close();form=null;
  }catch(Exception e){result[mode+"_failure"]=e.GetType().Name+":"+e.Message;all=false;}finally{if(form!=null&&!form.IsDisposed){if((bool)V(form,"organizing")){Call(form,"CancelOrganize");Pump(delegate{return !(bool)V(form,"organizing");});}Call(form,"CancelQueue");Pump(delegate{return V(form,"process")==null;});form.Close();}}
 }
 foreach(var pair in result)if(pair.Value is bool)all&=(bool)pair.Value;result["passed"]=all;File.WriteAllText(Path.Combine(fixtures,"organizer-ui-results.json"),new JavaScriptSerializer().Serialize(result));return all?0:1;
 }
}
