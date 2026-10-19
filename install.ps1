[CmdletBinding()]
param(
    [switch]$NoDesktopShortcut,
    [switch]$FrameRepairOnly,
    [string]$InstallDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WeChatWindowGuard'),
    [string]$BuildOutputDirectory = (Join-Path $PSScriptRoot 'build')
)
$ErrorActionPreference = 'Stop'
$guardBuiltExe = & (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $BuildOutputDirectory
$guardInstallDir = [IO.Path]::GetFullPath($InstallDirectory)
$guardStartupDir = [Environment]::GetFolderPath('Startup')
$guardProgramsDir = Join-Path ([Environment]::GetFolderPath('Programs')) '微信窗口修复'
$guardDesktopDir = [Environment]::GetFolderPath('Desktop')
$guardExe = Join-Path $guardInstallDir 'WeChatWindowGuard.exe'
$guardIcon = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Tencent\Weixin\Weixin.exe'
$guardShell = New-Object -ComObject WScript.Shell
if ($FrameRepairOnly) {
    # Side-by-side manual entry only. Never stop/replace/start the installed watch process.
    $guardFrameExe = Join-Path $guardInstallDir 'WeChatWindowFrameRepair.exe'
    $guardFrameLinks = @((Join-Path $guardProgramsDir '微信白边与最大化修复.lnk'))
    if (-not $NoDesktopShortcut) { $guardFrameLinks += (Join-Path $guardDesktopDir '微信白边与最大化修复.lnk') }
    foreach ($guardFrameLinkPath in $guardFrameLinks) {
        if (Test-Path -LiteralPath $guardFrameLinkPath) {
            $guardExisting = $guardShell.CreateShortcut($guardFrameLinkPath)
            if ($guardExisting.TargetPath -ine $guardFrameExe -or $guardExisting.Arguments -ne '--repair-frame-ui') {
                throw ('A shortcut belongs to another program or mode: ' + $guardFrameLinkPath)
            }
        }
    }
    New-Item -ItemType Directory -Path $guardInstallDir -Force | Out-Null
    New-Item -ItemType Directory -Path $guardProgramsDir -Force | Out-Null
    if (Test-Path -LiteralPath $guardFrameExe) {
        $guardFrameRunning = @(Get-Process -Name WeChatWindowFrameRepair -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $guardFrameExe })
        if ($guardFrameRunning.Count -gt 0) { throw 'The manual repair entry is still running. Close its result dialog before updating.' }
        Copy-Item -LiteralPath $guardFrameExe -Destination (Join-Path $guardInstallDir ('WeChatWindowFrameRepair-backup-' + [Guid]::NewGuid().ToString('N') + '.exe'))
    }
    Copy-Item -LiteralPath $guardBuiltExe -Destination $guardFrameExe
    # Keep original installed watch documentation intact as well.
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $guardInstallDir 'FrameRepair-README.md')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PRIVACY.md') -Destination (Join-Path $guardInstallDir 'FrameRepair-PRIVACY.md')
    foreach ($guardFrameLinkPath in $guardFrameLinks) {
        $guardLink = $guardShell.CreateShortcut($guardFrameLinkPath)
        $guardLink.TargetPath = $guardFrameExe
        $guardLink.Arguments = '--repair-frame-ui'
        $guardLink.WorkingDirectory = $guardInstallDir
        $guardLink.WindowStyle = 7
        $guardLink.Description = '手动恢复已验证的微信白边与最大化越界异常；不退出登录，不新增自动任务'
        $guardLink.IconLocation = if (Test-Path -LiteralPath $guardIcon) { $guardIcon + ',0' } else { $guardFrameExe + ',0' }
        $guardLink.Save()
    }
    Write-Output 'Installed the manual frame-repair entry only. The existing watch process and startup shortcuts were not changed.'
    return
}
$guardLinks = @(
    @{ Path = (Join-Path $guardStartupDir 'WeChatWindowGuard.lnk'); Arguments = '--watch'; Description = '自动修复微信主窗口留在已断开的显示器区域的问题' },
    @{ Path = (Join-Path $guardProgramsDir '微信窗口救援.lnk'); Arguments = '--recover'; Description = '恢复已经登录的微信主窗口' },
    @{ Path = (Join-Path $guardProgramsDir '停用微信窗口自动修复.lnk'); Arguments = '--disable'; Description = '停止修复并移出开机启动项；保留程序及记录' }
)
if (-not $NoDesktopShortcut) {
    $guardLinks += @{ Path = (Join-Path $guardDesktopDir '微信窗口救援.lnk'); Arguments = '--recover'; Description = '恢复已经登录的微信窗口，不新开登录入口' }
}
# Validate all existing shortcuts before changing any installation files.
foreach ($guardEntry in $guardLinks) {
    if (Test-Path -LiteralPath $guardEntry.Path) {
        $guardExisting = $guardShell.CreateShortcut($guardEntry.Path)
        if ($guardExisting.TargetPath -ine $guardExe) { throw ('A shortcut belongs to another program: ' + $guardEntry.Path) }
    }
}
New-Item -ItemType Directory -Path $guardInstallDir -Force | Out-Null
New-Item -ItemType Directory -Path $guardProgramsDir -Force | Out-Null
if (Test-Path -LiteralPath $guardExe) {
    $guardStopped = Start-Process -FilePath $guardExe -ArgumentList '--stop' -WindowStyle Hidden -PassThru -Wait
    if ($guardStopped.ExitCode -ne 0) { throw 'The existing guard could not be stopped.' }
    Start-Sleep -Seconds 3
    $guardBackupName = 'WeChatWindowGuard-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N') + '.exe'
    Copy-Item -LiteralPath $guardExe -Destination (Join-Path $guardInstallDir $guardBackupName)
}
Copy-Item -LiteralPath $guardBuiltExe -Destination $guardExe
foreach ($guardDocument in @('README.md', 'LICENSE', 'PRIVACY.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $guardDocument) -Destination (Join-Path $guardInstallDir $guardDocument)
}
foreach ($guardEntry in $guardLinks) {
    $guardLink = $guardShell.CreateShortcut($guardEntry.Path)
    $guardLink.TargetPath = $guardExe
    $guardLink.Arguments = $guardEntry.Arguments
    $guardLink.WorkingDirectory = $guardInstallDir
    $guardLink.WindowStyle = 7
    $guardLink.Description = $guardEntry.Description
    $guardLink.IconLocation = if (Test-Path -LiteralPath $guardIcon) { $guardIcon + ',0' } else { $guardExe + ',0' }
    $guardLink.Save()
}
Start-Process -FilePath $guardExe -ArgumentList '--watch' -WindowStyle Hidden
Write-Output 'Installed. Use the rescue shortcut to restore the existing Weixin window.'
