# HB-INT-06 test-only source integrity and command receipt primitives.
# No Golden execution, RTL/proof decisions, or trust promotion.
Set-StrictMode -Version Latest

function Get-HbSha256Bytes([byte[]]$Bytes) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes))
}

function Invoke-HbGit([string]$Root, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'git'
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('-C')
    $start.ArgumentList.Add($Root)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'git failed to start' }
    try {
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw 'git inspection timed out'
        }
        if ($process.ExitCode -ne 0) { throw "git inspection failed (exit $($process.ExitCode)): $stderr" }
        return $stdout.TrimEnd([char]13,[char]10)
    }
    finally { $process.Dispose() }
}

function Assert-HbExpectedSha([string]$Actual, [string]$Expected) {
    if ($Expected -cnotmatch '^[0-9a-f]{40}$' -or $Actual -cne $Expected) {
        throw "Pinned repository HEAD mismatch: expected $Expected, actual $Actual"
    }
}

function Assert-HbOutputOutside([string]$Output, [string[]]$ProtectedRoots) {
    $target = [IO.Path]::GetFullPath($Output).TrimEnd([IO.Path]::DirectorySeparatorChar)
    foreach ($root in $ProtectedRoots) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $base = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $relative = [IO.Path]::GetRelativePath($base, $target)
        if ($relative -eq '.' -or
            ($relative -ne '..' -and
             -not $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar) -and
             -not [IO.Path]::IsPathRooted($relative))) {
            throw "Output root overlaps protected source: $base"
        }
    }
    if (Test-Path -LiteralPath $target) { throw "Output root must be new: $target" }
    $current = $target
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Linked output ancestor is prohibited: $current"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
}

function Get-HbSourceState([string]$Root) {
    $path = (Resolve-Path -LiteralPath $Root -ErrorAction Stop).ProviderPath
    $top = Invoke-HbGit $path @('rev-parse', '--show-toplevel')
    if ([IO.Path]::GetFullPath($top).TrimEnd('\','/') -ine
        [IO.Path]::GetFullPath($path).TrimEnd('\','/')) {
        throw 'Golden root must be the exact Git repository top-level'
    }
    $head = Invoke-HbGit $path @('rev-parse', 'HEAD')
    if ($head -cnotmatch '^[0-9a-f]{40}$') { throw 'Invalid Git HEAD identity' }
    $status = Invoke-HbGit $path @('status', '--porcelain=v1', '--untracked-files=all')
    $lines = [Collections.Generic.List[string]]::new()
    $count = 0
    $totalBytes = [long]0
    $items = Get-ChildItem -LiteralPath $path -Force -Recurse -ErrorAction Stop
    foreach ($item in $items) {
        $relative = [IO.Path]::GetRelativePath($path, $item.FullName).Replace('\','/')
        if ($relative -eq '.git' -or $relative.StartsWith('.git/', [StringComparison]::Ordinal)) {
            continue
        }
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Linked source entry prohibited (symlink/junction): $relative"
        }
        if ($item.PSIsContainer) { continue }
        $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        $lines.Add($relative + [char]9 + $item.Length + [char]9 + $hash)
        $totalBytes += $item.Length
        $count++
    }
    $lines.Sort([StringComparer]::Ordinal)
    $preimage = [string]::Join([char]10, $lines) + [char]10
    return [pscustomobject]@{
        Head = $head
        GitStatus = $status
        GitStatusSha256 = Get-HbSha256Bytes ([Text.Encoding]::UTF8.GetBytes($status))
        TreeSha256 = Get-HbSha256Bytes ([Text.Encoding]::UTF8.GetBytes($preimage))
        FileCount = $count
        TotalBytes = $totalBytes
        Files = @($lines.ToArray())
    }
}

function Assert-HbSourceUnchanged($Before, $After) {
    if ($Before.Head -cne $After.Head -or
        $Before.GitStatus -cne $After.GitStatus -or
        $Before.TreeSha256 -cne $After.TreeSha256 -or
        $Before.FileCount -ne $After.FileCount -or
        $Before.TotalBytes -ne $After.TotalBytes) {
        throw 'Golden source changed between preflight and postflight: fail closed'
    }
}

function Assert-HbCommandReceipt($Receipt) {
    if ($Receipt.Status -eq 'Passed') {
        if (-not $Receipt.Started -or $Receipt.ExitCode -ne 0 -or
            $Receipt.Arguments.Count -eq 0 -or
            $Receipt.ExecutableSha256 -cnotmatch '^[0-9A-F]{64}$' -or
            $Receipt.StdoutSha256 -cnotmatch '^[0-9A-F]{64}$' -or
            $Receipt.StderrSha256 -cnotmatch '^[0-9A-F]{64}$') {
            throw 'Fake/zero-step command receipt cannot be marked Passed'
        }
    }
    if ($Receipt.Status -notin @('Passed','Failed','Blocked','NotRun')) {
        throw 'Unrecognized command receipt status'
    }
}

function Invoke-HbRecordedCommand(
    [string]$Name, [string]$Executable, [string[]]$Arguments,
    [string]$WorkingDirectory, [string]$OutputRoot, [int]$TimeoutSeconds = 900) {
    $resolved = Get-Command $Executable -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $resolved) {
        return [pscustomobject]@{
            Name = $Name; Status = 'NotRun'; Started = $false; ExitCode = $null
            Executable = $Executable; Arguments = @($Arguments)
            ExecutableSha256 = $null; StdoutSha256 = $null; StderrSha256 = $null
            Reason = 'Required executable not installed'
        }
    }
    $exePath = $resolved.Source
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $exePath
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw "Command could not start: $Name" }
    try {
        $outTask = $process.StandardOutput.ReadToEndAsync()
        $errTask = $process.StandardError.ReadToEndAsync()
        $finished = $process.WaitForExit($TimeoutSeconds * 1000)
        if (-not $finished) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        $stdout = $outTask.GetAwaiter().GetResult()
        $stderr = $errTask.GetAwaiter().GetResult()
        $encoding = [Text.UTF8Encoding]::new($false)
        $outFile = Join-Path $OutputRoot "$Name.stdout.txt"
        $errFile = Join-Path $OutputRoot "$Name.stderr.txt"
        [IO.File]::WriteAllText($outFile, $stdout, $encoding)
        [IO.File]::WriteAllText($errFile, $stderr, $encoding)
        $exit = if ($finished) { $process.ExitCode } else { $null }
        $record = [pscustomobject]@{
            Name = $Name
            Status = if (-not $finished) { 'Blocked' } elseif ($exit -eq 0) { 'Passed' } else { 'Failed' }
            Started = $true
            ExitCode = $exit
            Executable = $exePath
            Arguments = @($Arguments)
            ExecutableSha256 = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash
            StdoutSha256 = (Get-FileHash -LiteralPath $outFile -Algorithm SHA256).Hash
            StderrSha256 = (Get-FileHash -LiteralPath $errFile -Algorithm SHA256).Hash
            Reason = if (-not $finished) { "Timed out after $TimeoutSeconds seconds; process tree killed" } else { $null }
        }
        Assert-HbCommandReceipt $record
        return $record
    }
    finally { $process.Dispose() }
}
