# Tray icon for Licht.exe (Windows). Uses only what every Windows has (PowerShell, .NET).
# Talks to Licht through standard output, one word per line:
#   ready, open, quit, autostart:on, autostart:off
# Environment: LICHT_EXE (icon source), LICHT_PID (ends together with Licht),
#   LICHT_AUTOSTART=1 (checkbox state), LICHT_HELLO=1 (show a hint once),
#   LICHT_TRAY_SELFTEST=1 (create the icon, report "ready", exit – used by the build).

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

function Send([string]$line) {
  [Console]::Out.WriteLine($line)
  [Console]::Out.Flush()
}

$icon = New-Object System.Windows.Forms.NotifyIcon
try {
  $icon.Icon = [System.Drawing.Icon]::ExtractAssociatedIcon($env:LICHT_EXE)
} catch {
  $icon.Icon = [System.Drawing.SystemIcons]::Application
}
$icon.Text = 'Licht'

$menu = New-Object System.Windows.Forms.ContextMenuStrip

$open = New-Object System.Windows.Forms.ToolStripMenuItem('Licht öffnen')
$open.Font = New-Object System.Drawing.Font($menu.Font, [System.Drawing.FontStyle]::Bold)
$open.add_Click({ Send 'open' })
[void]$menu.Items.Add($open)

$auto = New-Object System.Windows.Forms.ToolStripMenuItem('Mit Windows starten')
$auto.CheckOnClick = $true
$auto.Checked = ($env:LICHT_AUTOSTART -eq '1')
$auto.add_Click({
  if ($auto.Checked) { Send 'autostart:on' } else { Send 'autostart:off' }
})
[void]$menu.Items.Add($auto)

[void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))

$quit = New-Object System.Windows.Forms.ToolStripMenuItem('Beenden')
$quit.add_Click({
  Send 'quit'
  $icon.Visible = $false
  [System.Windows.Forms.Application]::Exit()
})
[void]$menu.Items.Add($quit)

$icon.ContextMenuStrip = $menu
$icon.add_MouseClick({
  param($src, $e)
  if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Left) { Send 'open' }
})
$icon.Visible = $true

if ($env:LICHT_TRAY_SELFTEST -eq '1') {
  Send 'ready'
  $icon.Visible = $false
  $icon.Dispose()
  exit 0
}

# End together with Licht, even if Licht could not say goodbye.
$parent = [int]$env:LICHT_PID
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 2000
$timer.add_Tick({
  if (-not (Get-Process -Id $parent -ErrorAction SilentlyContinue)) {
    $icon.Visible = $false
    [System.Windows.Forms.Application]::Exit()
  }
})
$timer.Start()

if ($env:LICHT_HELLO -eq '1') {
  $icon.ShowBalloonTip(6000, 'Licht läuft', 'Licht läuft jetzt im Hintergrund. Ein Klick auf dieses Symbol öffnet die App, Rechtsklick zeigt weitere Optionen.', [System.Windows.Forms.ToolTipIcon]::Info)
}

Send 'ready'
[System.Windows.Forms.Application]::Run()
$icon.Visible = $false
$icon.Dispose()
