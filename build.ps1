[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$guardBuildDir = Join-Path $PSScriptRoot 'build'
$guardCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not [Environment]::Is64BitOperatingSystem) { throw 'Only 64-bit Windows is supported.' }
if (-not (Test-Path -LiteralPath $guardCompiler)) { throw 'The .NET Framework C# compiler was not found.' }
New-Item -ItemType Directory -Path $guardBuildDir -Force | Out-Null
$guardOutput = Join-Path $guardBuildDir 'WeChatWindowGuard.exe'
& $guardCompiler /nologo /target:winexe /platform:x64 /optimize+ /debug- /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "/win32manifest:$PSScriptRoot\app.manifest" "/out:$guardOutput" "$PSScriptRoot\WeChatWindowGuard.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output $guardOutput
