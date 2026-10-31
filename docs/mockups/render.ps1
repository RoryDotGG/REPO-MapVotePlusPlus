# Renders the mock-ups, banner and icon from the HTML in this folder with headless Brave (any Chromium works: -Browser).
# Uses a throwaway browser profile, so your own isn't touched. Usage: ./render.ps1 [-Only mock-lobby]
param(
    [string]$Browser = "$env:ProgramFiles\BraveSoftware\Brave-Browser\Application\brave.exe",
    [string]$Only = ''
)

$jobs = @(
    @{ Page = 'mock-lobby.html'; Out = '..\mock-lobby.png'; Size = '1280,720' },
    @{ Page = 'mock-truck.html'; Out = '..\mock-truck.png'; Size = '1280,720' },
    @{ Page = 'mock-reveal.html'; Out = '..\mock-reveal.png'; Size = '1280,720' },
    @{ Page = 'banner.html'; Out = '..\banner.png'; Size = '1280,640' },
    @{ Page = 'icon.html'; Out = '..\..\icon.png'; Size = '256,256' }
)
$profileDir = Join-Path ([IO.Path]::GetTempPath()) 'mapvote-mockups'

foreach ($job in $jobs) {
    if ($Only -and -not $job.Page.StartsWith($Only)) { continue }
    $out = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $job.Out))
    $url = ([Uri](Join-Path $PSScriptRoot $job.Page)).AbsoluteUri
    $arguments = @(
        '--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
        "--user-data-dir=$profileDir", "--window-size=$($job.Size)", '--timeout=6000', # time for the web fonts
        "--screenshot=$out", $url
    )
    & $Browser @arguments 2>$null | Out-Null # piping makes PowerShell wait for the GUI-subsystem browser
    "$($job.Page) -> $out"
}
