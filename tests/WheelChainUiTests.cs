using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
[assembly:TargetFramework(".NETFramework,Version=v4.8")]
class WheelChainUiTests {
 [StructLayout(LayoutKind.Sequential)]struct Info {public uint Size,Mask;public int Min,Max;public uint Page;public int Position,Track;}
 [DllImport("user32.dll")]static extern bool GetScrollInfo(IntPtr window,int bar,ref Info info);
 [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr hwnd,uint msg,IntPtr w,IntPtr l);
 static Info Range(Control c){var i=new Info{Size=(uint)Marshal.SizeOf(typeof(Info)),Mask=7};GetScrollInfo(c.Handle,1,ref i);return i;}
 static int Max(Control c){Info i=Range(c);return Math.Max(i.Min,i.Max-(int)Math.Max(0L,(long)i.Page-1));}
 static void Pump(int ms){Stopwatch s=Stopwatch.StartNew();while(s.ElapsedMilliseconds<ms){Application.DoEvents();System.Threading.Thread.Sleep(2);}}
 static int Y(Panel p){return -p.AutoScrollPosition.Y;}
 static void Wheel(Control target,int delta,int flags=0,Control location=null){Control point=location??target;Point screen=point.PointToScreen(new Point(10,10));long pos=((long)(ushort)(short)screen.Y<<16)|(ushort)(short)screen.X;SendMessage(target.Handle,0x020A,new IntPtr((delta<<16)|flags),new IntPtr(pos));}
 static Control Field(Type t,Form f,string n){return (Control)t.GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);}
 static void InputAt(TextBox input,int line){int first=(int)SendMessage(input.Handle,0x00CE,IntPtr.Zero,IntPtr.Zero);SendMessage(input.Handle,0x00B6,IntPtr.Zero,new IntPtr(line-first));}
 static void LongInput(TextBox input){var s=new StringBuilder();for(int i=0;i<100;i++)s.Append("SYNTHETIC_LINE_").Append(i).Append("\r\n");input.Text=s.ToString();input.Focus();input.Select(4,7);InputAt(input,40);}
 static void Outer(Panel body,int y=100){body.AutoScrollPosition=new Point(0,y);Pump(20);}
 static void Check(Dictionary<string,object> result,string name,bool yes){result[name]=yes;}
 [STAThread]static int Main(string[] args){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);string root=Path.GetFullPath(args[1]);if(Directory.Exists(root)&&Directory.GetFileSystemEntries(root).Length>0)throw new Exception("Test state directory must be new and empty.");Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"settings.json"),"{\"OutputFolder\":\"C:\\\\Music\\\\AppleMusic\",\"ScanFolder\":\"\"}");Type type=Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("MainWindow");var result=new Dictionary<string,object>();
 using(Form form=(Form)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{root},null)){
  form.ShowInTaskbar=false;form.Show();Application.DoEvents();Panel body=(Panel)Field(type,form,"bodySurface");TextBox input=(TextBox)Field(type,form,"link");ListView queue=(ListView)Field(type,form,"queueView");int step=(int)Math.Round(24*form.DeviceDpi/96.0);result["dpi"]=form.DeviceDpi;result["wheel_lines"]=SystemInformation.MouseWheelScrollLines;result["step_pixels"]=step;
  input.Focus();input.Clear();Outer(body);Wheel(input,-120);Pump(250);Check(result,"empty_input_down_chains_once",Y(body)==100+step);Outer(body);Wheel(input,120);Pump(250);Check(result,"empty_input_up_chains_once",Y(body)==100-step);
  input.Text="synthetic single line";input.Select(3,5);Outer(body);Wheel(input,-120);Pump(250);Check(result,"short_input_chains_and_preserves_selection",Y(body)==100+step&&input.SelectionStart==3&&input.SelectionLength==5);
  input.Clear();Outer(body);for(int i=0;i<12;i++)Wheel(input,-10);Pump(250);Check(result,"empty_input_fine_delta_exactly_one_notch",Y(body)==100+step);
  LongInput(input);Outer(body);int start=Range(input).Position;string text=input.Text;int selection=input.SelectionStart,length=input.SelectionLength;Wheel(input,-120);Pump(250);Check(result,"long_middle_native_down_only",Y(body)==100&&Range(input).Position>start);int after=Range(input).Position;Wheel(input,120);Pump(250);Check(result,"long_middle_native_up_only",Y(body)==100&&Range(input).Position<after);Check(result,"text_selection_focus_unchanged",input.Text==text&&input.SelectionStart==selection&&input.SelectionLength==length&&input.Focused);
  InputAt(input,40);Outer(body);start=Range(input).Position;for(int i=0;i<3;i++)Wheel(input,-30);Check(result,"native_fine_partial_not_sent_to_parent",Range(input).Position==start&&Y(body)==100);Wheel(input,-30);Pump(250);Check(result,"native_fine_full_notch_no_hidden_cache",Range(input).Position>start&&Y(body)==100);
  InputAt(input,0);Outer(body);Wheel(input,120);Pump(250);Check(result,"long_top_up_chains",Y(body)==100-step&&Range(input).Position==0);Outer(body);Wheel(input,-120);Pump(250);Check(result,"long_top_down_keeps_native",Y(body)==100&&Range(input).Position>0);
  InputAt(input,10000);Outer(body);start=Range(input).Position;Wheel(input,-120);Pump(250);Check(result,"long_bottom_down_chains",Y(body)==100+step&&Range(input).Position==start);Outer(body);Wheel(input,120);Pump(250);Check(result,"long_bottom_up_keeps_native",Y(body)==100&&Range(input).Position<start);
  InputAt(input,Max(input)-1);Outer(body);Wheel(input,-90);Wheel(input,-60);Pump(250);int partial=step/4;Check(result,"native_edge_residual_handed_off_once",Range(input).Position==Max(input)&&Y(body)==100+partial);Wheel(input,-90);Pump(250);Check(result,"edge_residual_plus_next_delta_conserved",Y(body)==100+step);start=Range(input).Position;Wheel(input,30);Wheel(input,90);Pump(250);Check(result,"reverse_native_has_no_old_partial_cache",Range(input).Position<start&&Y(body)==100+step);
  InputAt(input,Max(input)-1);Outer(body);Wheel(input,-360);Pump(250);Check(result,"multi_notch_packet_chains_only_unconsumed_notches",Range(input).Position==Max(input)&&Y(body)==100+2*step);
  InputAt(input,40);Outer(body);Wheel(input,-90);input.Clear();Wheel(input,-30);Pump(250);Check(result,"content_change_discards_old_native_partial",Y(body)==100+step/4);
  input.Clear();Outer(body);Wheel(body,-120,0,input);Pump(250);Check(result,"parent_hover_route_empty_input_once",Y(body)==100+step);LongInput(input);Outer(body);start=Range(input).Position;Wheel(body,-120,0,input);Pump(250);Check(result,"parent_hover_route_long_input_native_once",Range(input).Position>start&&Y(body)==100);
  input.Clear();Outer(body);Wheel(input,-120);Pump(250);int down=Y(body);Wheel(input,120);Pump(250);Check(result,"empty_input_reversal_no_stuck_edge",down==100+step&&Y(body)==100);
  foreach(int keys in new[]{4,8,1}){input.Clear();Outer(body);Wheel(input,-120,keys);Pump(180);Check(result,"modified_input_no_parent_keys_"+keys,Y(body)==100&&input.Text=="");}
  queue.Items.Clear();queue.Focus();Outer(body);Wheel(queue,-120);Pump(250);Check(result,"empty_queue_chains_down_once",Y(body)==100+step);Outer(body);Wheel(queue,120);Pump(250);Check(result,"empty_queue_chains_up_once",Y(body)==100-step);queue.Items.Add(new ListViewItem("synthetic one"));Outer(body);Wheel(queue,-120);Pump(250);Check(result,"one_row_queue_chains",Y(body)==100+step);
  queue.Items.Clear();for(int i=0;i<100;i++)queue.Items.Add(new ListViewItem(new[]{"Synthetic "+i,"waiting","—"}));queue.TopItem=queue.Items[40];queue.Items[42].Selected=true;Outer(body);start=Range(queue).Position;Wheel(queue,-120);Pump(250);Check(result,"queue_middle_native_only",Y(body)==100&&Range(queue).Position>start&&queue.Items[42].Selected&&queue.Focused);
  queue.TopItem=queue.Items[0];Outer(body);Wheel(queue,120);Pump(250);Check(result,"queue_top_up_chains",Y(body)==100-step);queue.TopItem=queue.Items[99];Outer(body);Wheel(queue,-120);Pump(250);Check(result,"queue_bottom_down_chains",Y(body)==100+step);Outer(body);for(int i=0;i<4;i++)Wheel(queue,-30);Pump(250);Check(result,"queue_edge_fine_delta_no_double",Y(body)==100+step);
  foreach(int keys in new[]{4,8}){queue.TopItem=queue.Items[99];Outer(body);Wheel(queue,-120,keys);Pump(180);Check(result,"modified_queue_no_parent_keys_"+keys,Y(body)==100&&queue.Items[42].Selected);}
  input.Clear();typeof(Control).GetMethod("RecreateHandle",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(input,null);Outer(body);Wheel(input,-120);Pump(250);Check(result,"input_handle_recreation_keeps_chain",Y(body)==100+step);
  queue.Items.Clear();typeof(Control).GetMethod("RecreateHandle",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(queue,null);Outer(body);result["recreated_queue_before_y"]=Y(body);result["recreated_queue_range"]=Range(queue);result["recreated_queue_handle"]=queue.Handle.ToInt64();Wheel(queue,-120);Pump(250);result["recreated_queue_after_y"]=Y(body);Check(result,"queue_handle_recreation_keeps_chain",Y(body)==100+step);
  queue.Items.Clear();Outer(body,0);Wheel(queue,120);Pump(250);Check(result,"page_top_chain_clamped",Y(body)==0);int maximum=body.VerticalScroll.Maximum-body.VerticalScroll.LargeChange+1;Outer(body,maximum);Wheel(queue,-120);Pump(250);Check(result,"page_bottom_chain_clamped",Y(body)==maximum);Wheel(queue,120);Pump(250);Check(result,"page_bottom_reverse_not_stuck",Y(body)==maximum-step);
  form.Close();Check(result,"closed_cleanly",form.IsDisposed);
 }
 bool passed=SystemInformation.MouseWheelScrollLines==3;foreach(var pair in result)if(pair.Value is bool)passed&=(bool)pair.Value;result["passed"]=passed;File.WriteAllText(Path.Combine(root,"wheel-chain-results.json"),new JavaScriptSerializer().Serialize(result));return passed?0:1;
 }
}
