param([string]$zip, [string]$romZip, [string]$exe, [string]$romName = 'baserom.us.z64', [string]$arg = '', [int]$seconds = 60)
$dir = Join-Path 'D:\AI\probe' ([IO.Path]::GetFileNameWithoutExtension($zip))
if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
New-Item -ItemType Directory $dir | Out-Null
Expand-Archive $zip $dir
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::OpenRead($romZip); $e = $z.Entries[0]; [IO.Compression.ZipFileExtensions]::ExtractToFile($e, (Join-Path $dir $romName)); $z.Dispose()
$src = @"
using System; using System.Text; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class Probe {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(P p, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, P p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  static string C(IntPtr h){var s=new StringBuilder(256);GetClassNameW(h,s,256);return s.ToString();}
  static string T(IntPtr h){var s=new StringBuilder(512);GetWindowTextW(h,s,512);return s.ToString();}
  public static List<string> Step(uint pid){ var r=new List<string>();
    EnumWindows((h,l)=>{uint p;GetWindowThreadProcessId(h,out p); if(p==pid && IsWindowVisible(h)){ string line=C(h)+" | "+T(h);
      if(C(h)=="#32770"){ EnumChildWindows(h,(c,l2)=>{ string t=T(c); if(C(c)=="Static" && t.Length>0) line+=" | "+t;
          if(C(c)=="Button" && (t.Replace("&","")=="Yes"||t=="OK")){ PostMessage(h,0x0111,(IntPtr)GetDlgCtrlID(c),c); line+=" [answered "+t+"]"; } return true;},IntPtr.Zero);}
      r.Add(line);} return true;},IntPtr.Zero); return r; }
}
"@
if (-not ('Probe' -as [type])) { Add-Type -TypeDefinition $src }
$args2 = if ($arg) { $arg } else { $null }
$p = if ($args2) { Start-Process (Join-Path $dir $exe) -ArgumentList $args2 -WorkingDirectory $dir -PassThru } else { Start-Process (Join-Path $dir $exe) -WorkingDirectory $dir -PassThru }
$seen = @{}
for ($i = 0; $i -lt $seconds; $i++) {
  Start-Sleep 1
  if ($p.HasExited) { "exited code $($p.ExitCode) at $i s"; break }
  foreach ($w in [Probe]::Step([uint32]$p.Id)) { if (-not $seen.ContainsKey($w)) { $seen[$w] = 1; "$i s: $w" } }
  $o2r = Get-ChildItem $dir -Filter *.o2r | Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-5) }
  if ($o2r) { $sizes = ($o2r | ForEach-Object { "$($_.Name)=$($_.Length)" }) -join ' '; if ($sizes -ne $last) { "$i s: new $sizes"; $last = $sizes } }
}
Stop-Process -Id $p.Id -ErrorAction SilentlyContinue
