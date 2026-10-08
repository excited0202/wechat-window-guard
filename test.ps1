[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$guardBinary = & (Join-Path $PSScriptRoot 'build.ps1')
$guardReport = Join-Path $PSScriptRoot 'build\self-test-result.json'
$guardTest = Start-Process -FilePath $guardBinary -ArgumentList @('--self-test', ('"' + $guardReport + '"')) -WindowStyle Hidden -PassThru -Wait
if ($guardTest.ExitCode -ne 0) { throw 'Self-test failed. Review the local report; do not upload it without redaction.' }
$guardResult = Get-Content -LiteralPath $guardReport -Raw | ConvertFrom-Json
if (-not $guardResult.Success -or $guardResult.Count -lt 15) { throw 'The self-test report was incomplete.' }
Write-Output ('Passed {0} self-tests.' -f $guardResult.Count)
