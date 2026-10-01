$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$dll = 'C:\Users\dang.jf\WorkBuddy AI\2026-09-26-16-56-26\AUTOCAD2025_AI_TRANSLATE_CH_EN\src\AutoCAD.AITranslate\bin\Release\net8.0-windows\AutoCAD.AITranslate.dll'
$acadExe = 'C:\Program Files\Autodesk\AutoCAD 2026\acad.exe'
$dwg = 'C:\Users\dang.jf\Documents\Drawing1.dwg'
$log = Join-Path $env:TEMP 'AutoCAD.AITranslate.diag.log'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W32 {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool join);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr c, string cls, string win);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("imm32.dll")] public static extern IntPtr ImmGetContext(IntPtr h);
  [DllImport("imm32.dll")] public static extern bool ImmSetConversionStatus(IntPtr imc, int conv, int sent);
  [DllImport("imm32.dll")] public static extern bool ImmReleaseContext(IntPtr h, IntPtr imc);

  public static bool Focus(IntPtr h) {
    keybd_event(0x12, 0, 0, UIntPtr.Zero);           // tap Alt to release foreground lock
    keybd_event(0x12, 0, 2, UIntPtr.Zero);
    IntPtr fg = GetForegroundWindow();
    uint ftid = GetWindowThreadProcessId(fg, out _);
    uint ttid = GetWindowThreadProcessId(h, out _);
    bool joined = ftid != ttid && AttachThreadInput(ttid, ftid, true);
    ShowWindow(h, 5);
    BringWindowToTop(h);
    SetForegroundWindow(h);
    if (joined) AttachThreadInput(ttid, ftid, false);
    return GetForegroundWindow() == h;
  }
  public static bool IsForeground(IntPtr h) { return GetForegroundWindow() == h; }
  public static void ForceEnglishIme(IntPtr root) {
    IntPtr child = IntPtr.Zero;
    while (true) {
      child = FindWindowEx(root, child, null, null);
      if (child == IntPtr.Zero) break;
      ForceEnglishIme(child);
      IntPtr imc = ImmGetContext(child);
      if (imc != IntPtr.Zero) { ImmSetConversionStatus(imc, 0, 0); ImmReleaseContext(child, imc); }
    }
  }
}
"@

function Log([string]$m) { Write-Host "[test] $m" }

function Focus-Acad($acad) {
    for ($i = 0; $i -lt 5; $i++) {
        if ([W32]::Focus($acad.MainWindowHandle)) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

function Click-YesDialogs([uint32]$targetPid) {
    $found = $false
    $hwnd = [IntPtr]::Zero
    while ($true) {
        $hwnd = [W32]::FindWindowEx([IntPtr]::Zero, $hwnd, '#32770', $null)
        if ($hwnd -eq [IntPtr]::Zero) { break }
        $p = [uint32]0
        [void][W32]::GetWindowThreadProcessId($hwnd, [ref]$p)
        if ($p -ne $targetPid) { continue }
        Write-Host "[test] modal dialog (pid match) hwnd=$hwnd"
        foreach ($n in @('是(&Y)', '是(Y)', 'Yes', '加载', 'Load', '确定', 'OK')) {
            $b = [W32]::FindWindowEx($hwnd, [IntPtr]::Zero, 'Button', $n)
            if ($b -ne [IntPtr]::Zero) {
                [W32]::SendMessage($b, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
                Write-Host ("[test] clicked dialog button '" + $n + "'")
                $found = $true
                break
            }
        }
    }
    return $found
}

if (Test-Path $log) { Remove-Item $log -Force }
Get-Process acad -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

Log ('launching acad with drawing: ' + $dwg)
Start-Process -FilePath $acadExe -ArgumentList $dwg

$acad = $null
for ($i = 0; $i -lt 90; $i++) {
    Start-Sleep -Seconds 2
    $procs = Get-Process acad -ErrorAction SilentlyContinue
    $acad = $procs | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -match 'Drawing1.*AutoCAD' } | Select-Object -First 1
    if ($acad) { break }
}
if (-not $acad) {
    $acad = Get-Process acad -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
}
if (-not $acad) { Log 'FATAL: acad never appeared'; exit 1 }
$acadPid = [uint32]$acad.Id
Log ('hwnd=' + $acad.MainWindowHandle + ' title=' + $acad.MainWindowTitle)
Start-Sleep -Seconds 25   # let UI settle

Log ('focus check before typing: ' + (Focus-Acad $acad))
[W32]::ForceEnglishIme($acad.MainWindowHandle)
Start-Sleep -Milliseconds 500
if (-not [W32]::IsForeground($acad.MainWindowHandle)) { Log 'WARN: acad still not foreground, aborting typing phase'; exit 1 }

Log 'typing FILEDIA 0 + NETLOAD...'
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('FILEDIA{ENTER}')
Start-Sleep -Milliseconds 900
[System.Windows.Forms.SendKeys]::SendWait('0{ENTER}')
Start-Sleep -Milliseconds 900
[W32]::ForceEnglishIme($acad.MainWindowHandle)
[System.Windows.Forms.SendKeys]::SendWait('NETLOAD{ENTER}')
Start-Sleep -Seconds 2
[System.Windows.Forms.SendKeys]::SendWait($dll + '{ENTER}')
Log 'NETLOAD submitted'

$loaded = $false
for ($round = 0; $round -lt 20; $round++) {
    Start-Sleep -Seconds 2
    if (Test-Path $log) { $loaded = $true; break }
    [void](Click-YesDialogs $acadPid)
}
Log '--- diag log ---'
if (Test-Path $log) { Get-Content $log | ForEach-Object { Write-Host "  $_" } }
if (-not $loaded) { Log 'FATAL: plugin never loaded'; exit 1 }
Start-Sleep -Seconds 6   # ribbon build happens on Idle

Log 'UIA: locating AI tab and 模型设置 button...'
$auto = [System.Windows.Automation.AutomationElement]
$root = $auto::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, [int]$acad.Id)
$acadWin = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
if (-not $acadWin) { Log 'FATAL: acad window not found via UIA'; exit 1 }

$tabCond = New-Object System.Windows.Automation.PropertyCondition($auto::NameProperty, 'AI 翻译')
$tab = $acadWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $tabCond)
if ($tab) {
    Log ('AI tab found: ' + $tab.Current.ControlType.ProgrammaticName)
    try { ($tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() }
    catch {
        try { ($tab.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
        catch { Log ('WARN: tab activation failed: ' + $_.Exception.Message) }
    }
    Start-Sleep -Seconds 3
} else { Log 'WARN: AI tab not found via UIA (button search continues)' }

$btnCond = New-Object System.Windows.Automation.PropertyCondition($auto::NameProperty, '模型设置')
$btn = $acadWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
if (-not $btn) {
    Log 'FATAL: 模型设置 not found via UIA. Ribbon Button/TabItem names visible:'
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $global:dumpCount = 0
    function Dump-Names($el, $depth) {
        if ($global:dumpCount -gt 500 -or $depth -gt 10) { return }
        $c = $walker.GetFirstChild($el)
        while ($null -ne $c) {
            try {
                $global:dumpCount++
                $n = $c.Current.Name
                $ct = $c.Current.ControlType.ProgrammaticName
                if ($n -and $n.Length -gt 0 -and $n.Length -lt 40 -and $ct -match 'Button|TabItem') {
                    Write-Host ('  [d' + $depth + '] ' + $ct + ' | ' + $n)
                }
            } catch {}
            Dump-Names $c ($depth + 1)
            $c = $walker.GetNextSibling($c)
        }
    }
    Dump-Names $acadWin 0
    exit 1
}

Log ('button bounds: ' + $btn.Current.BoundingRectangle)
Log 'clicking 模型设置...'
$clicked = $false
try {
    ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    $clicked = $true
    Log 'invoked via UIA pattern'
} catch {
    Log ('UIA Invoke failed: ' + $_.Exception.Message + ' — falling back to coordinate click')
}
if (-not $clicked) {
    $r = $btn.Current.BoundingRectangle
    Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);' -Name M -Namespace MU | Out-Null
    [MU]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
    Start-Sleep -Milliseconds 300
    [MU]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero)
    [MU]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero)
    Log 'real mouse click sent'
}
Start-Sleep -Seconds 8

$winCond = New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
$andCond = New-Object System.Windows.Automation.AndCondition($pidCond, $winCond)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $andCond)
$settingsOpen = $false
foreach ($w in $wins) {
    $wn = $w.Current.Name
    if ($wn) { Write-Host ('  window: [' + $wn + ']') }
    if ($wn -match '模型|配置|Settings') { $settingsOpen = $true }
}
Write-Host ('RESULT settings-dialog-opened=' + $settingsOpen)

Log '--- diag log final ---'
if (Test-Path $log) { Get-Content $log | ForEach-Object { Write-Host "  $_" } }

Get-Process acad -ErrorAction SilentlyContinue | Stop-Process -Force
Log 'acad stopped, done'
