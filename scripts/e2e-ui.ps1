# End-to-end UI check through UI Automation (the same tree Narrator reads):
# launch Squish with files, press Compress, wait for "Copy all", press it, and read the clipboard.
#   powershell -STA -File scripts\e2e-ui.ps1 -Exe <Squish.exe> -Files a.mp4,b.jpg [-Shot out.png] [-TimeoutSec 600]
#   (-Files is one string, paths separated by '|', because -File can't pass arrays.)
param([string]$Exe, [string]$Files, [string]$Shot, [int]$TimeoutSec = 600)
$ErrorActionPreference = 'Stop'
$paths = @($Files -split '\|')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms

$sw = [Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process $Exe -ArgumentList ($paths | ForEach-Object { "`"$_`"" }) -PassThru
while ($proc.MainWindowHandle -eq 0) {
    if ($sw.Elapsed.TotalSeconds -gt 20) { throw 'window never appeared' }
    Start-Sleep -Milliseconds 50; $proc.Refresh()
}
'window: {0:N2}s' -f $sw.Elapsed.TotalSeconds
$root = [Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

function Find-Button([string]$prefix) {
    $cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)
    foreach ($b in $root.FindAll([Windows.Automation.TreeScope]::Descendants, $cond)) {
        if ($b.Current.Name -like "$prefix*") { return $b }
    }
    return $null
}
function Find-Primary {
    $cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, 'PrimaryButton')
    return $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
}
function Invoke($el) { $el.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }

# Wait for probing to finish: the primary button reads "Compress N file(s)" and is enabled.
$deadline = (Get-Date).AddSeconds(30)
do {
    Start-Sleep -Milliseconds 300
    $btn = Find-Primary
} until (($btn -and $btn.Current.IsEnabled -and $btn.Current.Name -match "Compress $($paths.Count)") -or (Get-Date) -gt $deadline)
if (-not $btn) { throw 'Compress button not found' }
"ready: '$($btn.Current.Name)' at {0:N1}s, WS={1} MB" -f $sw.Elapsed.TotalSeconds, [math]::Round((Get-Process -Id $proc.Id).WorkingSet64 / 1MB)
Invoke $btn

$deadline = (Get-Date).AddSeconds($TimeoutSec)
do { Start-Sleep -Seconds 1; $copy = Find-Primary } until (($copy -and $copy.Current.Name -match '^Copy( all)?') -or (Get-Date) -gt $deadline)
if (-not $copy) { throw 'batch did not finish in time' }
"finished: '$($copy.Current.Name)' at {0:N1}s" -f $sw.Elapsed.TotalSeconds

# Card automation names carry the per-file result.
$cards = $root.FindAll([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)))
foreach ($c in $cards) { "card: $($c.Current.Name)" }

if ($Shot) { & (Join-Path $PSScriptRoot 'capture-window.ps1') -ProcessName $proc.ProcessName -Out $Shot }

Invoke $copy
Start-Sleep -Milliseconds 500
$drop = [Windows.Forms.Clipboard]::GetFileDropList()
"clipboard: $($drop.Count) file(s)"
foreach ($f in $drop) { '  {0}  {1:N2} MB' -f $f, ((Get-Item -LiteralPath $f).Length / 1e6) }
Stop-Process -Id $proc.Id
