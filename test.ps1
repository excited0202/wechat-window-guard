[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'build'))
$ErrorActionPreference = 'Stop'
$guardBinary = & (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $OutputDirectory
$guardReport = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'self-test-result.json'
$guardTest = Start-Process -FilePath $guardBinary -ArgumentList @('--self-test', ('"' + $guardReport + '"')) -WindowStyle Hidden -PassThru -Wait
if ($guardTest.ExitCode -ne 0) { throw 'Self-test failed. Review the local report; do not upload it without redaction.' }
$guardResult = Get-Content -LiteralPath $guardReport -Raw | ConvertFrom-Json
if (-not $guardResult.Success -or $guardResult.Count -lt 47) { throw 'The self-test report was incomplete.' }
Write-Output ('Passed {0} self-tests.' -f $guardResult.Count)
