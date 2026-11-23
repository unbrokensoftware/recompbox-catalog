param([string]$name)
$src = @"
using System; using System.Text; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class W {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(P p, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, P p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  static string C(IntPtr h){var s=new StringBuilder(256);GetClassNameW(h,s,256);return s.ToString();}
  static string T(IntPtr h){var s=new StringBuilder(512);GetWindowTextW(h,s,512);return s.ToString();}
  public static List<string> List(uint pid){ var r=new List<string>();
    EnumWindows((h,l)=>{uint p;GetWindowThreadProcessId(h,out p); if(p==pid){ r.Add("TOP vis="+IsWindowVisible(h)+" "+C(h)+" | "+T(h));
      EnumChildWindows(h,(c,l2)=>{r.Add("   "+C(c)+" | "+T(c));return true;},IntPtr.Zero);} return true;},IntPtr.Zero); return r; }
}
"@
Add-Type -TypeDefinition $src
foreach ($p in Get-Process $name -ErrorAction SilentlyContinue) { "pid $($p.Id)"; [W]::List([uint32]$p.Id) }
