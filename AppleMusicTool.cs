using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8",FrameworkDisplayName=".NET Framework 4.8")]

internal static class WindowLayout {
    public static Size FitSize(Rectangle work,Size preferred,float scale) {
        int marginX=(int)Math.Round(32*scale),marginY=(int)Math.Round(48*scale);
        return new Size(Math.Min((int)Math.Round(preferred.Width*scale),Math.Max(180,work.Width-marginX)),Math.Min((int)Math.Round(preferred.Height*scale),Math.Max(180,work.Height-marginY)));
    }
    public static Size FitSize(Rectangle work,Size preferred) {return FitSize(work,preferred,1F);}
    public static void Fit(Form form,Size preferred) {
        Rectangle work=Screen.FromControl(form).WorkingArea;float scale=form.DeviceDpi/96F;Size size=FitSize(work,preferred,scale);
        form.MinimumSize=new Size(Math.Min((int)Math.Round(400*scale),size.Width),Math.Min((int)Math.Round(280*scale),size.Height));form.Size=size;
        form.Location=new Point(work.Left+(work.Width-size.Width)/2,work.Top+(work.Height-size.Height)/2);
    }
    public static void Clamp(Form form) {
        if(form.IsDisposed||form.Disposing)return;
        Rectangle work=Screen.FromControl(form).WorkingArea;float scale=form.DeviceDpi/96F;Size cap=FitSize(work,new Size(10000,10000),scale);
        form.MinimumSize=new Size(Math.Min((int)Math.Round(400*scale),cap.Width),Math.Min((int)Math.Round(280*scale),cap.Height));
        Size size=new Size(Math.Min(form.Width,cap.Width),Math.Min(form.Height,cap.Height));form.Size=size;
        form.Location=new Point(Math.Max(work.Left,Math.Min(form.Left,work.Right-size.Width)),Math.Max(work.Top,Math.Min(form.Top,work.Bottom-size.Height)));
    }
}

// Logical wheel distance and a single coalesced animation target. No queued
// animations: reverse input replaces the outstanding direction immediately.
internal sealed class ScrollMotion {
    public double Position {get;private set;}
    public double Target {get;private set;}
    private int direction;
    public bool Moving {get{return Math.Abs(Target-Position)>0.00001;}}
    private static double Clamp(double value,int maximum){return Math.Max(0,Math.Min(Math.Max(0,maximum),value));}
    public void Reset(int position,int maximum){Position=Target=Clamp(position,maximum);direction=0;}
    public void Wheel(int delta,int dpi,int lines,int viewport,int maximum){
        Position=Clamp(Position,maximum);Target=Clamp(Target,maximum);
        if(delta==0||lines==0)return;
        double unit=lines<0?Math.Max(1,viewport-24*dpi/96.0):8*dpi/96.0*lines;
        double distance=-delta/120.0*unit;int nextDirection=Math.Sign(distance);
        if(Moving&&direction!=0&&direction!=nextDirection)Target=Position;
        Target=Clamp(Target+distance,maximum);direction=nextDirection;
    }
    public int Advance(double milliseconds,int maximum){
        Position=Clamp(Position,maximum);Target=Clamp(Target,maximum);
        Position+=(Target-Position)*(1-Math.Exp(-Math.Max(0,Math.Min(64,milliseconds))/25.0));
        if(Math.Abs(Target-Position)<0.15)Position=Target;
        return (int)Math.Round(Position,MidpointRounding.AwayFromZero);
    }
    public int Finish(){Position=Target;return (int)Math.Round(Position,MidpointRounding.AwayFromZero);}
}

// Keep partial normal-wheel deltas out of the native control's hidden cache so
// a boundary hand-off cannot leave stale native input for a later direction.
internal sealed class WheelRemainder {
    public int Pending {get;private set;}
    public void Reset(){Pending=0;}
    private int Combine(int delta){if(delta==0)return Pending;if(Pending!=0&&Math.Sign(Pending)!=Math.Sign(delta))Pending=0;return Pending+delta;}
    public int Native(int delta){int sum=Combine(delta),whole=sum/120*120;Pending=sum-whole;return whole;}
    public int Outer(int delta){int sum=Combine(delta);Pending=0;return sum;}
    public int Drain(){int value=Pending;Pending=0;return value;}
}

internal sealed class SmoothScrollPanel : Panel {
    private readonly ScrollMotion motion=new ScrollMotion();
    private readonly Stopwatch elapsed=new Stopwatch();
    private readonly System.Windows.Forms.Timer animation;
    private int rendered=-1;
    private bool applying;
    private int nativeDispatchDepth;
    private readonly Dictionary<Control,NativeScroller> scrollers=new Dictionary<Control,NativeScroller>();
    private NativeScroller nativeOwner;
    public int AnimationTicks {get;private set;}
    public bool AnimationRunning {get{return animation!=null&&animation.Enabled;}}
    [DllImport("user32.dll",EntryPoint="SystemParametersInfoW")]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadSystemPreference(uint action,uint parameter,[MarshalAs(UnmanagedType.Bool)]out bool value,uint flags);
    private static bool AnimationAllowed(){bool value;return ReadSystemPreference(0x1042,0,out value,0)&&value;}
    private int MaximumOffset {get{return VerticalScroll.Visible?Math.Max(0,VerticalScroll.Maximum-VerticalScroll.LargeChange+1):0;}}
    public SmoothScrollPanel(){AutoScroll=true;DoubleBuffered=true;animation=new System.Windows.Forms.Timer();animation.Interval=15;animation.Tick+=Animate;}
    private void StopAndSync(){if(animation!=null)animation.Stop();elapsed.Reset();rendered=Math.Max(0,-AutoScrollPosition.Y);motion.Reset(rendered,MaximumOffset);}
    private void Render(int position){if(position==rendered)return;applying=true;try{AutoScrollPosition=new Point(-AutoScrollPosition.X,position);rendered=Math.Max(0,-AutoScrollPosition.Y);}finally{applying=false;}}
    private void Animate(object sender,EventArgs args){
        // Focus navigation/scrollbar changes always win over an old wheel target.
        if(!Visible||!IsHandleCreated||Math.Max(0,-AutoScrollPosition.Y)!=rendered){StopAndSync();return;}
        AnimationTicks++;double dt=elapsed.Elapsed.TotalMilliseconds;elapsed.Restart();Render(motion.Advance(dt,MaximumOffset));
        if(!motion.Moving){animation.Stop();elapsed.Reset();}
    }
    private NativeScroller NativeAt(Point screen){
        Control parent=this;
        while(true){Control child=parent.GetChildAtPoint(parent.PointToClient(screen),GetChildAtPointSkip.Invisible);if(child==null)return null;NativeScroller scroller;if(scrollers.TryGetValue(child,out scroller))return scroller;parent=child;}
    }
    public void RegisterNativeScroller(Control control){
        TextBox text=control as TextBox;
        if(!Contains(control)||(!(control is ListView)&&(text==null||!text.Multiline||text.UseSystemPasswordChar||text.PasswordChar!='\0')))throw new ArgumentException("scroller");
        if(!scrollers.ContainsKey(control))scrollers.Add(control,new NativeScroller(this,control));
    }
    private void SelectOuter(){if(nativeOwner!=null){nativeOwner.Reset();nativeOwner=null;}}
    private void SelectNative(NativeScroller scroller){if(nativeOwner!=scroller){if(nativeOwner!=null)nativeOwner.Reset();scroller.Reset();nativeOwner=scroller;}StopAndSync();}
    private void ApplyWheel(int delta){
        if(delta==0)return;SelectOuter();if(rendered<0||Math.Max(0,-AutoScrollPosition.Y)!=rendered)StopAndSync();
        int lines=SystemInformation.MouseWheelScrollLines;if(lines==0){StopAndSync();return;}
        bool animate=animation.Enabled||AnimationAllowed();Form owner=FindForm();motion.Wheel(delta,owner==null?DeviceDpi:owner.DeviceDpi,lines,ClientSize.Height,MaximumOffset);
        if(!animate)Render(motion.Finish());else if(motion.Moving&&!animation.Enabled){elapsed.Restart();animation.Start();}
    }
    private sealed class NativeScroller:NativeWindow,IDisposable {
        [StructLayout(LayoutKind.Sequential)]private struct ScrollInfo{public uint Size,Mask;public int Min,Max;public uint Page;public int Position,Track;}
        [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetScrollInfo(IntPtr window,int bar,ref ScrollInfo info);
        [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
        private readonly SmoothScrollPanel owner;private readonly Control control;private readonly WheelRemainder pending=new WheelRemainder();
        private bool disposed,knownPosition;private int lastPosition,lastMin,lastMax;private uint lastPage;
        public NativeScroller(SmoothScrollPanel panel,Control child){owner=panel;control=child;control.HandleCreated+=Created;control.HandleDestroyed+=Destroyed;control.TextChanged+=Changed;control.SizeChanged+=Changed;control.Disposed+=Closed;if(control.IsHandleCreated)AssignHandle(control.Handle);}
        private void Created(object sender,EventArgs args){Reset();if(Handle==IntPtr.Zero)AssignHandle(control.Handle);}
        private void Destroyed(object sender,EventArgs args){Reset();if(Handle!=IntPtr.Zero)ReleaseHandle();}
        private void Changed(object sender,EventArgs args){Reset();}
        private void Closed(object sender,EventArgs args){Dispose();}
        public void Reset(){pending.Reset();knownPosition=false;}
        private bool CanScroll(int delta){
            ListView list=control as ListView;
            if(list!=null&&Handle!=IntPtr.Zero){
                // A recreated native ListView can expose a placeholder scrollbar
                // range (0..100, page 0) even with no rows. Query its real rows.
                int count=list.Items.Count,top=Math.Max(0,SendMessage(Handle,0x1027,IntPtr.Zero,IntPtr.Zero).ToInt32());
                uint page=(uint)Math.Max(1,SendMessage(Handle,0x1028,IntPtr.Zero,IntPtr.Zero).ToInt32());
                if(knownPosition&&(lastPosition!=top||lastMax!=count||lastPage!=page))pending.Reset();lastPosition=top;lastMin=0;lastMax=count;lastPage=page;knownPosition=true;
                return delta<0?top+(long)page<count:delta>0&&top>0;
            }
            var info=new ScrollInfo{Size=(uint)Marshal.SizeOf(typeof(ScrollInfo)),Mask=7};
            if(Handle==IntPtr.Zero||!GetScrollInfo(Handle,1,ref info)){Reset();return false;}
            if(knownPosition&&(lastPosition!=info.Position||lastMin!=info.Min||lastMax!=info.Max||lastPage!=info.Page))pending.Reset();lastPosition=info.Position;lastMin=info.Min;lastMax=info.Max;lastPage=info.Page;knownPosition=true;
            int maximum=Math.Max(info.Min,info.Max-(int)Math.Max(0L,(long)info.Page-1));
            return delta<0?info.Position<maximum:delta>0&&info.Position>info.Min;
        }
        public IntPtr Dispatch(Message message){return SendMessage(control.Handle,(uint)message.Msg,message.WParam,message.LParam);}
        protected override void WndProc(ref Message message){
            if(message.Msg==0x020A){
                int delta=(short)((message.WParam.ToInt64()>>16)&0xffff);int flags=(int)(message.WParam.ToInt64()&0xffff);if(delta==0){message.Result=IntPtr.Zero;return;}
                // Keep native modified-wheel/drag semantics and all original bits.
                if(flags!=0||(Control.ModifierKeys&Keys.Alt)!=0){owner.SelectNative(this);Reset();owner.nativeDispatchDepth++;try{base.WndProc(ref message);}finally{owner.nativeDispatchDepth--;}return;}
                bool canScroll=CanScroll(delta);
                if(!canScroll){int outgoing=pending.Outer(delta);owner.ApplyWheel(outgoing);message.Result=IntPtr.Zero;return;}
                owner.SelectNative(this);int whole=pending.Native(delta);
                if(whole==0){message.Result=IntPtr.Zero;return;}
                IntPtr original=message.WParam;int remaining=whole;
                try{
                    // A packet can contain several notches. Consume only the
                    // notches that fit inside, then hand the remainder out once.
                    while(remaining!=0){
                        int part=Math.Sign(remaining)*120;message.WParam=new IntPtr((part<<16)|flags);owner.nativeDispatchDepth++;
                        try{base.WndProc(ref message);}finally{owner.nativeDispatchDepth--;}
                        remaining-=part;knownPosition=false;
                        if(!CanScroll(delta)){int outgoing=remaining+pending.Drain();if(outgoing!=0)owner.ApplyWheel(outgoing);break;}
                    }
                }finally{message.WParam=original;}
                return;
            }
            if(owner.nativeDispatchDepth==0&&(message.Msg==0x0115||message.Msg==0x0114||message.Msg==0x0100||message.Msg==0x00B6||message.Msg==0x00B7))Reset();
            base.WndProc(ref message);
        }
        public void Dispose(){if(disposed)return;disposed=true;control.HandleCreated-=Created;control.HandleDestroyed-=Destroyed;control.TextChanged-=Changed;control.SizeChanged-=Changed;control.Disposed-=Closed;if(Handle!=IntPtr.Zero)ReleaseHandle();Reset();}
    }
    protected override void WndProc(ref Message message){
        if(message.Msg==0x020A){
            if(nativeDispatchDepth>0){message.Result=IntPtr.Zero;return;}
            long point=message.LParam.ToInt64();Point screen=new Point((short)(point&0xffff),(short)((point>>16)&0xffff));
            NativeScroller scroller=NativeAt(screen);if(scroller!=null){message.Result=scroller.Dispatch(message);return;}
            if(!VerticalScroll.Visible){base.WndProc(ref message);return;}
            int delta=(short)((message.WParam.ToInt64()>>16)&0xffff);
            ApplyWheel(delta);
            message.Result=IntPtr.Zero;return; // do not invoke default AutoScroll again
        }
        if(message.Msg==0x0115||message.Msg==0x0114)StopAndSync();
        base.WndProc(ref message);
    }
    protected override void OnScroll(ScrollEventArgs args){if(!applying)StopAndSync();base.OnScroll(args);}
    protected override void OnClientSizeChanged(EventArgs args){if(animation!=null)StopAndSync();base.OnClientSizeChanged(args);}
    protected override void OnVisibleChanged(EventArgs args){if(animation!=null&&!Visible)StopAndSync();base.OnVisibleChanged(args);}
    protected override void Dispose(bool disposing){if(disposing&&animation!=null){animation.Stop();animation.Dispose();foreach(var scroller in scrollers.Values)scroller.Dispose();scrollers.Clear();}base.Dispose(disposing);}
}

internal sealed class BusyGate {
    private int busy;
    public bool Enter() { return Interlocked.CompareExchange(ref busy, 1, 0) == 0; }
    public void Leave() { Interlocked.Exchange(ref busy, 0); }
}

internal sealed class CredentialVault {
    private readonly string folder;
    private readonly string file;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AppleMusicAACTool.v1");
    public CredentialVault(string privateFolder) { folder = privateFolder; file = Path.Combine(folder, "login.dpapi"); }
    public bool Exists { get { return File.Exists(file); } }
    public static void PrivateDirectory(string path) {
        Directory.CreateDirectory(path);
        DirectorySecurity acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        Directory.SetAccessControl(path, acl);
    }
    public void Save(string token) {
        PrivateDirectory(folder);
        byte[] plain = Encoding.UTF8.GetBytes(token);
        try {
            byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            string temporary = file + ".new";
            File.WriteAllBytes(temporary, encrypted);
            if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
        } finally { Array.Clear(plain, 0, plain.Length); }
    }
    public string Load() {
        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); } finally { Array.Clear(plain, 0, plain.Length); }
    }
    public void Clear() { if (File.Exists(file)) File.Delete(file); }
}

internal static class InputRules {
    public static string TokenValue(string value) {
        string token = value.Trim();
        if (token.Length < 20 || token.Length > 32768 || token == "LOCAL_VALUE_HERE" || token == "YOUR_TOKEN_HERE" || token.StartsWith("media-user-token=") || Regex.IsMatch(token,@"[\s\x00-\x20\x7f-\uffff<>]")) throw new Exception("cookie_invalid");
        return token;
    }
    public static string SongUrl(string input, out string songId) {
        songId = null;
        Uri uri;
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.Authority != "music.apple.com" || uri.UserInfo != "" || uri.Fragment != "") throw new Exception("invalid_url");
        Match match = Regex.Match(uri.AbsolutePath, @"^/([a-z]{2})/(song|album)/(?:[^/]+/)?([0-9]{1,20})/?$");
        if (!match.Success) throw new Exception("invalid_url");
        var query = HttpUtility.ParseQueryString(uri.Query);
        if (match.Groups[2].Value == "album") {
            string[] ids = query.GetValues("i");
            if (ids == null || ids.Length != 1 || !Regex.IsMatch(ids[0], @"^[0-9]{1,20}$")) throw new Exception("invalid_url");
            songId = ids[0];
        } else {
            songId = match.Groups[3].Value;
            if (query["i"] != null && query["i"] != songId) throw new Exception("invalid_url");
        }
        return "https://music.apple.com/" + match.Groups[1].Value + "/song/" + songId;
    }
    public static string CookieToken(string source) {
        FileInfo info = new FileInfo(source);
        if (!info.Exists || info.Length <= 0 || info.Length >= 1048576 || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new Exception("cookie_invalid");
        string token = null;
        foreach (string original in File.ReadAllLines(source, Encoding.UTF8)) {
            string line = original;
            if (line.StartsWith("#HttpOnly_")) line = line.Substring(10);
            if (line.Trim().Length == 0 || line.StartsWith("#")) continue;
            string[] fields = line.Split('\t');
            if (fields.Length != 7 || fields[0] != ".music.apple.com" || fields[1] != "TRUE" || fields[2] != "/" || fields[3] != "TRUE" || !Regex.IsMatch(fields[4], @"^[0-9]+$") || fields[5] != "media-user-token" || token != null) throw new Exception("cookie_invalid");
            token = fields[6];
        }
        if (String.IsNullOrWhiteSpace(token) || token == "LOCAL_VALUE_HERE" || token.IndexOfAny(new char[] {'\r','\n','\t','\0'}) >= 0) throw new Exception("cookie_invalid");
        return token;
    }
}

internal sealed class KillJob : IDisposable {
    private IntPtr handle;
    [StructLayout(LayoutKind.Sequential)] struct Basic { public long A, B; public uint Flags; public UIntPtr C, D; public uint E; public UIntPtr F; public uint G, H; }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] struct Extended { public Basic Basic; public Io Io; public UIntPtr A, B, C, D; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int type, IntPtr info, uint size);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public KillJob() {
        handle = CreateJobObject(IntPtr.Zero, null);
        Extended value = new Extended(); value.Basic.Flags = 0x2000;
        int size = Marshal.SizeOf(typeof(Extended)); IntPtr ptr = Marshal.AllocHGlobal(size);
        try { Marshal.StructureToPtr(value, ptr, false); if (handle == IntPtr.Zero || !SetInformationJobObject(handle, 9, ptr, (uint)size)) throw new Exception("job_object"); }
        catch { Dispose(); throw; } finally { Marshal.FreeHGlobal(ptr); }
    }
    public void Assign(Process process) { if (!AssignProcessToJobObject(handle, process.Handle)) throw new Exception("job_object"); }
    public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
}

internal sealed class ToolSettings {
    public string OutputFolder { get; set; }
    public string ScanFolder { get; set; }
    public static string DefaultOutput() { string music=Environment.GetFolderPath(Environment.SpecialFolder.MyMusic); if(String.IsNullOrEmpty(music)) music=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Music"); return Path.Combine(music,"AppleMusic"); }
    public static ToolSettings Load(string root) { try { var value=new JavaScriptSerializer().Deserialize<ToolSettings>(File.ReadAllText(Path.Combine(root,"settings.json"))); if(value != null && Path.IsPathRooted(value.OutputFolder)) return value; } catch { } return new ToolSettings { OutputFolder=DefaultOutput(), ScanFolder="" }; }
    public void Save(string root) { string file=Path.Combine(root,"settings.json"), temp=file+".new"; File.WriteAllText(temp,new JavaScriptSerializer().Serialize(this),new UTF8Encoding(false)); if(File.Exists(file)) File.Replace(temp,file,null); else File.Move(temp,file); }
    public static void CheckWritable(string folder) { string full=Path.GetFullPath(folder); if(full==Path.GetPathRoot(full)) throw new Exception("folder"); Directory.CreateDirectory(full); string probe=Path.Combine(full,".applemusic-write-"+Guid.NewGuid().ToString("N")+".tmp"); using(FileStream stream=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { } File.Delete(probe); }
}

internal sealed class CookieCandidate { public string FilePath; public DateTime Modified; }
internal static class CookieScan {
    public static bool CandidateName(string path) { string name=Path.GetFileName(path).ToLowerInvariant(); return name.EndsWith(".txt") && (name.StartsWith("cookies") || name.StartsWith("applemusic")) && !name.Contains("template") && !name.Contains("example") && !name.Contains("sample") && !name.Contains("模板"); }
    public static bool PathBoundary(string path,bool knownAppImport) {
        try {
            string full=Path.GetFullPath(path).TrimEnd('\\');
            if(full==Path.GetPathRoot(full).TrimEnd('\\'))return false;
            string[] components=full.ToLowerInvariant().Split(new char[]{'\\','/'},StringSplitOptions.RemoveEmptyEntries);
            bool downloads=false;
            foreach(string part in components) {
                if(part=="appdata" || part=="user data" || part=="userdata" || part=="profile" || part=="profiles" || part=="default" || part==".mozilla" || part=="chrome" || part=="chromium" || part=="firefox" || part=="edge" || part=="brave-browser" || part=="opera" || part=="vivaldi" || part.Contains("portable") || Regex.IsMatch(part,@"^profile [0-9]+$"))return false;
                if(part=="download" || part=="downloads" || part=="下载")downloads=true;
            }
            return knownAppImport || downloads;
        }catch{return false;}
    }
    public static bool AllowedFolder(string path,bool knownAppImport=false) {
        try {
            if(!PathBoundary(path,knownAppImport))return false;
            string full=Path.GetFullPath(path);
            if(!Directory.Exists(full) || (File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)return false;
            var owner=(SecurityIdentifier)Directory.GetAccessControl(full).GetOwner(typeof(SecurityIdentifier));
            if(!owner.Equals(WindowsIdentity.GetCurrent().User))return false;
            foreach(string marker in new string[]{"Local State","Preferences","Secure Preferences","prefs.js","cookies.sqlite","places.sqlite"})
                if(File.Exists(Path.Combine(full,marker)))return false;
            return true;
        }catch{return false;}
    }
    public static List<CookieCandidate> Find(string importFolder,string chosenFolder) {
        var results=new List<CookieCandidate>(); var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase); int examined=0;
        foreach(string folder in new string[] {importFolder,chosenFolder}) {
            if(String.IsNullOrEmpty(folder) || !AllowedFolder(folder,String.Equals(folder,importFolder,StringComparison.OrdinalIgnoreCase))) continue;
            foreach(string file in Directory.EnumerateFiles(folder,"*.txt",SearchOption.TopDirectoryOnly)) {
                if(!CandidateName(file) || !seen.Add(Path.GetFullPath(file))) continue;
                if(++examined>100) break;
                try { InputRules.CookieToken(file); results.Add(new CookieCandidate {FilePath=file,Modified=File.GetLastWriteTime(file)}); } catch { }
            }
        }
        results.Sort(delegate(CookieCandidate a,CookieCandidate b){return b.Modified.CompareTo(a.Modified);}); return results;
    }
}

internal sealed class LoginGuide : Form {
    private TextBox value; private Button save; private bool saved; private readonly Action<string> saver;
    public LoginGuide(Action<string> saveAction) {
        saver=saveAction;Text="登录指引 · 只粘贴自己的 Cookie 值";Font=new Font("Microsoft YaHei UI",9.5F);AutoScaleDimensions=new SizeF(96F,96F);AutoScaleMode=AutoScaleMode.Dpi;
        BackColor=Color.FromArgb(250,249,245);FormBorderStyle=FormBorderStyle.Sizable;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;Size=WindowLayout.FitSize(Screen.PrimaryScreen.WorkingArea,new Size(720,600));
        TableLayoutPanel shell=new TableLayoutPanel();shell.Dock=DockStyle.Fill;shell.ColumnCount=1;shell.RowCount=2;shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));shell.RowStyles.Add(new RowStyle(SizeType.Absolute,160));Controls.Add(shell);
        Panel scroll=new SmoothScrollPanel();scroll.Dock=DockStyle.Fill;scroll.AutoScroll=true;scroll.Margin=Padding.Empty;shell.Controls.Add(scroll,0,0);
        TableLayoutPanel body=new TableLayoutPanel();body.ColumnCount=1;body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));body.AutoSize=true;body.AutoSizeMode=AutoSizeMode.GrowAndShrink;body.Dock=DockStyle.Top;body.Padding=new Padding(16);scroll.Controls.Add(body);
        Action<Control> add=delegate(Control control){int row=body.RowCount++;body.RowStyles.Add(new RowStyle(SizeType.AutoSize));control.Dock=DockStyle.Top;control.Margin=new Padding(0,4,0,8);body.Controls.Add(control,0,row);};
        Label heading=new Label();heading.AutoSize=true;heading.Text="自己登录 → 找到这一项 → 只粘贴值";heading.Font=new Font(Font.FontFamily,15,FontStyle.Bold);add(heading);
        Button open=new Button();open.Text="1  打开 Apple Music 并自行登录";open.Height=34;open.FlatStyle=FlatStyle.Flat;open.Click+=delegate{try{Process.Start(new ProcessStartInfo("https://music.apple.com"){UseShellExecute=true});}catch{MessageBox.Show(this,"请在浏览器自行打开 https://music.apple.com。");}};add(open);
        Label steps=new Label();steps.AutoSize=true;steps.Text="2  按 Ctrl + Shift + I，选择 Application（应用）。\n3  左侧展开 Cookies，选择 https://music.apple.com。\n4  点击 media-user-token，在自己的浏览器中复制完整 Value。\n\n下图只展示位置：敏感值已遮挡，不要从图中复制，也不要复制整行或在控制台执行代码。";add(steps);
        string imagePath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"assets","cookie-guide-redacted.png");
        if(File.Exists(imagePath)) {
            PictureBox image=new PictureBox();image.Height=220;image.SizeMode=PictureBoxSizeMode.Zoom;image.Image=Image.FromFile(imagePath);image.Cursor=Cursors.Hand;image.AccessibleName="脱敏图示：Application、Cookies、music.apple.com、media-user-token；点击放大";
            image.Click+=delegate{using(Form viewer=new Form()){viewer.Text="登录位置图示（敏感值已遮挡）";viewer.AutoScaleDimensions=new SizeF(96,96);viewer.AutoScaleMode=AutoScaleMode.Dpi;viewer.AutoScroll=true;PictureBox full=new PictureBox();full.SizeMode=PictureBoxSizeMode.AutoSize;full.Image=(Image)image.Image.Clone();viewer.Controls.Add(full);viewer.Shown+=delegate{WindowLayout.Fit(viewer,new Size(1000,760));};viewer.DpiChanged+=delegate{WindowLayout.Clamp(viewer);};viewer.ShowDialog(this);full.Image.Dispose();}};add(image);FormClosed+=delegate{image.Image.Dispose();};
            Label caption=new Label();caption.AutoSize=true;caption.Text="点击图示可放大。值必须从你自己的已登录浏览器复制。";add(caption);
        } else {Label placeholder=new Label();placeholder.AutoSize=false;placeholder.Height=100;placeholder.Text="位置图示尚未安装；可按上面的 2—4 步操作。";placeholder.BackColor=Color.FromArgb(240,238,230);placeholder.TextAlign=ContentAlignment.MiddleCenter;add(placeholder);}
        TableLayoutPanel bottom=new TableLayoutPanel();bottom.Dock=DockStyle.Fill;bottom.Padding=new Padding(16,4,16,10);bottom.ColumnCount=2;bottom.RowCount=4;bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,84));bottom.RowStyles.Add(new RowStyle(SizeType.Absolute,24));bottom.RowStyles.Add(new RowStyle(SizeType.Absolute,32));bottom.RowStyles.Add(new RowStyle(SizeType.Absolute,36));bottom.RowStyles.Add(new RowStyle(SizeType.Percent,100));shell.Controls.Add(bottom,0,1);
        Label prompt=new Label();prompt.Text="仅粘贴 media-user-token 的值（内容会隐藏）";prompt.Dock=DockStyle.Fill;bottom.Controls.Add(prompt,0,0);bottom.SetColumnSpan(prompt,2);
        value=new TextBox();value.UseSystemPasswordChar=true;value.MaxLength=32768;value.Dock=DockStyle.Fill;bottom.Controls.Add(value,0,1);
        Button paste=new Button();paste.Text="粘贴";paste.Dock=DockStyle.Fill;paste.Click+=delegate{value.Paste();};bottom.Controls.Add(paste,1,1);
        Label privacy=new Label();privacy.Text="保存后请自行用普通文字覆盖剪贴板；不要把值发到聊天。";privacy.Dock=DockStyle.Fill;privacy.AutoEllipsis=true;privacy.ForeColor=Color.FromArgb(95,99,85);bottom.Controls.Add(privacy,0,2);bottom.SetColumnSpan(privacy,2);
        FlowLayoutPanel actions=new FlowLayoutPanel();actions.Dock=DockStyle.Fill;actions.FlowDirection=FlowDirection.RightToLeft;bottom.Controls.Add(actions,0,3);bottom.SetColumnSpan(actions,2);
        Button back=new Button();back.Text="返回";back.Size=new Size(84,30);back.Click+=delegate{Close();};actions.Controls.Add(back);
        save=new Button();save.Text="保存登录";save.Size=new Size(104,30);save.Click+=SaveValue;actions.Controls.Add(save);
        scroll.ClientSizeChanged+=delegate{body.Width=scroll.ClientSize.Width;foreach(Control c in body.Controls)if(c is Label)c.MaximumSize=new Size(Math.Max(180,body.ClientSize.Width-body.Padding.Horizontal-8),0);};
        Shown+=delegate{WindowLayout.Fit(this,new Size(720,600));body.Width=scroll.ClientSize.Width;};DpiChanged+=delegate{if(IsHandleCreated&&!IsDisposed)BeginInvoke((MethodInvoker)delegate{WindowLayout.Clamp(this);body.Width=scroll.ClientSize.Width;});};
        FormClosed+=delegate{value.Clear();};
    }
    private void SaveValue(object sender,EventArgs args) { if(saved)return;save.Enabled=false;try{string token=InputRules.TokenValue(value.Text);saver(token);saved=true;DialogResult=DialogResult.OK;Close();}catch{MessageBox.Show(this,"未保存：请只粘贴有效的 media-user-token 值，并检查工具目录权限。不会显示输入内容。","登录",MessageBoxButtons.OK,MessageBoxIcon.Information);}finally{value.Clear();if(!saved)save.Enabled=true;} }
    public bool TestSaveAndRepeatedClick() { value.Text="SYNTHETIC_TOKEN_VALUE_FOR_TEST_ONLY";SaveValue(this,EventArgs.Empty);SaveValue(this,EventArgs.Empty);return saved && value.Text.Length==0; }
    public bool TestCancelClears() { value.Text="SYNTHETIC_TOKEN_VALUE_FOR_TEST_ONLY";ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;Location=new Point(-32000,-32000);Show();Close();return !saved && value.Text.Length==0; }
}

internal sealed class CandidatePicker : Form {
    public string SelectedPath;
    public CandidatePicker(List<CookieCandidate> candidates) {
        Text="选择匹配的本机登录文件";ClientSize=new Size(780,340);Font=new Font("Microsoft YaHei UI",9F);BackColor=Color.FromArgb(250,249,245);StartPosition=FormStartPosition.CenterParent;
        ListView list=new ListView();list.View=View.Details;list.FullRowSelect=true;list.MultiSelect=false;list.SetBounds(18,20,744,230);list.Columns.Add("文件",210);list.Columns.Add("所在文件夹",350);list.Columns.Add("修改时间",160);
        foreach(CookieCandidate candidate in candidates){var row=new ListViewItem(Path.GetFileName(candidate.FilePath));row.SubItems.Add(Path.GetDirectoryName(candidate.FilePath));row.SubItems.Add(candidate.Modified.ToString("yyyy-MM-dd HH:mm"));row.Tag=candidate.FilePath;list.Items.Add(row);}Controls.Add(list);
        Label note=new Label();note.Text="只扫描指定目录当前层的候选文本，不读取浏览器或子文件夹。";note.SetBounds(18,265,740,24);Controls.Add(note);
        Button choose=new Button();choose.Text="导入所选文件";choose.SetBounds(592,297,170,30);choose.Click+=delegate{if(list.SelectedItems.Count==1){SelectedPath=Convert.ToString(list.SelectedItems[0].Tag);DialogResult=DialogResult.OK;Close();}};Controls.Add(choose);
    }
}

internal sealed class SongQueueItem {
    public string Id,Url,Title,State="waiting",Progress="—";
    public bool Terminal {get{return State=="success"||State=="error"||State=="cancelled";}}
}
internal sealed class QueueAddResult {public int Added,Invalid,Duplicate,Full;}
internal sealed class SongQueue {
    public const int Limit=100;
    public readonly List<SongQueueItem> Items=new List<SongQueueItem>();
    public SongQueueItem Current {get;private set;}
    public bool HasWaiting {get{foreach(var item in Items)if(item.State=="waiting")return true;return false;}}
    public QueueAddResult AddLines(string text) {
        var result=new QueueAddResult();
        if(text.Length>65536){result.Invalid=1;return result;}
        foreach(string line in text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)) {
            if(String.IsNullOrWhiteSpace(line))continue;
            string id,url;try{url=InputRules.SongUrl(line.Trim(),out id);}catch{result.Invalid++;continue;}
            bool duplicate=false;foreach(var item in Items)if(item.Id==id){duplicate=true;break;}
            if(duplicate){result.Duplicate++;continue;}
            if(Items.Count>=Limit){result.Full++;continue;}
            Items.Add(new SongQueueItem{Id=id,Url=url,Title=url});result.Added++;
        }
        return result;
    }
    public SongQueueItem TakeNext() {if(Current!=null)return null;foreach(var item in Items)if(item.State=="waiting"){Current=item;item.State="metadata";return item;}return null;}
    public void Finish(string state) {if(Current==null)return;if(state!="success"&&state!="error"&&state!="cancelled")throw new ArgumentException("state");Current.State=state;Current.Progress=state=="success"?"100%":"—";Current=null;}
    public void CancelWaiting() {foreach(var item in Items)if(item.State=="waiting"){item.State="cancelled";item.Progress="—";}}
    public void ClearFinished() {Items.RemoveAll(delegate(SongQueueItem item){return item.Terminal;});}
}

internal sealed class MainWindow : Form {
    private readonly string root, jobsRoot;
    private string output, activeOutput;
    private readonly ToolSettings settings;
    private readonly CredentialVault vault;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer();
    private readonly BusyGate gate = new BusyGate();
    private TextBox link; private Label login, status, detail, progressPercent;
    private Panel bodySurface; private TableLayoutPanel footerSurface;
    private string currentTitle;
    private readonly SongQueue queue=new SongQueue();
    private ListView queueView;
    private Button addQueue,clearQueue;
    private bool queueRunning,stopQueue;
    private readonly Dictionary<SongQueueItem,ListViewItem> queueRows=new Dictionary<SongQueueItem,ListViewItem>();
    private Button download, cancel, import, clear, guide, scan, changeFolder; private Label destinationLabel; private CheckBox remember;
    private ProgressBar progress; private System.Windows.Forms.Timer timer;
    private Process process; private KillJob killJob; private string jobFolder, sessionToken;
    private bool cancelRequested; private DateTime started;
    private string previousPhase = "";

    public MainWindow(string folder) {
        root=folder;jobsRoot=Path.Combine(root,"private","jobs");vault=new CredentialVault(Path.Combine(root,"private"));settings=ToolSettings.Load(root);output=settings.OutputFolder;
        Text="Apple Music AAC 下载器";Font=new Font("Microsoft YaHei UI",9.5F);AutoScaleDimensions=new SizeF(96F,96F);AutoScaleMode=AutoScaleMode.Dpi;
        BackColor=Color.FromArgb(250,249,245);ForeColor=Color.FromArgb(48,53,43);FormBorderStyle=FormBorderStyle.Sizable;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;
        Size=WindowLayout.FitSize(Screen.PrimaryScreen.WorkingArea,new Size(720,560));
        TableLayoutPanel shell=new TableLayoutPanel();shell.Dock=DockStyle.Fill;shell.ColumnCount=1;shell.RowCount=2;shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));Controls.Add(shell);
        bodySurface=new SmoothScrollPanel();bodySurface.Dock=DockStyle.Fill;bodySurface.AutoScroll=true;bodySurface.Margin=Padding.Empty;shell.Controls.Add(bodySurface,0,0);
        TableLayoutPanel content=Stack(BackColor);content.Padding=new Padding(18,14,18,8);bodySurface.Controls.Add(content);
        Label heading=BodyLabel("Apple Music 下载队列",true);heading.Font=new Font(Font.FontFamily,18,FontStyle.Bold);Row(content,heading);
        Row(content,BodyLabel("AAC 音频 · 按歌曲名保存 · 已有文件不覆盖",false));
        TableLayoutPanel card1=Card();Row(content,card1);Row(card1,BodyLabel("01  歌曲链接 · 每行一首",true));
        link=new TextBox();link.AccessibleName="Apple Music 歌曲链接，每行一首";link.Multiline=true;link.AcceptsReturn=true;link.ScrollBars=ScrollBars.Vertical;link.MaxLength=65536;link.Height=70;link.Dock=DockStyle.Top;link.Font=new Font(Font.FontFamily,10.5F);link.Margin=new Padding(0,5,0,10);Row(card1,link);
        FlowLayoutPanel queueButtons=ButtonFlow();addQueue=FlowButton("加入队列",112);addQueue.Click+=delegate{AddInput();};queueButtons.Controls.Add(addQueue);clearQueue=FlowButton("清除已结束项",140);clearQueue.Click+=delegate{queue.ClearFinished();RefreshQueue();};queueButtons.Controls.Add(clearQueue);Row(card1,queueButtons);
        Row(card1,BodyLabel("可直接下载一首，或粘贴多行后加入队列；默认逐首下载。",false));
        queueView=new ListView();queueView.View=View.Details;queueView.FullRowSelect=true;queueView.HideSelection=false;queueView.Height=160;queueView.AccessibleName="下载队列：歌曲、状态、进度";queueView.Columns.Add("歌曲 / 待解析链接",340);queueView.Columns.Add("状态",88);queueView.Columns.Add("进度",90);queueView.ClientSizeChanged+=delegate{ResizeQueueColumns();};Row(card1,queueView);
        destinationLabel=BodyLabel("保存至  "+output,false);Row(card1,destinationLabel);
        FlowLayoutPanel folders=ButtonFlow();changeFolder=FlowButton("更改保存位置…",124);changeFolder.Click+=ChangeDestination;folders.Controls.Add(changeFolder);
        Button open=FlowButton("打开文件夹",112);open.Click+=delegate{try{Directory.CreateDirectory(output);Process.Start("explorer.exe",Quote(output));}catch{SetStatus("无法打开文件夹，请检查目录权限。",true);}};folders.Controls.Add(open);Row(card1,folders);
        TableLayoutPanel card2=Card();Row(content,card2);Row(card2,BodyLabel("02  登录与隐私",true));
        FlowLayoutPanel loginButtons=ButtonFlow();guide=FlowButton("登录指引",104);guide.Click+=delegate{using(LoginGuide dialog=new LoginGuide(StoreToken)){dialog.ShowDialog(this);}};loginButtons.Controls.Add(guide);
        import=FlowButton("导入文件…",108);import.Click+=ImportLogin;loginButtons.Controls.Add(import);
        scan=FlowButton("扫描匹配…",108);scan.Click+=ScanLogin;loginButtons.Controls.Add(scan);
        clear=FlowButton("清除登录",96);clear.Click+=ClearLogin;loginButtons.Controls.Add(clear);Row(card2,loginButtons);
        remember=new CheckBox();remember.AutoSize=true;remember.Dock=DockStyle.Top;remember.Text="下次保存时加密记住登录";remember.Checked=true;remember.Margin=new Padding(0,6,0,4);Row(card2,remember);
        Row(card2,BodyLabel("取消勾选不会删除已有登录；需移除请点“清除登录”。",false));
        login=BodyLabel(vault.Exists?"已保存加密登录；仅在点击下载时使用。":"首次使用请打开“登录指引”，粘贴你自己的 Cookie 值。",false);Row(card2,login);
        Label privacy=BodyLabel("不读取浏览器凭据；任务结束或取消后删除临时副本。",false);privacy.ForeColor=Color.FromArgb(103,108,95);Row(card2,privacy);
        footerSurface=new TableLayoutPanel();footerSurface.AutoSize=true;footerSurface.AutoSizeMode=AutoSizeMode.GrowAndShrink;footerSurface.MinimumSize=new Size(0,128);footerSurface.Dock=DockStyle.Fill;footerSurface.Padding=new Padding(18,6,18,8);footerSurface.Margin=Padding.Empty;footerSurface.ColumnCount=1;footerSurface.RowCount=3;footerSurface.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));footerSurface.RowStyles.Add(new RowStyle(SizeType.AutoSize));footerSurface.RowStyles.Add(new RowStyle(SizeType.AutoSize));footerSurface.RowStyles.Add(new RowStyle(SizeType.AutoSize));shell.Controls.Add(footerSurface,0,1);
        TableLayoutPanel actions=new TableLayoutPanel();actions.Dock=DockStyle.Fill;actions.ColumnCount=3;actions.AutoSize=true;actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));footerSurface.Controls.Add(actions,0,0);
        download=FlowButton("开始下载",124);download.Dock=DockStyle.Fill;download.BackColor=Color.FromArgb(91,108,76);download.ForeColor=Color.White;download.Click+=StartDownload;actions.Controls.Add(download,0,0);
        cancel=FlowButton("取消全部",80);cancel.Dock=DockStyle.Fill;cancel.Enabled=false;cancel.Click+=delegate{CancelQueue();};actions.Controls.Add(cancel,1,0);
        status=new Label();status.Text="准备就绪";status.AutoEllipsis=true;status.Dock=DockStyle.Fill;status.TextAlign=ContentAlignment.MiddleLeft;status.Margin=new Padding(8,0,0,0);actions.Controls.Add(status,2,0);
        TableLayoutPanel meter=new TableLayoutPanel();meter.Dock=DockStyle.Fill;meter.AutoSize=true;meter.AutoSizeMode=AutoSizeMode.GrowAndShrink;meter.MinimumSize=new Size(0,34);meter.Margin=Padding.Empty;meter.Padding=Padding.Empty;meter.ColumnCount=2;meter.RowCount=1;meter.RowStyles.Add(new RowStyle(SizeType.AutoSize));meter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));meter.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));footerSurface.Controls.Add(meter,0,1);
        progress=new ProgressBar();progress.Dock=DockStyle.Fill;progress.Margin=new Padding(0,3,8,3);progress.Style=ProgressBarStyle.Continuous;meter.Controls.Add(progress,0,0);
        progressPercent=new Label();progressPercent.AutoSize=true;progressPercent.Text="—";progressPercent.Margin=Padding.Empty;progressPercent.Dock=DockStyle.Fill;progressPercent.TextAlign=ContentAlignment.MiddleRight;meter.Controls.Add(progressPercent,1,0);
        detail=new Label();detail.Text="每项只接受一首歌；读取信息时不显示虚假百分比。";detail.AutoSize=true;detail.MaximumSize=new Size(10000,0);detail.Dock=DockStyle.Fill;detail.Margin=new Padding(0,4,0,4);detail.AutoEllipsis=true;detail.TextAlign=ContentAlignment.MiddleLeft;detail.ForeColor=Color.FromArgb(103,108,95);footerSurface.Controls.Add(detail,0,2);
        ((SmoothScrollPanel)bodySurface).RegisterNativeScroller(link);((SmoothScrollPanel)bodySurface).RegisterNativeScroller(queueView);
        bodySurface.ClientSizeChanged+=delegate{content.Width=Math.Max(200,bodySurface.ClientSize.Width);WrapLabels(content);ResizeQueueColumns();};
        timer=new System.Windows.Forms.Timer();timer.Interval=250;timer.Tick+=PollJob;FormClosing+=ClosingWindow;
        Shown+=delegate{WindowLayout.Fit(this,new Size(720,560));content.Width=bodySurface.ClientSize.Width;WrapLabels(content);CleanupStaleJobs();};
        DpiChanged+=delegate{if(!IsDisposed&&IsHandleCreated)BeginInvoke((MethodInvoker)delegate{WindowLayout.Clamp(this);content.Width=bodySurface.ClientSize.Width;WrapLabels(content);ResizeQueueColumns();});};
    }
    private TableLayoutPanel Stack(Color color) {TableLayoutPanel panel=new TableLayoutPanel();panel.ColumnCount=1;panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));panel.AutoSize=true;panel.AutoSizeMode=AutoSizeMode.GrowAndShrink;panel.Dock=DockStyle.Top;panel.Margin=Padding.Empty;panel.BackColor=color;return panel;}
    private TableLayoutPanel Card() {TableLayoutPanel card=Stack(Color.FromArgb(240,238,230));card.Padding=new Padding(12);card.Margin=new Padding(0,10,0,0);return card;}
    private static void Row(TableLayoutPanel panel,Control control) {int row=panel.RowCount++;panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));control.Dock=DockStyle.Top;panel.Controls.Add(control,0,row);}
    private Label BodyLabel(string text,bool bold) {Label label=new Label();label.Text=text;label.AutoSize=true;label.Dock=DockStyle.Top;label.Margin=new Padding(0,3,0,5);label.BackColor=Color.Transparent;if(bold)label.Font=new Font(Font.FontFamily,10,FontStyle.Bold);return label;}
    private FlowLayoutPanel ButtonFlow() {FlowLayoutPanel flow=new FlowLayoutPanel();flow.AutoSize=true;flow.WrapContents=true;flow.Dock=DockStyle.Top;flow.Margin=new Padding(0,4,0,4);return flow;}
    private Button FlowButton(string text,int width) {Button button=new Button();button.Text=text;button.AutoSize=true;button.AutoSizeMode=AutoSizeMode.GrowAndShrink;button.MinimumSize=new Size(width,34);button.Padding=new Padding(8,4,8,4);button.Margin=new Padding(0,0,8,4);button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderColor=Color.FromArgb(196,198,183);button.BackColor=Color.FromArgb(250,249,245);button.ForeColor=Color.FromArgb(48,53,43);button.Cursor=Cursors.Hand;button.AccessibleName=text;return button;}
    private static void WrapLabels(Control parent) {foreach(Control control in parent.Controls){if(control is Label || control is CheckBox)control.MaximumSize=new Size(Math.Max(140,parent.ClientSize.Width-parent.Padding.Horizontal-8),0);if(control.HasChildren)WrapLabels(control);}}
    private static string Quote(string value) { if (value.IndexOf('"') >= 0) throw new Exception("path"); return "\"" + value + "\""; }
    private void SetStatus(string text, bool error) { status.Text = text; status.ForeColor = error ? Color.FromArgb(175,35,55) : Color.FromArgb(35,75,65); }
    private void SetBusy(bool busy) { download.Enabled = !busy; import.Enabled = !busy; clear.Enabled = !busy; guide.Enabled=!busy;scan.Enabled=!busy;changeFolder.Enabled=!busy;remember.Enabled = !busy; link.Enabled = true; cancel.Enabled = busy||queue.HasWaiting; progress.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous; if (!busy) {progress.Value = 0;progressPercent.Text="—";} }
    private void StoreToken(string token) { if(remember.Checked)vault.Save(token);sessionToken=token;login.Text=remember.Checked?"已使用 Windows 当前用户加密保存，仅向 Apple 认证。":(vault.Exists?"新值仅临时使用；旧加密登录仍保留，可点“清除登录”移除。":"已临时导入，关闭工具后需要重新提供。");SetStatus("登录已保存，可以开始下载。",false); }
    private void ImportPath(string path) { try{StoreToken(InputRules.CookieToken(path));}catch{SetStatus("导入失败：请检查 Apple Music Cookie 格式及目录权限。",true);} }
    private void ChangeDestination(object sender,EventArgs args) { using(FolderBrowserDialog dialog=new FolderBrowserDialog()){dialog.Description="选择音频保存文件夹，已有文件不会被覆盖";dialog.SelectedPath=output;if(dialog.ShowDialog(this)!=DialogResult.OK)return;try{ToolSettings.CheckWritable(dialog.SelectedPath);settings.OutputFolder=Path.GetFullPath(dialog.SelectedPath);settings.Save(root);output=settings.OutputFolder;destinationLabel.Text="保存至  "+output;SetStatus("保存位置已更改。",false);}catch{SetStatus("无法使用该文件夹，请检查访问权限。",true);}} }
    private void ScanLogin(object sender,EventArgs args) {
        if(!gate.Enter())return;
        string chosen;
        using(FolderBrowserDialog dialog=new FolderBrowserDialog()){dialog.Description="选择你保存 Cookies 文本的下载文件夹；只扫描当前层，不进入子文件夹或浏览器目录";if(!String.IsNullOrEmpty(settings.ScanFolder))dialog.SelectedPath=settings.ScanFolder;if(dialog.ShowDialog(this)!=DialogResult.OK){gate.Leave();return;}chosen=dialog.SelectedPath;}
        if(!CookieScan.AllowedFolder(chosen)){SetStatus("请选择自己拥有的 Download(s)/下载目录；不扫描 AppData 或浏览器目录。",true);gate.Leave();return;}
        settings.ScanFolder=chosen;try{settings.Save(root);}catch{SetStatus("无法保存扫描位置。",true);gate.Leave();return;}
        string imports=Path.Combine(root,"import");Directory.CreateDirectory(imports);SetBusy(true);cancel.Enabled=false;addQueue.Enabled=false;clearQueue.Enabled=false;SetStatus("正在匹配指定目录中的 Cookies 文本…",false);
        BackgroundWorker worker=new BackgroundWorker();worker.DoWork+=delegate(object s,DoWorkEventArgs e){e.Result=CookieScan.Find(imports,chosen);};
        worker.RunWorkerCompleted+=delegate(object s,RunWorkerCompletedEventArgs e){if(IsDisposed||Disposing){gate.Leave();worker.Dispose();return;}addQueue.Enabled=true;clearQueue.Enabled=true;SetBusy(false);gate.Leave();if(e.Error!=null){SetStatus("扫描未完成，请检查目录权限。",true);return;}var candidates=(List<CookieCandidate>)e.Result;if(candidates.Count==0){SetStatus("未找到匹配文件，可直接使用“登录指引”。",false);return;}using(CandidatePicker picker=new CandidatePicker(candidates)){if(picker.ShowDialog(this)==DialogResult.OK)ImportPath(picker.SelectedPath);}worker.Dispose();};worker.RunWorkerAsync();
    }
    private void ImportLogin(object sender, EventArgs args) {
        using (OpenFileDialog dialog = new OpenFileDialog()) {
            dialog.Title = "选择你在本机准备的 Apple Music Cookies 文件（不会删除源文件）"; dialog.Filter = "Cookie 文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            ImportPath(dialog.FileName);
        }
    }
    private void ClearLogin(object sender, EventArgs args) {
        if (MessageBox.Show(this, "清除本工具保存的登录？下次下载需要重新导入。不会删除你选择的源文件。", "清除登录", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { vault.Clear(); sessionToken = null; login.Text = "登录已清除，需要时请重新导入。"; SetStatus("已清除本工具的登录。", false); } catch { SetStatus("清除失败，请检查工具目录权限。", true); }
    }
    private void ResizeQueueColumns() {if(queueView==null||queueView.Columns.Count<3)return;float scale=DeviceDpi/96F;int state=(int)(96*scale),pct=(int)(100*scale);queueView.Columns[1].Width=state;queueView.Columns[2].Width=pct;queueView.Columns[0].Width=Math.Max((int)(160*scale),queueView.ClientSize.Width-state-pct-6);}
    private static string QueueState(string state) {switch(state){case "waiting":return "等待";case "metadata":return "读取信息";case "downloading":return "下载中";case "processing":return "处理中";case "verifying":return "校验中";case "success":return "完成";case "error":return "失败";case "cancelled":return "已取消";default:return "等待";}}
    private void RefreshQueue() {
        queueView.BeginUpdate();
        var removed=new List<SongQueueItem>();foreach(var pair in queueRows)if(!queue.Items.Contains(pair.Key)){queueView.Items.Remove(pair.Value);removed.Add(pair.Key);}foreach(var item in removed)queueRows.Remove(item);
        foreach(var item in queue.Items){ListViewItem row;if(!queueRows.TryGetValue(item,out row)){row=new ListViewItem(new[]{item.Title,QueueState(item.State),item.Progress});queueView.Items.Add(row);queueRows[item]=row;}row.Text=item.Title;row.SubItems[1].Text=QueueState(item.State);row.SubItems[2].Text=item.Progress;}
        queueView.EndUpdate();ResizeQueueColumns();
    }
    private void AddInput() {
        QueueAddResult result=queue.AddLines(link.Text);if(result.Added>0)link.Clear();RefreshQueue();if(process==null)cancel.Enabled=queue.HasWaiting;
        string message="加入 "+result.Added+" 首";if(result.Duplicate>0)message+="；重复跳过 "+result.Duplicate;if(result.Invalid>0)message+="；无效链接 "+result.Invalid;if(result.Full>0)message+="；队列最多 "+SongQueue.Limit+" 项";
        SetStatus(result.Added==0&&result.Invalid>0?"链接无效：仅接受 music.apple.com 的歌曲链接。":message+"。",result.Invalid>0);
    }
    private void StartDownload(object sender,EventArgs args) {
        if(queueRunning||process!=null)return;
        if(!String.IsNullOrWhiteSpace(link.Text))AddInput();
        if(!queue.HasWaiting)return;
        stopQueue=false;queueRunning=true;StartNext();
    }
    private void ContinueQueue() {if(IsDisposed||Disposing)return;if(queueRunning&&!stopQueue&&queue.HasWaiting){BeginInvoke((MethodInvoker)StartNext);}else{queueRunning=false;SetBusy(false);}}
    private void CancelQueue() {stopQueue=true;queue.CancelWaiting();RefreshQueue();if(process!=null)CancelJob();else{queueRunning=false;SetBusy(false);SetStatus("等待项已取消。",false);}}
    private void StartNext() {
        if(!queueRunning||stopQueue||process!=null)return;
        if(!queue.HasWaiting){queueRunning=false;SetBusy(false);SetStatus("队列已结束。",false);return;}
        if (!gate.Enter()) return;
        try {
            string token = sessionToken;
            if (token == null && vault.Exists) token = vault.Load();
            if (String.IsNullOrEmpty(token)) {queueRunning=false;SetBusy(false);SetStatus("请先使用登录指引或导入登录文件，再开始队列。",true);gate.Leave();return;}
            SongQueueItem next=queue.TakeNext();if(next==null){gate.Leave();return;}RefreshQueue();
            string canonical=next.Url,id=next.Id;
            var config = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(root,"tool-config.json")));
            string python = Path.GetFullPath(Path.Combine(root, Convert.ToString(config["python"])));
            if (!File.Exists(python)) throw new Exception("dependency");
            CredentialVault.PrivateDirectory(Path.Combine(root,"private")); Directory.CreateDirectory(jobsRoot);
            jobFolder = Path.Combine(jobsRoot, Guid.NewGuid().ToString("N")); CredentialVault.PrivateDirectory(jobFolder);
            string cookies = "# Netscape HTTP Cookie File\n.music.apple.com\tTRUE\t/\tTRUE\t0\tmedia-user-token\t" + token + "\n";
            File.WriteAllText(Path.Combine(jobFolder,"cookies.txt"), cookies, new UTF8Encoding(false));
            activeOutput=output;
            File.WriteAllText(Path.Combine(jobFolder,"job.json"), json.Serialize(new Dictionary<string,object> {{"url",canonical},{"song_id",id},{"destination",activeOutput}}), new UTF8Encoding(false));
            token = null; cookies = null;
            killJob = new KillJob();
            ProcessStartInfo start = new ProcessStartInfo(python, "-I " + Quote(Path.Combine(root,"backend.py")) + " " + Quote(jobFolder));
            start.WorkingDirectory = root; start.UseShellExecute = false; start.CreateNoWindow = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            process = new Process(); process.StartInfo = start;
            process.OutputDataReceived += delegate { }; process.ErrorDataReceived += delegate { };
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); killJob.Assign(process);
            File.WriteAllText(Path.Combine(jobFolder,"go.flag"), "start");
            cancelRequested = false; previousPhase = ""; currentTitle=null; started = DateTime.UtcNow; SetBusy(true); SetStatus("正在连接 Apple 并读取单曲信息…", false); detail.Text = "临时登录副本仅用于当前任务。"; timer.Start();
        } catch (Exception error) {
            bool hadItem=queue.Current!=null;StopProcess();bool cleaned=CleanupJob();queue.Finish("error");RefreshQueue();gate.Leave();
            if(!hadItem){queueRunning=false;SetBusy(false);SetStatus("无法读取登录资料，队列未启动；请清除后重新登录。",true);return;}
            if(!cleaned){queueRunning=false;SetBusy(false);SetStatus("临时文件清理失败，队列已暂停；关闭后检查 private\\jobs。",true);return;}
            SetStatus(error.Message == "invalid_url" ? "链接无效：请使用 music.apple.com 的单曲链接。" : "无法启动任务：请检查登录、目录权限及工具运行环境。", true);
            ContinueQueue();
        }
    }
    private void CancelJob() { if (process == null || cancelRequested) return; cancelRequested = true; cancel.Enabled = false; if (killJob != null) { killJob.Dispose(); killJob = null; } SetStatus("正在取消并清理临时登录文件…", false); }
    private void PollJob(object sender, EventArgs args) {
        if (process == null) return;
        if ((DateTime.UtcNow - started).TotalMinutes > 20) CancelJob();
        Dictionary<string, object> state = ReadState();
        if (state != null && !cancelRequested) {
            string phase = Convert.ToString(state["phase"]);
            if (phase != previousPhase) {
                previousPhase = phase;
                if (phase == "metadata") SetStatus("正在读取单曲信息…", false);
                else if (phase == "downloading") SetStatus("正在下载 AAC 音频…", false);
                else if (phase == "processing") SetStatus("传输完成，正在整理音频…", false);
                else if (phase == "verifying") SetStatus("正在检查格式、完整时长和解码…", false);
                if (state.ContainsKey("title")) currentTitle=Convert.ToString(state["title"]);
            }
        }
        if(state!=null&&!cancelRequested){
            if(state.ContainsKey("title"))currentTitle=Convert.ToString(state["title"]);
            if(queue.Current!=null){if(currentTitle!=null)queue.Current.Title=currentTitle;string phase=Convert.ToString(state["phase"]);if(phase=="metadata"||phase=="downloading"||phase=="processing"||phase=="verifying")queue.Current.State=phase;}
            UpdateProgress(state);if(queue.Current!=null)queue.Current.Progress=progressPercent.Text;RefreshQueue();
        }
        if (!process.HasExited) return;
        timer.Stop(); state=ReadState();int exitCode=process.ExitCode;bool clean=CleanupJob();
        if(killJob!=null){killJob.Dispose();killJob=null;}process.Dispose();process=null;gate.Leave();
        bool success=state!=null&&Convert.ToString(state["phase"])=="success"&&exitCode==0;
        if(queue.Current!=null&&state!=null&&state.ContainsKey("title"))queue.Current.Title=Convert.ToString(state["title"]);
        queue.Finish(cancelRequested?"cancelled":success?"success":"error");RefreshQueue();
        if(!clean){queueRunning=false;SetBusy(false);SetStatus("临时文件清理失败，队列已暂停；关闭后检查 private\\jobs。",true);return;}
        if(cancelRequested){SetStatus("当前项已取消，临时登录文件已删除。",false);detail.Text="已完成的音频保留。";}
        else if(success){SetStatus("当前项完成：AAC 与完整解码校验通过。",false);detail.Text=Path.GetFileName(Convert.ToString(state["file"]));}
        else{string category=state!=null&&state.ContainsKey("category")?Convert.ToString(state["category"]):"local_error";SetStatus(ErrorText(category),true);detail.Text="该项失败；继续下一项，不显示原始账户或媒体地址。";}
        ContinueQueue();
        if(success&&!queueRunning){progress.Style=ProgressBarStyle.Continuous;progress.Value=100;progressPercent.Text="100%";}
    }
    private static string BytesText(double value) {return value>=1048576?(value/1048576).ToString("0.0")+" MiB":value>=1024?(value/1024).ToString("0.0")+" KiB":value.ToString("0")+" B";}
    private void UpdateProgress(Dictionary<string,object> state) {
        string phase=Convert.ToString(state["phase"]);
        if(phase=="downloading"&&state.ContainsKey("percent")) {
            double percent=Math.Max(0,Math.Min(99.9,Convert.ToDouble(state["percent"])));progress.Style=ProgressBarStyle.Continuous;progress.Value=Math.Max(0,Math.Min(99,(int)percent));progressPercent.Text=(state.ContainsKey("estimated")?"约 ":"")+percent.ToString("0.0")+"%";
        } else {progress.Style=ProgressBarStyle.Marquee;progressPercent.Text=phase=="verifying"?"校验中":phase=="processing"?"整理中":"—";}
        if(state.ContainsKey("bytes_done")) {
            string counters="已传输 "+BytesText(Convert.ToDouble(state["bytes_done"]));
            if(state.ContainsKey("bytes_total"))counters+=" / "+(state.ContainsKey("estimated")?"预计 ":"")+BytesText(Convert.ToDouble(state["bytes_total"]));else counters+="（总大小未知）";
            if(state.ContainsKey("fragments_total"))counters+=" · 分片 "+state["fragments_done"]+" / "+state["fragments_total"];
            detail.Text=counters;
        } else detail.Text=currentTitle??(phase=="metadata"?"正在读取曲目信息；尚未开始传输音频。":phase=="processing"?"音频已传输，正在整理；尚未完成验证。":"正在进行音频完整性检查。");
    }
    private Dictionary<string,object> ReadState() { try { string file = Path.Combine(jobFolder,"status.json"); return File.Exists(file) ? json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file)) : null; } catch { return null; } }
    private static string ErrorText(string code) {
        switch(code) {
            case "cookie_invalid": return "登录文件无效，请重新导入正确的 Apple Music Cookie。";
            case "authentication": return "登录失效或订阅不可用，请清除登录后重新导入。";
            case "network": return "连接 Apple 失败，请检查网络后重试。";
            case "blocked_destination": return "请求地址不在 Apple 白名单内，任务已停止。";
            case "audio_incomplete": case "audio_invalid": return "音频完整性检查未通过，没有保存为完成文件。";
            case "save_failed": case "collision_limit": return "音频保存失败，请检查目标目录权限或重名文件。";
            case "cookie_cleanup": return "临时登录副本清理失败，请检查工具目录。";
            case "unexpected_media": return "链接未解析为唯一单曲，任务已停止。";
            default: return "下载未成功：可能是曲目、登录或服务限制，可重新导入后重试。";
        }
    }
    private void StopProcess() { if (killJob != null) { killJob.Dispose(); killJob = null; } if (process != null) { try { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } } catch { } process.Dispose(); process = null; } }
    private bool CleanupJob() {
        if (jobFolder == null) return true;
        try { if(!String.IsNullOrEmpty(activeOutput)){string partial=Path.Combine(activeOutput,".applemusic-"+Path.GetFileName(jobFolder)+".part");if(File.Exists(partial))File.Delete(partial);} SafeDeleteJob(jobFolder); jobFolder = null;activeOutput=null; return true; } catch { return false; }
    }
    private void SafeDeleteJob(string path) {
        string full = Path.GetFullPath(path); string expected = Path.GetFullPath(jobsRoot).TrimEnd('\\') + "\\";
        if (!full.StartsWith(expected, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(full) != Path.GetFullPath(jobsRoot) || !Regex.IsMatch(Path.GetFileName(full), "^[0-9a-f]{32}$")) throw new Exception("cleanup");
        if (Directory.Exists(full)) { foreach (string sub in Directory.GetDirectories(full,"*",SearchOption.AllDirectories)) if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) throw new Exception("cleanup"); if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new Exception("cleanup"); Directory.Delete(full,true); }
    }
    private void CleanupStaleJobs() {
        if (!Directory.Exists(jobsRoot)) return;
        foreach (string dir in Directory.GetDirectories(jobsRoot)) { try { SafeDeleteJob(dir); } catch { SetStatus("发现未清理的临时目录，请检查 private\\jobs。", true); } }
    }
    private void ClosingWindow(object sender, FormClosingEventArgs args) {
        if (((process != null && !process.HasExited)||queue.HasWaiting) && MessageBox.Show(this,"队列尚未结束，取消当前项及等待项并退出？", "退出",MessageBoxButtons.YesNo,MessageBoxIcon.Question) != DialogResult.Yes) { args.Cancel = true; return; }
        stopQueue=true;queueRunning=false;queue.CancelWaiting();timer.Stop(); StopProcess(); CleanupJob(); sessionToken = null;
    }
    public void RenderPreview(string path) { ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Location = new Point(-32000,-32000); Show(); Application.DoEvents(); using(Bitmap bitmap = new Bitmap(Width,Height)) { DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height)); bitmap.Save(path); } Hide(); }
    public bool TestInvalidLink() { link.Text = "https://example.invalid/song/1"; StartDownload(this,EventArgs.Empty); return process == null && status.Text.StartsWith("链接无效"); }
    public bool TestRememberChoiceIsExplicit() { vault.Save("SYNTHETIC_PERSISTED_LOGIN_ONLY");remember.Checked=false;StoreToken("SYNTHETIC_TEMPORARY_LOGIN_ONLY");bool ok=vault.Load()=="SYNTHETIC_PERSISTED_LOGIN_ONLY" && login.Text.Contains("旧加密登录仍保留") && remember.Text.Contains("下次保存");vault.Clear();sessionToken=null;return ok; }
}

internal static class Program {
    [STAThread] public static void Main(string[] args) {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string root = AppDomain.CurrentDomain.BaseDirectory;
        if (args.Length > 0 && args[0] == "--self-test") {
            string test = Path.Combine(root,"self-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(test);
            bool vaultOk=false, gateOk=false, cancelOk=false, uiOk=false, sourceOk=false, scanOk=false, folderOk=false, pasteOk=false, backOk=false, boundaryOk=false, rememberOk=false;
            try {
                string source = Path.Combine(test,"source.txt"); File.WriteAllText(source,"# Netscape HTTP Cookie File\n.music.apple.com\tTRUE\t/\tTRUE\t0\tmedia-user-token\tSYNTHETIC_TEST_ONLY\n");
                string synthetic = InputRules.CookieToken(source);
                CredentialVault v = new CredentialVault(Path.Combine(test,"vault")); v.Save(synthetic); vaultOk = v.Load() == synthetic; v.Clear(); vaultOk = vaultOk && !v.Exists; sourceOk = File.Exists(source);
                BusyGate gate = new BusyGate(); gateOk = gate.Enter() && !gate.Enter(); gate.Leave(); gateOk = gateOk && gate.Enter(); gate.Leave();
                string imports=Path.Combine(test,"import");Directory.CreateDirectory(imports);File.Copy(source,Path.Combine(imports,"cookies-good.txt"));File.Copy(source,Path.Combine(imports,"cookies-template.txt"));
                string nested=Path.Combine(imports,"nested");Directory.CreateDirectory(nested);File.Copy(source,Path.Combine(nested,"cookies-hidden.txt"));
                var found=CookieScan.Find(imports,imports);scanOk=found.Count==1 && Path.GetFileName(found[0].FilePath)=="cookies-good.txt" && !CookieScan.CandidateName("cookies-template.txt");
                string downloads=Path.Combine(test,"Downloads");Directory.CreateDirectory(downloads);
                string profile=Path.Combine(downloads,"profile");Directory.CreateDirectory(profile);
                string disguised=Path.Combine(downloads,"unusual-folder");Directory.CreateDirectory(disguised);File.WriteAllText(Path.Combine(disguised,"prefs.js"),"SYNTHETIC_PROFILE_MARKER_ONLY");
                boundaryOk=!CookieScan.PathBoundary(@"D:\AppData",false) && !CookieScan.PathBoundary(@"D:\Downloads\FirefoxPortable\Data\profile",false) && !CookieScan.AllowedFolder(profile) && !CookieScan.AllowedFolder(disguised) && CookieScan.AllowedFolder(downloads) && !CookieScan.PathBoundary(Path.Combine(Path.GetPathRoot(test),"UnrelatedFolder"),false);
                string output=Path.Combine(test,"chosen-output");ToolSettings.CheckWritable(output);var checkSettings=new ToolSettings{OutputFolder=output,ScanFolder=imports};checkSettings.Save(test);folderOk=ToolSettings.Load(test).OutputFolder==output && Directory.GetFiles(output).Length==0;
                int saves=0;using(LoginGuide dialog=new LoginGuide(delegate(string token){saves++;})){pasteOk=dialog.TestSaveAndRepeatedClick() && saves==1;}using(LoginGuide dialog=new LoginGuide(delegate(string token){saves++;})){backOk=dialog.TestCancelClears() && saves==1;}
                var config = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(root,"tool-config.json")));
                string python = Path.GetFullPath(Path.Combine(root,Convert.ToString(config["python"])));
                ProcessStartInfo start = new ProcessStartInfo(python,"-I -c \"import subprocess,sys,time;time.sleep(0.5);p=subprocess.Popen([sys.executable,'-I','-c','import time;time.sleep(30)']);print(p.pid,flush=True);time.sleep(30)\""); start.UseShellExecute=false; start.CreateNoWindow=true; start.RedirectStandardOutput=true;
                using(Process p = Process.Start(start)) { using(KillJob job = new KillJob()) { job.Assign(p); int childId = Int32.Parse(p.StandardOutput.ReadLine()); using(Process child = Process.GetProcessById(childId)) { job.Dispose(); cancelOk = p.WaitForExit(5000) && child.WaitForExit(5000); } } }
                new ToolSettings {OutputFolder=@"C:\Music\AppleMusic",ScanFolder=""}.Save(test);
                using(MainWindow form = new MainWindow(test)) { form.RenderPreview(Path.Combine(root,"ui-preview.png")); uiOk = form.TestInvalidLink();rememberOk=form.TestRememberChoiceIsExplicit(); }
                if(Array.IndexOf(args,"--force-test-failure")>=0)uiOk=false;
            } catch { }
            finally { if (Path.GetFullPath(test).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\self-test-",StringComparison.OrdinalIgnoreCase)) Directory.Delete(test,true); }
            File.WriteAllText(Path.Combine(root,"ui-self-test.json"),new JavaScriptSerializer().Serialize(new { dpapi_roundtrip_and_clear=vaultOk, selected_source_preserved=sourceOk, duplicate_start_gate=gateOk, cancellation_kills_process_tree=cancelOk, invalid_link_and_render=uiOk, bounded_scan_and_template_exclusion=scanOk, destination_change_persist_probe_cleanup=folderOk, masked_save_clear_repeated_click=pasteOk, masked_cancel_clears_without_save=backOk, scan_boundary_rejections=boundaryOk, remember_choice_explicit=rememberOk }));
            Environment.ExitCode=vaultOk&&sourceOk&&gateOk&&cancelOk&&uiOk&&scanOk&&folderOk&&pasteOk&&backOk&&boundaryOk&&rememberOk?0:1;
            return;
        }
        bool created;
        using (Mutex mutex = new Mutex(true,"Local\\AppleMusicAACTool-v1",out created)) {
            if (!created) { MessageBox.Show("工具已打开，请使用现有窗口。", "Apple Music AAC 下载器"); return; }
            Application.Run(new MainWindow(root));
        }
    }
}
