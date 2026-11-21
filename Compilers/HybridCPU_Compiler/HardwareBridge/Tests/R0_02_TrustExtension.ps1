#requires -Version 7.3
# R0-02 additional trust boundary; loaded only by HbInt06.CoreGate.ps1.
# Not a Core importer or independent verification authority.
Set-StrictMode -Version Latest

$script:R0_02CoreProject = 'tests/CircuitWeave.CWINT03.BridgeImport/CircuitWeave.CWINT03.BridgeImport.csproj'
$script:R0_02GoldenSha = '6946ccab9d888f7dae0a4d8e18480f436138acd9'

function Assert-R0_02DispatchTrust {
    if ($env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or
        $env:GITHUB_REF -cne 'refs/heads/master' -or
        $env:GITHUB_REPOSITORY -cne 'yuriyyak23/HybridCPU-Compilers' -or
        $env:GITHUB_REF_PROTECTED -cne 'true') {
        throw 'R002UntrustedDispatch'
    }
    if ($env:HB_R0_02_APPROVED_CORE_SHA -cne $script:R002ReviewedCoreSha -or
        $env:HB_R0_02_APPROVED_GOLDEN_SHA -cne $script:R0_02GoldenSha) {
        throw 'R002OwnerApprovalMissing'
    }
}
function Assert-R0_02FineGrainedToken([string]$Value) {
    # Marker is only a syntax filter, NEVER a claim that GitHub authorized repo scopes.
    if ([string]::IsNullOrWhiteSpace($Value) -or
        $Value -cnotmatch '^github_pat_[A-Za-z0-9_]{20,4000}$') {
        throw 'R002FineGrainedTokenRequired'
    }
}
function Assert-R0_02SafeRoot([string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Root) -or -not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw 'R002CheckoutRootAbsent'
    }
    if (((Get-Item -LiteralPath $Root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'R002LinkedCheckoutPath'
    }
    $full = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Root).ProviderPath)
    $cursor = $full
    while ($cursor) {
        $entry = Get-Item -LiteralPath $cursor -Force
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'R002LinkedCheckoutPath'
        }
        $ancestor = [IO.Directory]::GetParent($cursor)
        if ($null -eq $ancestor) { break }
        $cursor = $ancestor.FullName
    }
    $gitDir = Join-Path $full '.git'
    if (-not (Test-Path -LiteralPath $gitDir -PathType Container)) {
        throw 'R002GitDirectoryAbsent'
    }
    if (((Get-Item -LiteralPath $gitDir -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'R002LinkedGitDirectory'
    }
    # checkout@persist-credentials:false must actually remove authorization headers.
    $configPath = Join-Path $gitDir 'config'
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { throw 'R002GitConfigMissing' }
    $configItem = Get-Item -LiteralPath $configPath -Force
    if (($configItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $configItem.Length -gt 65536) { throw 'R002GitConfigUnsafe' }
    $config = [IO.File]::ReadAllText($configPath)
    if ($config -match '(?i)extraheader|authorization|credential[.]helper|github_pat_|ghp_') {
        throw 'R002PersistedCheckoutCredential'
    }
    return $full
}
function Assert-R0_02BridgeCheckout([string]$BridgeRoot, [string]$BridgeSha) {
    Assert-HbCorePin $BridgeSha
    $root = Assert-R0_02SafeRoot $BridgeRoot
    $gitTop = Invoke-HbCoreGit $root @('rev-parse','--show-toplevel')
    if ([IO.Path]::GetFullPath($gitTop).TrimEnd([char[]]@([char]'\',[char]'/')) -ine
        $root.TrimEnd([char[]]@([char]'\',[char]'/'))) {
        throw 'R002WrongBridgeRoot'
    }
    $origin = Invoke-HbCoreGit $root @('remote','get-url','origin')
    if ($origin -cnotin @('https://github.com/yuriyyak23/HybridCPU-Compilers.git',
                           'https://github.com/yuriyyak23/HybridCPU-Compilers')) {
        throw 'R002WrongBridgeOrigin'
    }
    if ((Invoke-HbCoreGit $root @('rev-parse','HEAD')) -cne $BridgeSha) {
        throw 'R002WrongBridgeCheckoutSha'
    }
    if ((Invoke-HbCoreGit $root @('rev-parse','--is-shallow-repository')) -cne 'false') {
        throw 'R002ShallowBridgeCheckout'
    }
    if (Invoke-HbCoreGit $root @('status','--porcelain=v1','--untracked-files=all')) {
        throw 'R002DirtyBridgeCheckout'
    }
}
function Assert-R0_02CoreCheckout([string]$CoreRoot, [string]$CoreSha, [string]$CoreProject) {
    if ($CoreProject -cne $script:R0_02CoreProject) {
        throw 'R002WrongCoreTestHostGraph'
    }
    $null = Assert-R0_02SafeRoot $CoreRoot
    Assert-HbCoreCheckout $CoreRoot $CoreSha $CoreProject
}
function Assert-R0_02UniqueJson([System.Text.Json.JsonElement]$Element) {
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($member in $Element.EnumerateObject()) {
            if (-not $seen.Add($member.Name)) { throw 'R002DuplicateJsonField' }
            Assert-R0_02UniqueJson $member.Value
        }
    } elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        foreach ($member in $Element.EnumerateArray()) { Assert-R0_02UniqueJson $member }
    }
}
function Read-R0_02StrictJson([string]$Path, [long]$MaxBytes) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'R002ReceiptMissing'
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -lt 2 -or $item.Length -gt $MaxBytes) {
        throw 'R002ReceiptUnsafe'
    }
    $json = [IO.File]::ReadAllText($item.FullName, [Text.UTF8Encoding]::new($false, $true))
    $options = [System.Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = 16
    $options.AllowTrailingCommas = $false
    $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
    $doc = [System.Text.Json.JsonDocument]::Parse($json, $options)
    try {
        if ($doc.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw 'R002ReceiptRootInvalid'
        }
        Assert-R0_02UniqueJson $doc.RootElement
        return ($json | ConvertFrom-Json -ErrorAction Stop)
    } finally { $doc.Dispose() }
}
function Assert-R0_02PriorReceipt([string]$Stage, [string]$BridgeSha) {
    $path = Join-Path (Get-R002ReceiptDirectory) ($Stage.ToLowerInvariant() + '.json')
    $r = Read-R0_02StrictJson $path 65536
    if ($r.Schema -cne 'hybridcpu.hardwarebridge.r0-02-core-gate/v1' -or
        $r.Stage -cne $Stage -or $r.State -cne 'Passed' -or
        $r.RequestedCoreSha -cne $script:R002ReviewedCoreSha -or
        $r.ReviewedCoreSha -cne $script:R002ReviewedCoreSha -or
        $r.EventClass -cne 'workflow_dispatch' -or
        $r.BridgeHead -cne $BridgeSha) {
        throw 'R002PriorStageUntrusted'
    }
}
function Assert-R0_02SnapshotPair($Before, $After, [string]$ExpectedHead) {
    if ($null -eq $Before -or $null -eq $After -or
        $Before.Head -cne $ExpectedHead -or $After.Head -cne $ExpectedHead -or
        $Before.GitStatus -cne '' -or $After.GitStatus -cne '' -or
        $Before.TreeSha256 -cnotmatch '^[0-9A-Fa-f]{64}$' -or
        $Before.TreeSha256 -cne $After.TreeSha256 -or
        $Before.GitStatusSha256 -cne $After.GitStatusSha256 -or
        $Before.FileCount -lt 1 -or $Before.FileCount -ne $After.FileCount -or
        $Before.TotalBytes -lt 1 -or $Before.TotalBytes -ne $After.TotalBytes) {
        throw 'R002SourceInventoryChanged'
    }
}
function Assert-R0_02PairedCommands($Receipt, [string]$ReceiptPath, [string]$CoreRoot) {
    # Only commands produced by the owned HB-INT-06 parent and actually executed count.
    $required = @('dotnet-version','v1-protocol-tests','v2-codec-contract',
                  'companion-restore','companion-build','selftests-restore',
                  'selftests-build','companion-selftests','no-write-mutations',
                  'paired-core-importer')
    $records = @($Receipt.Commands)
    if ($records.Count -ne $required.Count) { throw 'R002MissingActualCommand' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $dir = Split-Path -Parent $ReceiptPath
    foreach ($command in $records) {
        $name = [string]$command.Name
        if ($name -cnotin $required) { throw 'R002UnknownCommandRole' }
        if (-not $seen.Add($name)) { throw 'R002DuplicateCommandRole' }
        if ($command.Started -cne $true -or $command.Status -cne 'Passed' -or
            $command.ExitCode -ne 0 -or @($command.Arguments).Count -lt 1 -or
            $command.ExecutableSha256 -cnotmatch '^[0-9A-Fa-f]{64}$' -or
            $command.StdoutSha256 -cnotmatch '^[0-9A-Fa-f]{64}$' -or
            $command.StderrSha256 -cnotmatch '^[0-9A-Fa-f]{64}$') {
            throw 'R002CommandNotExecuted'
        }
        foreach ($stream in @('stdout','stderr')) {
            $file = Join-Path $dir "$name.$stream.txt"
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
                throw 'R002CommandStreamMissing'
            }
            $item = Get-Item -LiteralPath $file -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                $item.Length -gt 16777216) { throw 'R002UnsafeCommandStream' }
            $expectedHash = if ($stream -eq 'stdout') { $command.StdoutSha256 } else { $command.StderrSha256 }
            if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne $expectedHash) {
                throw 'R002CommandStreamChanged'
            }
        }
        if (-not (Test-Path -LiteralPath $command.Executable -PathType Leaf)) {
            throw 'R002ExecutableMissing'
        }
        if ((Get-FileHash -LiteralPath $command.Executable -Algorithm SHA256).Hash -cne
            $command.ExecutableSha256) { throw 'R002ExecutableChanged' }
    }
    foreach ($name in $required) {
        if (-not $seen.Contains($name)) { throw 'R002MissingActualCommand' }
    }
    $coreCommand = @($records | Where-Object { $_.Name -ceq 'paired-core-importer' })[0]
    $args = @($coreCommand.Arguments)
    $expected = [IO.Path]::GetFullPath((Join-Path $CoreRoot $script:R0_02CoreProject))
    if ($args.Count -ne 7 -or $args[0] -cne 'run' -or
        $args[1] -cne '--project' -or $args[3] -cne '--configuration' -or
        $args[4] -cne 'Release' -or $args[5] -cnotlike '-p:ArtifactsPath=*' -or
        $args[6] -cne '-p:UseArtifactsOutput=true' -or
        [IO.Path]::GetFullPath([string]$args[2]) -ine $expected) {
        throw 'R002WrongCoreTestHostGraph'
    }
}
function Assert-R0_02PairedReceipt([string]$Path, [string]$CoreSha,
                                   [string]$BridgeSha, [string]$CoreRoot) {
    $r = Read-R0_02StrictJson $Path 4194304
    if ($r.Schema -cne 'hybridcpu.hardwarebridge.qa-receipt/v1' -or
        $r.Scope -cne 'unqualified-software-facts-only' -or
        $r.CoreHead -cne $CoreSha -or $r.BridgeHead -cne $BridgeSha -or
        $r.GoldenHead -cne $script:R0_02GoldenSha -or
        $r.ExpectedSdkVersion -cne '11.0.100-rc.2.26504.105' -or
        $r.Matrix.StrictCoreImporter -cne 'Passed' -or
        $r.Matrix.CompanionBuild -cne 'Passed' -or
        $r.Matrix.StandaloneCompanion -cne 'Passed' -or
        $r.CoreSourceUnchanged -cne $true -or $r.SourceUnchanged -cne $true) {
        throw 'R002PairedReceiptNotPassed'
    }
    Assert-R0_02SnapshotPair $r.CoreBefore $r.CoreAfter $CoreSha
    Assert-R0_02SnapshotPair $r.GoldenBefore $r.GoldenAfter $script:R0_02GoldenSha
    Assert-R0_02PairedCommands $r $Path $CoreRoot
}
function Invoke-R0_02SyntheticNegatives {
    function Denied([scriptblock]$Test, [string]$Code) {
        $refused = $false
        try { & $Test } catch { $refused = $true }
        if (-not $refused) { throw 'R002NegativeMutationAdmitted' }
        Write-Host "PASS R0-02 negative: $Code"
    }
    # Synthetic tests must remain deterministic even when a future approved dispatch
    # supplies real owner variables. Never inspect or print the private credential.
    $oldEvent = $env:GITHUB_EVENT_NAME
    try {
        $env:GITHUB_EVENT_NAME = 'pull_request'
        Denied { Assert-R0_02DispatchTrust } 'untrusted-pull-request'
    } finally { $env:GITHUB_EVENT_NAME = $oldEvent }
    $oldCoreApproval = $env:HB_R0_02_APPROVED_CORE_SHA
    try {
        $env:HB_R0_02_APPROVED_CORE_SHA = ''
        Denied { Assert-R0_02DispatchTrust } 'dispatch-without-owner-approval'
    } finally { $env:HB_R0_02_APPROVED_CORE_SHA = $oldCoreApproval }
    Denied { Assert-R0_02FineGrainedToken 'synthetic-fake-token' } 'malformed-token-marker'
    Denied { Assert-R0_02CoreCheckout '.' ('0' * 40) '../test.csproj' } 'wrong-test-host'
    $parent = Join-Path ([IO.Path]::GetTempPath()) ("R0_02_" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $parent | Out-Null
    try {
        $duplicate = Join-Path $parent 'duplicate.json'
        [IO.File]::WriteAllText($duplicate, '{"Schema":"x","Nested":{"State":1,"State":2}}')
        Denied { $null = Read-R0_02StrictJson $duplicate 65536 } 'nested-duplicate-json'
        $forged = Join-Path $parent 'forged.json'
        [IO.File]::WriteAllText($forged,
            '{"Schema":"hybridcpu.hardwarebridge.qa-receipt/v1","Scope":"unqualified-software-facts-only","Matrix":{"StrictCoreImporter":"Passed","CompanionBuild":"Passed"},"Commands":[]}')
        Denied { Assert-R0_02PairedReceipt $forged ('0' * 40) ('a' * 40) $parent } 'forged-passed-without-execution'
        Denied { $null = Read-R0_02StrictJson (Join-Path $parent 'missing.json') 65536 } 'missing-prior-receipt'
        $clean = [pscustomobject]@{
            Head = ('a' * 40); GitStatus = ''
            TreeSha256 = ('B' * 64); GitStatusSha256 = ('C' * 64)
            FileCount = 2; TotalBytes = 100
        }
        $changed = [pscustomobject]@{
            Head = ('a' * 40); GitStatus = ''
            TreeSha256 = ('D' * 64); GitStatusSha256 = ('C' * 64)
            FileCount = 2; TotalBytes = 100
        }
        Denied { Assert-R0_02SnapshotPair $clean $changed ('a' * 40) } 'replayed-source-content-changed'
        # A synthetic Git tree is only used to prove refusal. Never treat a
        # synthetic checkout as positive Core access or authorized source.
        $fixture = Join-Path $parent 'synthetic-bridge'
        New-Item -ItemType Directory -Path $fixture | Out-Null
        [IO.File]::WriteAllText((Join-Path $fixture 'marker.txt'), 'R0_02_negative_only')
        $null = Invoke-HbCoreGit $fixture @('init','-q')
        $null = Invoke-HbCoreGit $fixture @('remote','add','origin',
            'https://github.com/yuriyyak23/HybridCPU-Compilers.git')
        $null = Invoke-HbCoreGit $fixture @('add','.')
        $null = Invoke-HbCoreGit $fixture @('-c','user.name=R0_02_QA',
            '-c','user.email=qa@example.invalid','commit','-q','-m','synthetic-negative')
        $head = Invoke-HbCoreGit $fixture @('rev-parse','HEAD')
        Denied { Assert-R0_02BridgeCheckout $fixture ('0' * 40) } 'wrong-bridge-physical-head'
        $configPath = Join-Path $fixture '.git/config'
        $originalConfig = [IO.File]::ReadAllText($configPath)
        try {
            [IO.File]::AppendAllText($configPath,
                [Environment]::NewLine + '[http "https://github.com/"]' +
                [Environment]::NewLine + 'extraheader = AUTHORIZATION: synthetic' + [Environment]::NewLine)
            Denied { $null = Assert-R0_02SafeRoot $fixture } 'persisted-checkout-credential'
        } finally { [IO.File]::WriteAllText($configPath, $originalConfig) }
        $null = Invoke-HbCoreGit $fixture @('remote','set-url','origin',
            'https://github.com/untrusted/other.git')
        Denied { Assert-R0_02BridgeCheckout $fixture $head } 'wrong-bridge-origin'
        $null = Invoke-HbCoreGit $fixture @('remote','set-url','origin',
            'https://github.com/yuriyyak23/HybridCPU-Compilers.git')
        [IO.File]::AppendAllText((Join-Path $fixture 'marker.txt'), 'dirty')
        Denied { Assert-R0_02BridgeCheckout $fixture $head } 'dirty-bridge-checkout'
        $null = Invoke-HbCoreGit $fixture @('checkout','--','marker.txt')
        [IO.File]::WriteAllText((Join-Path $fixture '.git/shallow'), $head + [char]10)
        Denied { Assert-R0_02BridgeCheckout $fixture $head } 'shallow-bridge-checkout'
    }
    finally { Remove-Item -LiteralPath $parent -Force -Recurse }
}
