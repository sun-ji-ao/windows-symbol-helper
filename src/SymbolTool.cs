using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using System.IO.Compression;
using System.Web.Script.Serialization;

namespace WindowsSymbolTool {
public class Entry { public string Name; public string Key; public string Record { get { return Name + "," + Key + ",1"; } } }
public class Result { public string Name; public string Key; public string Status; public int Attempts; public string Error; public string Sha256; }
public class RunReport { public string Started; public string Source; public int Requested; public int Succeeded; public int Failed; public bool Complete; public List<Result> Results = new List<Result>(); }
public static class Native {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct Index {
        public uint StructSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=261)] public string File;
        public int Stripped; public uint Timestamp; public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=261)] public string Dbg;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=261)] public string Pdb;
        public Guid Guid; public uint Sig; public uint Age;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet=CharSet.Unicode, SetLastError=true)] delegate bool ReadIndex(string file, ref Index info, uint flags);
    static ReadIndex reader;
    public static void Initialize(string dll) {
        IntPtr h = LoadLibraryEx(dll, IntPtr.Zero, 8);
        if(h == IntPtr.Zero) throw new Exception("无法加载随包 DbgHelp: " + Marshal.GetLastWin32Error());
        IntPtr p = GetProcAddress(h, "SymSrvGetFileIndexInfoW");
        if(p == IntPtr.Zero) throw new Exception("DbgHelp 缺少符号索引接口");
        reader = (ReadIndex)Marshal.GetDelegateForFunctionPointer(p, typeof(ReadIndex));
    }
    public static Index GetIndex(string file) {
        Index info = new Index(); info.StructSize=(uint)Marshal.SizeOf(typeof(Index));
        // All DbgHelp calls are serialized, including GUI/background operations.
        lock(typeof(Native)) { if(!reader(file, ref info, 0)) throw new Exception("符号索引读取失败，错误 " + Marshal.GetLastWin32Error()); }
        return info;
    }
    public static string Key(Index i) { return (i.Sig != 0 ? i.Sig.ToString("X8") : i.Guid.ToString("N").ToUpperInvariant()) + i.Age.ToString("X"); }
    [DllImport("psapi.dll", SetLastError=true)] public static extern bool EnumDeviceDrivers([Out] IntPtr[] addresses, uint bytes, out uint needed);
    [DllImport("psapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern uint GetDeviceDriverFileName(IntPtr address, StringBuilder text, int size);
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct Privileges { public uint Count; public Luid Id; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr p,uint access,out IntPtr token);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool LookupPrivilegeValue(string system,string name,out Luid id);
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool AdjustTokenPrivileges(IntPtr token,bool disable,ref Privileges next,uint size,out Privileges previous,out uint needed);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    public sealed class DebugScope : IDisposable {
        IntPtr token; Privileges previous;
        public DebugScope() {
            if(!OpenProcessToken(Process.GetCurrentProcess().Handle,0x28,out token)) throw new Exception("无法打开进程权限令牌");
            try {
                Luid id; if(!LookupPrivilegeValue(null,"SeDebugPrivilege",out id)) throw new Exception("无法查询调试权限");
                var next = new Privileges { Count=1, Id=id, Attributes=2 }; uint needed;
                bool ok=AdjustTokenPrivileges(token,false,ref next,(uint)Marshal.SizeOf(typeof(Privileges)),out previous,out needed);
                int error=Marshal.GetLastWin32Error(); if(!ok || error!=0) throw new Exception("已加载驱动采集需要管理员权限，请右键以管理员身份运行");
            } catch { CloseHandle(token); token=IntPtr.Zero; throw; }
        }
        public void Dispose() { if(token!=IntPtr.Zero) { Privileges ignored; uint size; AdjustTokenPrivileges(token,false,ref previous,(uint)Marshal.SizeOf(typeof(Privileges)),out ignored,out size); CloseHandle(token); token=IntPtr.Zero; } }
    }
}
public static class Core {
    public const string Server="https://msdl.microsoft.com/download/symbols";
    public static string Root=AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    public static string ToolsDir;
    public static volatile bool CancelRequested;
    public static void CheckCancel() { if(CancelRequested)throw new OperationCanceledException("已停止。已通过校验的文件保留，可用原清单继续下载。"); }
    static JavaScriptSerializer Json { get { return new JavaScriptSerializer { MaxJsonLength=32*1024*1024 }; } }
    public static string Stamp() { return DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"); }
    public static void SaveJson(string path,object value) { Atomic(path,Json.Serialize(value)); }
    public static void Atomic(string path,string text) { string tmp=path+".tmp"; File.WriteAllText(tmp,text,new UTF8Encoding(false)); if(File.Exists(path)) File.Replace(tmp,path,null); else File.Move(tmp,path); }
    public static string Hash(string path) { using(var s=File.OpenRead(path)) using(var h=SHA256.Create()) return BitConverter.ToString(h.ComputeHash(s)).Replace("-",""); }
    public static void Prepare() {
        using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("tools.zip")) {
            string version; using(var hash=SHA256.Create()) version=BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").Substring(0,20);
            stream.Position=0; ToolsDir=Path.Combine(Root,".symbol-runtime",version); Directory.CreateDirectory(ToolsDir);
            using(var mutex=new Mutex(false,"Local\\WindowsSymbolTool-"+version)) {
                bool acquired=false;
                try {
                    try { acquired=mutex.WaitOne(30000); } catch(AbandonedMutexException) { acquired=true; }
                    if(!acquired)throw new Exception("另一个实例正在准备依赖，请稍后重试");
                    using(var zip=new ZipArchive(stream,ZipArchiveMode.Read)) foreach(var item in zip.Entries) {
                        if(item.Name.Length==0)continue;
                        byte[] data; using(var input=item.Open())using(var memory=new MemoryStream()){input.CopyTo(memory);data=memory.ToArray();}
                        string target=Path.Combine(ToolsDir,item.Name),expected;
                        using(var hash=SHA256.Create())expected=BitConverter.ToString(hash.ComputeHash(data)).Replace("-","");
                        if(!File.Exists(target)||Hash(target)!=expected)File.WriteAllBytes(target,data);
                    }
                } finally { if(acquired)mutex.ReleaseMutex(); }
            }
        }
        Native.Initialize(Path.Combine(ToolsDir,"dbghelp.dll"));
    }
    public static Entry Parse(string line) {
        var m=System.Text.RegularExpressions.Regex.Match(line.Trim(),@"^([A-Za-z0-9_().+ -]+\.pdb),([0-9A-Fa-f]{9,48}),1$");
        if(!m.Success || m.Groups[1].Value.StartsWith(" ") || m.Groups[1].Value.Contains("..")) throw new Exception("清单记录格式无效（仅接受 PDB）："+line);
        return new Entry { Name=m.Groups[1].Value,Key=m.Groups[2].Value.ToUpperInvariant() };
    }
    public static List<Entry> ReadManifest(string path) {
        var lines=File.ReadAllLines(path); if(lines.Length>30000) throw new Exception("清单过大");
        var entries=lines.Where(x=>!String.IsNullOrWhiteSpace(x)).Select(Parse).GroupBy(x=>x.Record,StringComparer.OrdinalIgnoreCase).Select(x=>x.First()).ToList();
        if(entries.Count==0) throw new Exception("清单为空，没有可处理的 Windows PDB"); return entries;
    }
    public static string SymbolPath(string cache,Entry e) { return Path.Combine(cache,e.Name,e.Key,e.Name); }
    public static bool Validate(string path,Entry e,out string reason) {
        try {
            if(!File.Exists(path) || new FileInfo(path).Length==0) throw new Exception("文件缺失或为空");
            var info=Native.GetIndex(path);
            if(!String.Equals(Native.Key(info),e.Key,StringComparison.OrdinalIgnoreCase)) throw new Exception("PDB GUID/签名 + Age 不匹配");
            reason=""; return true;
        } catch(Exception ex) { reason=ex.Message; return false; }
    }
    static string ResolveDriver(string path) {
        string windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if(path.StartsWith(@"\SystemRoot\",StringComparison.OrdinalIgnoreCase)) return Path.Combine(windows,path.Substring(12));
        if(path.StartsWith(@"\??\",StringComparison.Ordinal)) return path.Substring(4);
        if(path.StartsWith(@"\Windows\",StringComparison.OrdinalIgnoreCase)) return Path.GetPathRoot(windows).TrimEnd('\\')+path;
        if(!Path.IsPathRooted(path)) return Path.Combine(windows,path);
        return path;
    }
    public static string Collect(string specificFile,Action<string> log) {
        string output=Path.Combine(Root,"request_"+Stamp()); Directory.CreateDirectory(output);
        var candidates=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var warnings=new List<string>(); var modules=new List<object>();
        string windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if(specificFile!=null) candidates.Add(Path.GetFullPath(specificFile));
        else {
            try {
                using(var scope=new Native.DebugScope()) {
                    var addresses=new IntPtr[4096]; uint needed;
                    if(!Native.EnumDeviceDrivers(addresses,(uint)(addresses.Length*IntPtr.Size),out needed) || needed==0 || needed>addresses.Length*IntPtr.Size) throw new Exception("驱动枚举失败或缓冲区不足");
                    int nonzero=0;
                    for(int n=0;n<needed/IntPtr.Size;n++) {
                        if(addresses[n]==IntPtr.Zero) continue; nonzero++;
                        var buffer=new StringBuilder(32768);
                        if(Native.GetDeviceDriverFileName(addresses[n],buffer,buffer.Capacity)==0) warnings.Add("无法读取已加载驱动路径："+n);
                        else candidates.Add(ResolveDriver(buffer.ToString()));
                    }
                    if(nonzero==0) throw new Exception("系统未返回任何有效驱动地址，无法完成已加载驱动采集");
                }
            } catch(Exception ex) { warnings.Add(ex.Message+"；已继续扫描磁盘上的 Windows 系统驱动。"); }
            string system=Path.Combine(windows,"System32");
            foreach(string name in new[]{"ntoskrnl.exe","hal.dll","kdcom.dll","kdnet.dll","win32k.sys","win32kbase.sys","win32kfull.sys"}) {
                string f=Path.Combine(system,name); if(File.Exists(f)) candidates.Add(f);
            }
            try { foreach(string f in Directory.GetFiles(Path.Combine(system,"drivers"),"*.sys",SearchOption.TopDirectoryOnly)) candidates.Add(f); }
            catch(Exception ex) { warnings.Add("系统驱动目录读取失败："+ex.Message); }
        }
        var entries=new Dictionary<string,Entry>(StringComparer.OrdinalIgnoreCase);
        foreach(string file in candidates.OrderBy(x=>x)) {
            CheckCancel();
            try {
                if(!File.Exists(file)) throw new Exception("模块文件不可读取，可能是设备路径或已移除");
                string company=FileVersionInfo.GetVersionInfo(file).CompanyName ?? "";
                if(!Path.GetFullPath(file).StartsWith(windows+"\\",StringComparison.OrdinalIgnoreCase) || company.IndexOf("Microsoft",StringComparison.OrdinalIgnoreCase)<0) {
                    modules.Add(new { File=file,Status="SkippedNonWindows",Detail=company }); continue;
                }
                var info=Native.GetIndex(file);
                string name=Path.GetFileName(info.Pdb);
                if(String.IsNullOrEmpty(name) || !name.EndsWith(".pdb",StringComparison.OrdinalIgnoreCase) || (info.Guid==Guid.Empty && info.Sig==0)) throw new Exception("模块没有可用 PDB 标识");
                Entry entry=Parse(name+","+Native.Key(info)+",1"); entries[entry.Record]=entry;
                modules.Add(new { File=file,Status="Collected",Detail=entry.Record });
                log("采集 "+Path.GetFileName(file)+" → "+entry.Name);
            } catch(Exception ex) { warnings.Add(file+": "+ex.Message); modules.Add(new { File=file,Status="Failed",Detail=ex.Message }); }
        }
        Atomic(Path.Combine(output,"symbols.manifest"),String.Join(Environment.NewLine,entries.Keys.OrderBy(x=>x))+Environment.NewLine);
        SaveJson(Path.Combine(output,"request-info.json"),new { CollectedAt=DateTime.Now.ToString("o"),OS=Environment.OSVersion.VersionString,Scope="Windows Microsoft modules only; loaded drivers + System32 drivers + kernel baseline",Requested=entries.Count,Warnings=warnings,Modules=modules });
        Atomic(Path.Combine(output,"采集说明.txt"),"仅需把 symbols.manifest 搬到联网电脑。request-info.json 记录采集范围和遗漏。\r\nWindows 目录 + Microsoft 公司元数据用于筛选，不保证微软公开每一个 PDB。\r\n"+String.Join("\r\n",warnings));
        if(entries.Count==0) throw new Exception("没有采集到符号。详细原因："+output);
        log("导出 "+entries.Count+" 项；警告 "+warnings.Count+" 条。输出："+output);
        return output;
    }
    public static int RunProcess(string exe,string args,int timeout,string logPath) {
        var p=new Process { StartInfo=new ProcessStartInfo(exe,args) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=ToolsDir } };
        var text=new StringBuilder(); object gate=new object();
        p.OutputDataReceived+=(s,e)=>{ if(e.Data!=null) lock(gate) text.AppendLine(e.Data); };
        p.ErrorDataReceived+=(s,e)=>{ if(e.Data!=null) lock(gate) text.AppendLine(e.Data); };
        try {
            p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
            var clock=Stopwatch.StartNew();
            while(!p.WaitForExit(250)) {
                if(CancelRequested || clock.ElapsedMilliseconds>=timeout) { try { p.Kill(); } catch {} p.WaitForExit(); CheckCancel(); lock(gate) text.AppendLine("TIMEOUT"); return -2; }
            }
            p.WaitForExit(); return p.ExitCode;
        } finally { lock(gate) File.WriteAllText(logPath,text.ToString(),Encoding.UTF8); p.Dispose(); }
    }
    static string Quote(string s) { if(s.Contains("\"") || s.EndsWith("\\")) throw new Exception("路径不受支持"); return "\""+s+"\""; }
    // A failed attempt includes download OR identity validation failure. Initial + 3 retries.
    public static Result Execute(Entry e,Func<int,string> attempt,Action<int> wait,Action<string> log) {
        var r=new Result { Name=e.Name,Key=e.Key,Status="Failed",Error="",Sha256="" };
        for(int n=1;n<=4;n++) {
            CheckCancel();
            r.Attempts=n;
            try { r.Error=attempt(n) ?? ""; } catch(OperationCanceledException) { throw; } catch(Exception ex) { r.Error=ex.Message; }
            if(r.Error.Length==0) { r.Status="Verified"; return r; }
            if(n<4) { log(e.Name+" 本次未通过："+r.Error+"；准备重试 "+n+"/3"); wait(n); }
        }
        log(e.Name+" 首次尝试及 3 次重试均失败，写入最终失败清单。"); return r;
    }
    public static string Download(string manifest,string existingCache,Action<string> log) {
        var entries=ReadManifest(manifest);
        string cache=String.IsNullOrEmpty(existingCache)?Path.Combine(Root,"symbol_"+Stamp()):Path.GetFullPath(existingCache);
        if(cache.IndexOfAny(new[]{'*',';'})>=0) throw new Exception("缓存路径不能包含 * 或 ;");
        Directory.CreateDirectory(cache);
        // A lock prevents two instances modifying the same cache/report.
        using(var cacheLock=new FileStream(Path.Combine(cache,".download.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
            var all=new Dictionary<string,Entry>(StringComparer.OrdinalIgnoreCase);
            string inventory=Path.Combine(cache,"symbols.manifest");
            if(File.Exists(inventory)) foreach(var e in ReadManifest(inventory)) all[e.Record]=e;
            foreach(var e in entries) all[e.Record]=e;
            Atomic(inventory,String.Join("\r\n",all.Keys)+"\r\n");
            string reportDir=Path.Combine(cache,"reports",Stamp()); Directory.CreateDirectory(reportDir);
            var report=new RunReport { Started=DateTime.Now.ToString("o"),Source=Path.GetFullPath(manifest),Requested=entries.Count };
            var priorHashes=ReadHashes(cache);
            Atomic(Path.Combine(reportDir,"requested.manifest"),String.Join("\r\n",entries.Select(e=>e.Record))+"\r\n");
            SaveReport(reportDir,report,false);
            for(int i=0;i<entries.Count;i++) {
                CheckCancel();
                Entry e=entries[i]; string file=SymbolPath(cache,e); string reason;
                log("["+(i+1)+"/"+entries.Count+"] "+e.Name);
                Result result;
                string oldHash; string relative=e.Name+"\\"+e.Key+"\\"+e.Name;
                if(Validate(file,e,out reason) && (!priorHashes.TryGetValue(relative,out oldHash) || Hash(file)==oldHash)) result=new Result { Name=e.Name,Key=e.Key,Status="CachedVerified",Attempts=0,Error="",Sha256=Hash(file) };
                else {
                    string item=Path.Combine(reportDir,"item-"+i.ToString("D5")+".manifest"); Atomic(item,e.Record+"\r\n");
                    result=Execute(e,n=>{
                        // Remove only the exact invalid file, so retries cannot reuse corruption.
                        if(File.Exists(file)) File.Delete(file);
                        string attemptLog=Path.Combine(reportDir,"item-"+i.ToString("D5")+"-attempt-"+n+".log");
                        int code=RunProcess(Path.Combine(ToolsDir,"symchk.exe"),"/im "+Quote(item)+" /s "+Quote("srv*"+cache+"*"+Server)+" /op /os",180000,attemptLog);
                        string why;
                        if(code!=0) return "下载进程退出码 "+code+"；"+String.Join(" ",File.ReadAllLines(attemptLog).Where(x=>x.Contains("ERROR") || x.Contains("TIMEOUT")).Take(2));
                        if(!Validate(file,e,out why)) return why;
                        return "";
                    },n=>{for(int t=0;t<n*8;t++){CheckCancel();Thread.Sleep(250);}},log);
                    if(result.Status=="Verified") result.Sha256=Hash(file);
                }
                report.Results.Add(result); SaveReport(reportDir,report,false);
                log(result.Status=="Failed"?"最终失败："+result.Error:"已校验通过");
            }
            SaveReport(reportDir,report,true);
            // Consolidated failure list includes earlier unresolved entries, revalidated locally.
            var missing=new List<string>(); var hashes=new List<string>();
            var refreshed=new HashSet<string>(report.Results.Where(x=>x.Status!="Failed").Select(x=>x.Name+"\\"+x.Key+"\\"+x.Name),StringComparer.OrdinalIgnoreCase);
            foreach(var e in all.Values) {
                string why,oldHash; string f=SymbolPath(cache,e),relative=e.Name+"\\"+e.Key+"\\"+e.Name;
                if(!Validate(f,e,out why)) { missing.Add(e.Record); continue; }
                string hash=Hash(f);
                if(!refreshed.Contains(relative) && priorHashes.TryGetValue(relative,out oldHash) && hash!=oldHash) { missing.Add(e.Record);hashes.Add(oldHash+"  "+relative.Replace('\\','/')); }
                else hashes.Add(hash+"  "+relative.Replace('\\','/'));
            }
            Atomic(Path.Combine(cache,"failed.manifest"),String.Join("\r\n",missing)+(missing.Count>0?"\r\n":""));
            Atomic(Path.Combine(cache,"SHA256SUMS.txt"),String.Join("\r\n",hashes)+"\r\n");
            Atomic(Path.Combine(cache,"WinDbg使用说明.txt"),"将整个目录复制到需要符号的离线调试环境，保留 PDB/标识/PDB 结构。\r\n以下 D:\\Symbols\\目录名 为复制后的实际路径：\r\n.sympath srv*D:\\Symbols\\"+Path.GetFileName(cache)+"\r\n.reload /f nt\r\nlmvm nt\r\n\r\n可使用本工具“校验符号目录”验证复制完整性。\r\n");
            log("本次完成：成功 "+report.Succeeded+"，失败 "+report.Failed+"；整个缓存未解决 "+missing.Count+" 项。\r\n"+cache);
            return cache;
        }
    }
    static void SaveReport(string dir,RunReport report,bool complete) {
        report.Succeeded=report.Results.Count(x=>x.Status!="Failed"); report.Failed=report.Results.Count(x=>x.Status=="Failed"); report.Complete=complete;
        SaveJson(Path.Combine(dir,"report.json"),report);
        Atomic(Path.Combine(dir,"failed.manifest"),String.Join("\r\n",report.Results.Where(x=>x.Status=="Failed").Select(x=>x.Name+","+x.Key+",1")));
    }
    static Dictionary<string,string> ReadHashes(string cache) {
        var hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        string sums=Path.Combine(cache,"SHA256SUMS.txt");
        if(File.Exists(sums)) foreach(string line in File.ReadAllLines(sums)) {
            if(String.IsNullOrWhiteSpace(line)) continue;
            if(line.Length<67 || line.Substring(64,2)!="  ") throw new Exception("SHA256SUMS 格式无效");
            if(!System.Text.RegularExpressions.Regex.IsMatch(line.Substring(0,64),"^[0-9A-Fa-f]{64}$"))throw new Exception("SHA256SUMS 哈希无效");
            hashes[line.Substring(66).Replace('/','\\')]=line.Substring(0,64).ToUpperInvariant();
        }
        return hashes;
    }
    public static int Verify(string cache,Action<string> log) {
        var entries=ReadManifest(Path.Combine(cache,"symbols.manifest")); int failures=0;
        var hashes=ReadHashes(cache); string sums=Path.Combine(cache,"SHA256SUMS.txt");
        foreach(var e in entries) {
            CheckCancel();
            string f=SymbolPath(cache,e), why, expected; bool ok=Validate(f,e,out why);
            if(ok && hashes.TryGetValue(e.Name+"\\"+e.Key+"\\"+e.Name,out expected) && !String.Equals(Hash(f),expected,StringComparison.OrdinalIgnoreCase)) { ok=false; why="SHA-256 与下载时不一致"; }
            if(!ok) failures++; log(e.Name+": "+(ok?"PDB 标识校验通过":why));
        }
        log("校验完成："+(entries.Count-failures)+" 通过，"+failures+" 失败。"+(File.Exists(sums)?"已检查清单内 SHA-256。":"无 SHA-256 清单，仅校验 PDB 标识。")); return failures;
    }
    public static void SelfTest() {
        var e=Parse("test.pdb,0123456789ABCDEF0123456789ABCDEF1,1");
        int calls=0, waits=0; var r=Execute(e,n=>{ calls++;return "fail"; },n=>waits++,s=>{});
        if(calls!=4 || waits!=3 || r.Status!="Failed" || r.Attempts!=4) throw new Exception("retry exhaustion test failed");
        calls=0; waits=0; r=Execute(e,n=>{calls++;return n==3?"":"fail";},n=>waits++,s=>{});
        if(calls!=3 || waits!=2 || r.Status!="Verified") throw new Exception("retry recovery test failed");
        r=Execute(e,n=>"",n=>{throw new Exception("must not wait");},s=>{}); if(r.Attempts!=1) throw new Exception("initial success failed");
        foreach(string bad in new[]{"../evil.pdb,ABCDEF123,1","x.pdb,../../x,1","x.exe,0123456789,1","x.pdb,123,1"}) { bool rejected=false;try{Parse(bad);}catch{rejected=true;}if(!rejected)throw new Exception("unsafe manifest accepted"); }
        string tmp=Path.Combine(ToolsDir,"invalid.pdb"); File.WriteAllText(tmp,"not a pdb"); string why; if(Validate(tmp,e,out why))throw new Exception("corrupt PDB accepted");
        CancelRequested=true; bool stopped=false;try{Execute(e,n=>"",n=>{},s=>{});}catch(OperationCanceledException){stopped=true;}finally{CancelRequested=false;}if(!stopped)throw new Exception("cancellation failed");
        calls=0;r=Execute(e,n=>{calls++;throw new IOException("transport exception");},n=>{},s=>{});if(calls!=4||r.Status!="Failed")throw new Exception("exception retries failed");
    }
}
public static class Theme {
    public static readonly Color Ink=Color.FromArgb(24,40,61),Muted=Color.FromArgb(100,116,139),Blue=Color.FromArgb(37,99,235),Line=Color.FromArgb(225,232,241),Background=Color.FromArgb(244,247,251);
    public static Font Font(float size,FontStyle style=FontStyle.Regular){return new Font("Microsoft YaHei UI",size,style);}
    public static System.Drawing.Drawing2D.GraphicsPath Round(Rectangle r,int radius){
        var p=new System.Drawing.Drawing2D.GraphicsPath();int d=radius*2;
        p.AddArc(r.Left,r.Top,d,d,180,90);p.AddArc(r.Right-d,r.Top,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.Left,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
    }
    public static Label Label(string text,float size,Color color,FontStyle style=FontStyle.Regular){return new Label{Text=text,Font=Font(size,style),ForeColor=color,BackColor=Color.Transparent,AutoSize=false,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Margin=Padding.Empty};}
}
public class Surface:Panel {
    public Color Fill=Color.White;
    public Surface(){DoubleBuffered=true;BackColor=Theme.Background;Padding=new Padding(22);}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;using(var path=Theme.Round(new Rectangle(0,0,Width-1,Height-1),14))using(var brush=new SolidBrush(Fill))using(var pen=new Pen(Theme.Line)){e.Graphics.FillPath(brush,path);e.Graphics.DrawPath(pen,path);}}
}
public class ModernButton:Button {
    public bool Primary;
    bool hover;
    public ModernButton(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;Font=Theme.Font(10,FontStyle.Bold);Cursor=Cursors.Hand;Height=40;Width=160;BackColor=Color.White;}
    protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){hover=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnEnabledChanged(EventArgs e){Invalidate();base.OnEnabledChanged(e);}
    protected override void OnPaint(PaintEventArgs e){
        e.Graphics.Clear(BackColor);e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Color fill=!Enabled?Color.FromArgb(239,243,248):Primary?(hover?Color.FromArgb(29,78,216):Theme.Blue):(hover?Color.FromArgb(235,242,252):Color.White);
        using(var path=Theme.Round(new Rectangle(1,1,Width-3,Height-3),8))using(var brush=new SolidBrush(fill))using(var pen=new Pen(Primary&&Enabled?fill:Theme.Line)){e.Graphics.FillPath(brush,path);e.Graphics.DrawPath(pen,path);}
        TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,!Enabled?Color.FromArgb(148,163,184):Primary?Color.White:Theme.Ink,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
        if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(6,6,Width-12,Height-12));
    }
}
public class MainForm:Form {
    TextBox logs=new TextBox {Multiline=true,ReadOnly=true,BorderStyle=BorderStyle.None,Dock=DockStyle.Fill,WordWrap=false,ScrollBars=ScrollBars.Both,Font=Theme.Font(9),BackColor=Color.White,ForeColor=Theme.Muted};
    List<ModernButton> operationButtons=new List<ModernButton>();
    ModernButton cancel=new ModernButton {Text="停止任务",Width=108,Enabled=false};
    Label status=Theme.Label("●  准备就绪",10,Color.FromArgb(5,130,100),FontStyle.Bold);
    ProgressBar progress=new ProgressBar {Dock=DockStyle.Bottom,Height=3,Style=ProgressBarStyle.Marquee,MarqueeAnimationSpeed=28,Visible=false};
    bool busy;
    public MainForm() {
        Text="Windows 符号助手";ShowIcon=false;ClientSize=new Size(1040,790);MinimumSize=new Size(960,760);StartPosition=FormStartPosition.CenterScreen;
        Font=Theme.Font(10);AutoScaleMode=AutoScaleMode.Font;BackColor=Theme.Background;
        var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(28,22,28,14),ColumnCount=1,RowCount=6,BackColor=Theme.Background};
        foreach(int height in new[]{88,54,206,58})layout.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
        Controls.Add(layout);
        var header=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Margin=Padding.Empty};
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,145));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute,43));header.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        header.Controls.Add(Theme.Label("Windows 符号助手",23,Theme.Ink,FontStyle.Bold),0,0);
        status.TextAlign=ContentAlignment.MiddleRight;header.Controls.Add(status,1,0);
        var subtitle=Theme.Label("为无法联网的电脑准备 Windows 公共符号，方便离线调试。",10,Theme.Muted);
        header.Controls.Add(subtitle,0,1);header.SetColumnSpan(subtitle,2);layout.Controls.Add(header,0,0);
        var journey=new Surface {Dock=DockStyle.Fill,Padding=new Padding(16,0,16,0),Margin=new Padding(0,0,0,12),Fill=Color.FromArgb(235,242,255)};
        var steps=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,BackColor=Color.Transparent};
        for(int n=0;n<3;n++)steps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.333f));
        steps.Controls.Add(Theme.Label("01   离线电脑生成清单    →",10,Theme.Blue,FontStyle.Bold),0,0);
        steps.Controls.Add(Theme.Label("02   联网电脑下载符号    →",10,Theme.Blue,FontStyle.Bold),1,0);
        steps.Controls.Add(Theme.Label("03   复制回离线环境使用",10,Theme.Blue,FontStyle.Bold),2,0);journey.Controls.Add(steps);layout.Controls.Add(journey,0,1);
        var cards=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        cards.Controls.Add(Card("离线电脑","生成符号需求清单","在需要调试的电脑上运行，导出 Windows 模块标识。\r\n建议以管理员身份运行，以完整枚举已加载驱动。","导出清单  →",false,()=>Start("正在生成清单",()=>Core.Collect(null,Log))),0,0);
        cards.Controls.Add(Card("联网电脑","按清单下载公共符号","选择从离线电脑带来的清单，下载匹配的符号。\r\n完成后，将整个符号目录复制回离线调试环境。","选择清单并下载  →",true,()=>{string manifest=PickManifest();if(manifest!=null)Start("正在下载符号",()=>Core.Download(manifest,null,Log));}),1,0);
        cards.GetControlFromPosition(0,0).Margin=new Padding(0,0,8,0);cards.GetControlFromPosition(1,0).Margin=new Padding(8,0,0,0);layout.Controls.Add(cards,0,2);
        var utilities=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.LeftToRight,WrapContents=false,Padding=new Padding(0,8,0,0),Margin=Padding.Empty};
        var retry=Button("重试 / 继续下载",false,()=>{string manifest=PickManifest();if(manifest==null)return;using(var dlg=new FolderBrowserDialog{Description="选择原 symbol_时间 目录，复用已下载符号",SelectedPath=Path.GetDirectoryName(manifest)})if(dlg.ShowDialog()==DialogResult.OK)Start("正在继续下载",()=>Core.Download(manifest,dlg.SelectedPath,Log));});
        var verify=Button("校验符号目录",false,()=>{using(var dlg=new FolderBrowserDialog{Description="选择包含 symbols.manifest 的符号目录"})if(dlg.ShowDialog()==DialogResult.OK)Start("正在校验符号",()=>{int n=Core.Verify(dlg.SelectedPath,Log);return "本地校验结束，失败 "+n+" 项";});});
        retry.BackColor=Theme.Background;verify.BackColor=Theme.Background;utilities.Controls.Add(retry);utilities.Controls.Add(verify);
        var note=Theme.Label("失败后自动重试 3 次 · 下载后校验匹配性",9,Theme.Muted);note.Dock=DockStyle.None;note.Width=345;note.Height=40;note.Margin=new Padding(14,0,0,0);utilities.Controls.Add(note);layout.Controls.Add(utilities,0,3);
        var logCard=new Surface {Dock=DockStyle.Fill,Margin=Padding.Empty,Padding=new Padding(20,12,20,16)};
        var logLayout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,BackColor=Color.White,Margin=Padding.Empty};logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,46));logLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var logHeader=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty,BackColor=Color.White};logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,110));
        logHeader.Controls.Add(Theme.Label("运行记录",11,Theme.Ink,FontStyle.Bold),0,0);cancel.Dock=DockStyle.Fill;cancel.Margin=new Padding(0,0,0,8);logHeader.Controls.Add(cancel,1,0);logLayout.Controls.Add(logHeader,0,0);logs.Margin=new Padding(0,8,0,0);logLayout.Controls.Add(logs,0,1);logCard.Controls.Add(logLayout);logCard.Controls.Add(progress);layout.Controls.Add(logCard,0,4);
        var footer=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,130));
        footer.Controls.Add(Theme.Label("Windows 公共符号  /  清单与下载结果保存在程序目录",9,Theme.Muted),0,0);
        var open=new LinkLabel {Text="打开程序目录  ↗",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleRight,LinkColor=Theme.Blue,ActiveLinkColor=Theme.Ink,LinkBehavior=LinkBehavior.HoverUnderline,Font=Theme.Font(9)};open.LinkClicked+=(s,e)=>Process.Start("explorer.exe",Core.Root);footer.Controls.Add(open,1,0);layout.Controls.Add(footer,0,5);
        cancel.Click+=(s,e)=>{Core.CancelRequested=true;cancel.Enabled=false;status.Text="●  正在停止";Log("正在停止，请稍候……");};
        FormClosing+=(s,e)=>{if(busy){e.Cancel=true;Core.CancelRequested=true;Log("已请求停止，任务停止后可关闭窗口。");}};
        Log("准备就绪。请根据当前电脑的网络环境，选择导出清单或下载符号。");Log("输出目录："+Core.Root);
    }
    ModernButton Button(string text,bool primary,Action run){var button=new ModernButton{Text=text,Primary=primary,Margin=new Padding(0,0,10,0)};button.Click+=(s,e)=>run();operationButtons.Add(button);return button;}
    Surface Card(string tag,string title,string description,string buttonText,bool primary,Action run){
        var card=new Surface{Dock=DockStyle.Fill};
        var content=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=4,ColumnCount=1,BackColor=Color.White,Margin=Padding.Empty};
        content.RowStyles.Add(new RowStyle(SizeType.Absolute,23));content.RowStyles.Add(new RowStyle(SizeType.Absolute,34));content.RowStyles.Add(new RowStyle(SizeType.Percent,100));content.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
        content.Controls.Add(Theme.Label(tag,9,primary?Theme.Blue:Theme.Muted,FontStyle.Bold),0,0);content.Controls.Add(Theme.Label(title,15,Theme.Ink,FontStyle.Bold),0,1);
        var desc=Theme.Label(description,9,Theme.Muted);desc.TextAlign=ContentAlignment.TopLeft;desc.Padding=new Padding(0,6,0,0);content.Controls.Add(desc,0,2);
        var button=Button(buttonText,primary,run);button.Width=192;button.Anchor=AnchorStyles.Left|AnchorStyles.Bottom;button.Margin=Padding.Empty;content.Controls.Add(button,0,3);card.Controls.Add(content);return card;
    }
    string PickManifest(){using(var d=new OpenFileDialog{Title="选择符号需求清单或失败清单",Filter="符号清单|*.manifest|所有文件|*.*"})return d.ShowDialog()==DialogResult.OK?d.FileName:null;}
    void Log(string text){if(InvokeRequired){BeginInvoke(new Action<string>(Log),text);return;}logs.AppendText(DateTime.Now.ToString("HH:mm:ss")+"   "+text+Environment.NewLine);}
    async void Start(string activity,Func<string> work){
        busy=true;Core.CancelRequested=false;foreach(var b in operationButtons)b.Enabled=false;cancel.Enabled=true;progress.Visible=true;status.Text="●  "+activity;status.ForeColor=Theme.Blue;
        try{string output=await Task.Run(work);Log(output);status.Text="●  任务已结束";status.ForeColor=Color.FromArgb(5,130,100);MessageBox.Show(this,"请查看运行记录中的成功、失败或警告统计。\r\n\r\n"+output,"任务已结束",MessageBoxButtons.OK,MessageBoxIcon.Information);}
        catch(OperationCanceledException ex){Log(ex.Message);status.Text="●  已停止";status.ForeColor=Theme.Muted;}
        catch(Exception ex){Log("错误："+ex.Message);status.Text="●  操作未完成";status.ForeColor=Color.FromArgb(185,61,47);MessageBox.Show(this,ex.Message,"操作未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        finally{busy=false;foreach(var b in operationButtons)b.Enabled=true;cancel.Enabled=false;progress.Visible=false;}
    }
}
public static class Program {
    [STAThread] public static int Main(string[] args) {
        bool cli=args.Length>0;
        try {
            Core.Prepare();
            if(!cli){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new MainForm());return 0;}
            Action<string> log=s=>Console.WriteLine(s);
            switch(args[0]) {
                case "--self-test":Core.SelfTest();Console.WriteLine("SELF TEST PASSED");return 0;
                case "--ui-preview":
                    Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                    using(var form=new MainForm()) { if(args.Length>3)form.ClientSize=new Size(Int32.Parse(args[2]),Int32.Parse(args[3]));form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();form.PerformLayout();using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));bitmap.Save(args[1],System.Drawing.Imaging.ImageFormat.Png);}form.Close(); }
                    Console.WriteLine("UI PREVIEW PASSED");return 0;
                case "--collect":Console.WriteLine(Core.Collect(null,log));return 0;
                case "--collect-file":Console.WriteLine(Core.Collect(args[1],log));return 0;
                case "--download": {
                    string cache=Core.Download(args[1],args.Length>2?args[2]:null,log);
                    return Core.ReadManifest(Path.Combine(cache,"symbols.manifest")).Any(e=>{string reason;return !Core.Validate(Core.SymbolPath(cache,e),e,out reason);})?2:0;
                }
                case "--verify":return Core.Verify(args[1],log)==0?0:2;
                default:Console.WriteLine("--collect | --collect-file <Windows binary> | --download <manifest> [existing cache] | --verify <cache> | --self-test");return 1;
            }
        } catch(Exception ex){if(cli)Console.Error.WriteLine(ex.ToString());else MessageBox.Show(ex.Message,"启动或操作失败");return 1;}
    }
}
}
