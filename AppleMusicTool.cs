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
        saver=saveAction; Text="登录指引 · 只粘贴你自己的 Cookie 值"; ClientSize=new Size(730,475); Font=new Font("Microsoft YaHei UI",9.5F); BackColor=Color.FromArgb(250,249,245); FormBorderStyle=FormBorderStyle.FixedDialog; MaximizeBox=false; MinimizeBox=false; StartPosition=FormStartPosition.CenterParent;
        Label title=new Label();title.Text="自己登录，复制一次，然后加密保存";title.Font=new Font(Font.FontFamily,16,FontStyle.Bold);title.SetBounds(24,22,680,38);Controls.Add(title);
        Label steps=new Label();steps.Text="1  点击下方按钮，在浏览器中自行登录 Apple Music。\n\n2  按 Ctrl + Shift + I，打开 Application（应用）标签。\n\n3  展开 Storage → Cookies，选择 https://music.apple.com。\n\n4  找到 media-user-token，只复制它的 Value（值）。\n    不要复制整行、其他 Cookie 或在控制台粘贴代码。";steps.SetBounds(26,73,680,210);Controls.Add(steps);
        Button open=new Button();open.Text="打开 Apple Music";open.SetBounds(26,287,180,32);open.FlatStyle=FlatStyle.Flat;open.Click+=delegate{try{Process.Start(new ProcessStartInfo("https://music.apple.com"){UseShellExecute=true});}catch{MessageBox.Show(this,"请在浏览器自行打开 https://music.apple.com。");}};Controls.Add(open);
        Label prompt=new Label();prompt.Text="仅粘贴 media-user-token 的值（输入内容会隐藏）";prompt.SetBounds(26,335,675,24);Controls.Add(prompt);
        value=new TextBox();value.UseSystemPasswordChar=true;value.MaxLength=32768;value.SetBounds(26,363,570,30);Controls.Add(value);
        Button paste=new Button();paste.Text="粘贴";paste.SetBounds(608,361,96,32);paste.Click+=delegate{value.Paste();};Controls.Add(paste);
        save=new Button();save.Text="保存登录";save.SetBounds(488,419,110,34);save.Click+=SaveValue;Controls.Add(save);
        Button back=new Button();back.Text="返回";back.SetBounds(608,419,96,34);back.Click+=delegate{Close();};Controls.Add(back);
        Label privacy=new Label();privacy.Text="只在本机使用。保存后请自行用普通文字覆盖剪贴板。";privacy.SetBounds(26,421,450,40);privacy.ForeColor=Color.FromArgb(95,99,85);Controls.Add(privacy);
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

internal sealed class MainWindow : Form {
    private readonly string root, jobsRoot;
    private string output, activeOutput;
    private readonly ToolSettings settings;
    private readonly CredentialVault vault;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer();
    private readonly BusyGate gate = new BusyGate();
    private TextBox link; private Label login, status, detail;
    private Button download, cancel, import, clear, guide, scan, changeFolder; private Label destinationLabel; private CheckBox remember;
    private ProgressBar progress; private System.Windows.Forms.Timer timer;
    private Process process; private KillJob killJob; private string jobFolder, sessionToken;
    private bool cancelRequested; private DateTime started;
    private string previousPhase = "";

    public MainWindow(string folder) {
        root = folder; jobsRoot = Path.Combine(root, "private", "jobs"); vault = new CredentialVault(Path.Combine(root, "private")); settings=ToolSettings.Load(root);output=settings.OutputFolder;
        Text = "Apple Music AAC 下载器"; ClientSize = new Size(820, 662); MinimumSize = new Size(836, 701);
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9.5F); BackColor = Color.FromArgb(250,249,245); ForeColor=Color.FromArgb(48,53,43);
        Label heading = LabelAt("让音乐，落在你的文件夹里", 28, 25, 760, 40); heading.Font = new Font(Font.FontFamily, 21, FontStyle.Bold);
        LabelAt("Apple Music 单曲  /  AAC 音频  /  自动按曲名命名", 30, 73, 755, 24).ForeColor=Color.FromArgb(103,108,95);
        Panel card1=new Panel();card1.BackColor=Color.FromArgb(240,238,230);card1.SetBounds(24,114,772,180);Controls.Add(card1);card1.SendToBack();
        Panel card2=new Panel();card2.BackColor=Color.FromArgb(240,238,230);card2.SetBounds(24,310,772,208);Controls.Add(card2);card2.SendToBack();
        LabelAt("01  单曲链接", 44, 133, 730, 25).Font=new Font(Font.FontFamily,10,FontStyle.Bold);
        link = new TextBox(); link.AccessibleName="Apple Music 单曲链接";link.SetBounds(44, 169, 732, 34); link.Font = new Font(Font.FontFamily, 11); Controls.Add(link);
        destinationLabel=LabelAt("保存至  "+output, 44, 229, 506, 38);
        changeFolder=ButtonAt("更改…",562,228,84,34);changeFolder.Click+=ChangeDestination;
        Button open = ButtonAt("打开文件夹", 658, 228, 118, 34); open.Click += delegate { try { Directory.CreateDirectory(output); Process.Start("explorer.exe", Quote(output)); } catch { SetStatus("无法打开文件夹，请检查目录权限。", true); } };
        LabelAt("02  登录与隐私",44,328,730,25).Font=new Font(Font.FontFamily,10,FontStyle.Bold);
        guide=ButtonAt("登录指引",44,367,125,36);guide.Click+=delegate{using(LoginGuide dialog=new LoginGuide(StoreToken)){dialog.ShowDialog(this);}};
        import = ButtonAt("导入文件…", 181, 367, 123, 36); import.Click += ImportLogin;
        scan=ButtonAt("扫描匹配…",316,367,125,36);scan.Click+=ScanLogin;
        clear = ButtonAt("清除登录", 453, 367, 110, 36); clear.Click += ClearLogin;
        remember = new CheckBox(); remember.Text = "下次保存时加密记住登录（取消勾选不会删除已有登录）";remember.BackColor=Color.FromArgb(240,238,230); remember.Checked = true; remember.SetBounds(44, 414, 727, 28); Controls.Add(remember);
        login = LabelAt(vault.Exists ? "已保存加密登录；仅在你点击下载时使用。" : "首次使用，点击“登录指引”后粘贴你自己的 Cookie 值即可。", 44, 449, 730, 25);
        LabelAt("不读取浏览器凭据；临时明文副本在任务结束或取消后删除。", 44, 481, 730, 24).ForeColor = Color.FromArgb(103,108,95);
        download = ButtonAt("下载单曲", 24, 540, 170, 46); download.BackColor = Color.FromArgb(91,108,76); download.ForeColor = Color.White; download.FlatStyle = FlatStyle.Flat; download.Click += StartDownload;
        cancel = ButtonAt("取消", 206, 540, 110, 46); cancel.Enabled = false; cancel.Click += delegate { CancelJob(); };
        status = LabelAt("准备就绪", 338, 551, 458, 34);
        progress = new ProgressBar(); progress.SetBounds(24, 608, 772, 7); progress.Style = ProgressBarStyle.Continuous; Controls.Add(progress);
        detail = LabelAt("只接受单曲，重名会自动避让，已有文件不会覆盖。", 24, 630, 772, 25); detail.ForeColor = Color.FromArgb(103,108,95);
        card1.SendToBack();
        card2.SendToBack();
        timer = new System.Windows.Forms.Timer(); timer.Interval = 250; timer.Tick += PollJob;
        FormClosing += ClosingWindow;
        Shown += delegate { CleanupStaleJobs(); };
    }
    private Label LabelAt(string text, int x, int y, int w, int h) { Label label = new Label(); label.Text = text;label.BackColor=((y>=114&&y<294)||(y>=310&&y<518))?Color.FromArgb(240,238,230):BackColor; label.AutoEllipsis = true; label.SetBounds(x,y,w,h); Controls.Add(label); return label; }
    private Button ButtonAt(string text, int x, int y, int w, int h) { Button button = new Button(); button.Text = text; button.SetBounds(x,y,w,h); button.Cursor = Cursors.Hand;button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderColor=Color.FromArgb(196,198,183);button.BackColor=Color.FromArgb(250,249,245);button.ForeColor=Color.FromArgb(48,53,43);button.AccessibleName=text; Controls.Add(button); return button; }
    private static string Quote(string value) { if (value.IndexOf('"') >= 0) throw new Exception("path"); return "\"" + value + "\""; }
    private void SetStatus(string text, bool error) { status.Text = text; status.ForeColor = error ? Color.FromArgb(175,35,55) : Color.FromArgb(35,75,65); }
    private void SetBusy(bool busy) { download.Enabled = !busy; import.Enabled = !busy; clear.Enabled = !busy; guide.Enabled=!busy;scan.Enabled=!busy;changeFolder.Enabled=!busy;remember.Enabled = !busy; link.Enabled = !busy; cancel.Enabled = busy; progress.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous; if (!busy) progress.Value = 0; }
    private void StoreToken(string token) { if(remember.Checked)vault.Save(token);sessionToken=token;login.Text=remember.Checked?"已使用 Windows 当前用户加密保存，仅向 Apple 认证。":(vault.Exists?"新值仅临时使用；旧加密登录仍保留，可点“清除登录”移除。":"已临时导入，关闭工具后需要重新提供。");SetStatus("登录已保存，可以开始下载。",false); }
    private void ImportPath(string path) { try{StoreToken(InputRules.CookieToken(path));}catch{SetStatus("导入失败：请检查 Apple Music Cookie 格式及目录权限。",true);} }
    private void ChangeDestination(object sender,EventArgs args) { using(FolderBrowserDialog dialog=new FolderBrowserDialog()){dialog.Description="选择音频保存文件夹，已有文件不会被覆盖";dialog.SelectedPath=output;if(dialog.ShowDialog(this)!=DialogResult.OK)return;try{ToolSettings.CheckWritable(dialog.SelectedPath);settings.OutputFolder=Path.GetFullPath(dialog.SelectedPath);settings.Save(root);output=settings.OutputFolder;destinationLabel.Text="保存至  "+output;SetStatus("保存位置已更改。",false);}catch{SetStatus("无法使用该文件夹，请检查访问权限。",true);}} }
    private void ScanLogin(object sender,EventArgs args) {
        if(!gate.Enter())return;
        string chosen;
        using(FolderBrowserDialog dialog=new FolderBrowserDialog()){dialog.Description="选择你保存 Cookies 文本的下载文件夹；只扫描当前层，不进入子文件夹或浏览器目录";if(!String.IsNullOrEmpty(settings.ScanFolder))dialog.SelectedPath=settings.ScanFolder;if(dialog.ShowDialog(this)!=DialogResult.OK){gate.Leave();return;}chosen=dialog.SelectedPath;}
        if(!CookieScan.AllowedFolder(chosen)){SetStatus("请选择自己拥有的 Download(s)/下载目录；不扫描 AppData 或浏览器目录。",true);gate.Leave();return;}
        settings.ScanFolder=chosen;try{settings.Save(root);}catch{SetStatus("无法保存扫描位置。",true);gate.Leave();return;}
        string imports=Path.Combine(root,"import");Directory.CreateDirectory(imports);SetBusy(true);cancel.Enabled=false;SetStatus("正在匹配指定目录中的 Cookies 文本…",false);
        BackgroundWorker worker=new BackgroundWorker();worker.DoWork+=delegate(object s,DoWorkEventArgs e){e.Result=CookieScan.Find(imports,chosen);};
        worker.RunWorkerCompleted+=delegate(object s,RunWorkerCompletedEventArgs e){SetBusy(false);gate.Leave();if(e.Error!=null){SetStatus("扫描未完成，请检查目录权限。",true);return;}var candidates=(List<CookieCandidate>)e.Result;if(candidates.Count==0){SetStatus("未找到匹配文件，可直接使用“登录指引”。",false);return;}using(CandidatePicker picker=new CandidatePicker(candidates)){if(picker.ShowDialog(this)==DialogResult.OK)ImportPath(picker.SelectedPath);}worker.Dispose();};worker.RunWorkerAsync();
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
    private void StartDownload(object sender, EventArgs args) {
        if (!gate.Enter()) return;
        try {
            string id; string canonical = InputRules.SongUrl(link.Text, out id);
            string token = sessionToken;
            if (token == null && vault.Exists) token = vault.Load();
            if (String.IsNullOrEmpty(token)) { SetStatus("请先点击“导入登录文件”。", true); gate.Leave(); return; }
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
            cancelRequested = false; previousPhase = ""; started = DateTime.UtcNow; SetBusy(true); SetStatus("正在连接 Apple 并读取单曲信息…", false); detail.Text = "临时登录副本仅用于当前任务。"; timer.Start();
        } catch (Exception error) {
            StopProcess(); CleanupJob(); gate.Leave(); SetBusy(false);
            SetStatus(error.Message == "invalid_url" ? "链接无效：请使用 music.apple.com 的单曲链接。" : "无法启动任务：请检查登录、目录权限及工具运行环境。", true);
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
                else if (phase == "verifying") SetStatus("正在检查格式、完整时长和解码…", false);
                if (state.ContainsKey("title")) detail.Text = Convert.ToString(state["title"]);
            }
        }
        if (!process.HasExited) return;
        timer.Stop(); state = ReadState(); bool clean = CleanupJob();
        if (killJob != null) { killJob.Dispose(); killJob = null; } process.Dispose(); process = null; SetBusy(false); gate.Leave();
        if (!clean) { SetStatus("临时文件清理失败，请关闭工具后检查 private\\jobs 文件夹。", true); return; }
        if (cancelRequested) { SetStatus("任务已取消，临时登录副本已删除。", false); detail.Text = "已完成的音频会保留；可在文件夹中查看。"; return; }
        if (state != null && Convert.ToString(state["phase"]) == "success") { SetStatus("下载完成，AAC 格式和完整解码检查通过。", false); detail.Text = Path.GetFileName(Convert.ToString(state["file"])); progress.Value = 100; }
        else { string category = state != null && state.ContainsKey("category") ? Convert.ToString(state["category"]) : "local_error"; SetStatus(ErrorText(category), true); detail.Text = "不会显示原始账户、媒体地址或解密日志。"; }
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
        if (process != null && !process.HasExited && MessageBox.Show(this,"下载进行中，取消任务并退出？", "退出",MessageBoxButtons.YesNo,MessageBoxIcon.Question) != DialogResult.Yes) { args.Cancel = true; return; }
        timer.Stop(); StopProcess(); CleanupJob(); sessionToken = null;
    }
    public void RenderPreview(string path) { ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Location = new Point(-32000,-32000); Show(); Application.DoEvents(); using(Bitmap bitmap = new Bitmap(Width,Height)) { DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height)); bitmap.Save(path); } Hide(); }
    public bool TestInvalidLink() { link.Text = "https://example.invalid/song/1"; StartDownload(this,EventArgs.Empty); return process == null && status.Text.StartsWith("链接无效"); }
    public bool TestRememberChoiceIsExplicit() { vault.Save("SYNTHETIC_PERSISTED_LOGIN_ONLY");remember.Checked=false;StoreToken("SYNTHETIC_TEMPORARY_LOGIN_ONLY");bool ok=vault.Load()=="SYNTHETIC_PERSISTED_LOGIN_ONLY" && login.Text.Contains("旧加密登录仍保留") && remember.Text.Contains("不会删除已有登录");vault.Clear();sessionToken=null;return ok; }
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
