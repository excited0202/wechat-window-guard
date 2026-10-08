[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$guardAllowed = @('.gitignore', 'README.md', 'LICENSE', 'PRIVACY.md', 'CONTRIBUTING.md', 'WeChatWindowGuard.cs', 'app.manifest', 'build.ps1', 'test.ps1', 'install.ps1', 'verify-publication.ps1')
Push-Location $PSScriptRoot
try {
    $guardFiles = @(& git ls-files --cached)
    if ($LASTEXITCODE -ne 0 -or $guardFiles.Count -eq 0) { throw 'Stage the intended publication files first.' }
    $guardUnexpected = @($guardFiles | Where-Object { $_ -notin $guardAllowed })
    if ($guardUnexpected.Count -gt 0) { throw 'The index contains files outside the publication allowlist.' }
    # Patterns are assembled to avoid matching the checker itself. No secret values are printed.
    $guardRules = @{
        'personal Windows path' = '[A-Z]:[\\/]' + '(Users|Documents and Settings)[\\/]'
        'UNC path' = '[\\]{2}' + '[a-zA-Z0-9][a-zA-Z0-9._-]*[\\]'
        'private IPv4 address' = '\b(?:10\.' + '(?:\d{1,3}\.){2}\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})\b'
        'GitHub token' = '(?:gh[pousr]_' + '[A-Za-z0-9_]{20,}|github_pat_' + '[A-Za-z0-9_]{20,})'
        'private key' = '-----BEGIN ' + '(?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
        'cloud access key' = 'AKIA' + '[A-Z0-9]{16}'
        'email address' = '[A-Za-z0-9._%+-]+@' + '[A-Za-z0-9.-]+\.[A-Za-z]{2,}'
    }
    foreach ($guardFile in $guardFiles) {
        $guardText = (& git show (':' + $guardFile)) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw ('Unable to read staged file: ' + $guardFile) }
        foreach ($guardRule in $guardRules.GetEnumerator()) {
            if ($guardText -match $guardRule.Value) { throw ('Publication check failed: ' + $guardRule.Key + ' in ' + $guardFile) }
        }
    }
    $guardHead = & git rev-parse --verify --quiet HEAD
    if ($LASTEXITCODE -eq 0) {
        foreach ($guardIdentity in @(& git log '--format=%ae%n%ce')) {
            if ($guardIdentity -notmatch '^(?:[A-Za-z0-9+_.-]+@users\.noreply\.github\.com|noreply@github\.com)$') { throw 'A commit author or committer email is not a GitHub noreply address.' }
        }
        foreach ($guardObject in @(& git rev-list --objects --all)) {
            if ($guardObject -match '^[0-9a-f]+ (.+)$' -and $Matches[1] -notin $guardAllowed) { throw 'History contains a file outside the publication allowlist.' }
        }
    }
    Write-Output ('Publication checks passed for {0} staged files. Review content and history manually as well.' -f $guardFiles.Count)
}
finally { Pop-Location }
