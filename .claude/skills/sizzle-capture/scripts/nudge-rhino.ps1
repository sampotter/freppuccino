# `rhinocode script` only queues the script: Rhino starts it the next time its main window is in the foreground.
# This brings Rhino forward for a few seconds (long enough to pick the script up; once started it keeps running
# without focus) and then gives focus back to whatever window the user was in.
# Windows refuses SetForegroundWindow from a background process unless it shares the foreground thread's input
# queue or a key event just happened, so both tricks are used and the result is checked.
param([int]$Seconds = 4)
Add-Type -Name Win -Namespace Sizzle2 -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
public static bool Focus(IntPtr h) {
  IntPtr fg = GetForegroundWindow();
  if (fg == h) return true;
  uint me = GetCurrentThreadId(), fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero), target = GetWindowThreadProcessId(h, IntPtr.Zero);
  keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);   // ALT down/up unlocks SetForegroundWindow
  if (fgThread != me) AttachThreadInput(me, fgThread, true);
  if (target != me) AttachThreadInput(me, target, true);
  if (IsIconic(h)) ShowWindow(h, 9);
  BringWindowToTop(h);
  bool ok = SetForegroundWindow(h);
  if (fgThread != me) AttachThreadInput(me, fgThread, false);
  if (target != me) AttachThreadInput(me, target, false);
  return ok && GetForegroundWindow() == h;
}
'@
$rhino = Get-Process Rhino -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $rhino) { 'Rhino is not running'; exit 1 }
$previous = [Sizzle2.Win]::GetForegroundWindow()
$h = $rhino.MainWindowHandle
$ok = [Sizzle2.Win]::Focus($h)
if (-not $ok) { Start-Sleep -Milliseconds 300; $ok = [Sizzle2.Win]::Focus($h) }
"rhino foreground: $ok"
Start-Sleep -Seconds $Seconds
if ($previous -ne [IntPtr]::Zero -and $previous -ne $h) { [Sizzle2.Win]::Focus($previous) | Out-Null }
if (-not $ok) { exit 1 }
