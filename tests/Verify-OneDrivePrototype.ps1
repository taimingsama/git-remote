param(
    [Parameter(Mandatory = $true)][string]$HelperPath,
    [switch]$Live
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$helper = (Resolve-Path -LiteralPath $HelperPath).ProviderPath
$name = "gitremote-prototype-$([Guid]::NewGuid().ToString('N')).git"
$temp = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).ProviderPath
$scratch = Join-Path $temp ([IO.Path]::GetFileNameWithoutExtension($name))
$remote = "onedrive-prototype::$name"
$oldPath = $env:PATH
$oldFixture = $env:GITREMOTE_PROTOTYPE_FIXTURE_DIRECTORY
$oldToken = $env:GITREMOTE_PROTOTYPE_ACCESS_TOKEN
$cloudCreated = $false

function Invoke-Git {
    & git @args
    if ($LASTEXITCODE -ne 0) { throw "Git failed with exit code $LASTEXITCODE." }
}

function Assert-Equal($Expected, $Actual, $Subject) {
    if ($Expected -cne $Actual) { throw "$Subject did not match." }
}

function Save-GraphFile($Path, $Destination) {
    $encoded = (($Path -split '/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
    for ($attempt = 1; ; $attempt++) {
        try {
            Invoke-WebRequest -UseBasicParsing -Uri "${itemUrl}/${encoded}:/content" -Headers $headers -OutFile $Destination -TimeoutSec 30
            return
        } catch [Net.WebException] {
            if ($attempt -ge 3 -or $_.Exception.Response) { throw }
            Start-Sleep -Milliseconds (250 * $attempt)
        }
    }
}

function Assert-ConcurrentBranchWrites($Source, $Name, $Remote, $Commit, $Initial, $Topic, [switch]$Update) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $helper
    $start.Arguments = "prototype $Name"
    $start.WorkingDirectory = $Source
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $process.StandardInput.WriteLine('list for-push')
        $process.StandardInput.Flush()
        do {
            $read = $process.StandardOutput.ReadLineAsync()
            if (-not $read.Wait(10000) -or $null -eq $read.Result) { throw 'The helper did not finish listing refs.' }
        } while ($read.Result.Length -ne 0)

        $target = if ($Update) { 'refs/heads/main' } else { 'refs/heads/race' }
        $winner = if ($Update) { $Initial } else { $Commit }
        $loser = if ($Update) {
            $Topic
        } else {
            Invoke-Git -C $Source -c user.name=Prototype -c user.email=prototype@example.invalid -c commit.gpgsign=false commit-tree "$Initial^{tree}" -p $Initial -m 'Stale create'
        }
        Invoke-Git -C $Source push $Remote "+${winner}:$target"
        $beforeLoser = (Read-FixtureManifest (Join-Path $scratch $name)).Version
        $process.StandardInput.WriteLine('list')
        $process.StandardInput.WriteLine("push +${loser}:$target")
        if ($Update) {
            $process.StandardInput.WriteLine("push ${Commit}:refs/heads/independent")
        }
        $process.StandardInput.WriteLine()
        $process.StandardInput.Close()
        $reply = $process.StandardOutput.ReadToEndAsync()
        if (-not $process.WaitForExit(10000) -or $process.ExitCode -ne 0 -or
            ($reply.Result -split '\r?\n') -cnotcontains "error $target remote refs changed; retry") {
            throw 'The helper did not reject the stale update.'
        }
        Assert-Equal "$winner`t$target" (Invoke-Git ls-remote $Remote $target) 'Winning push'
        if ($Update) {
            if (($reply.Result -split '\r?\n') -cnotcontains 'ok refs/heads/independent') {
                throw 'The stale ref blocked an unrelated ref in the same batch.'
            }
            Assert-Equal "$Commit`trefs/heads/independent" (Invoke-Git ls-remote $Remote refs/heads/independent) 'Independent push'
        }
        if (-not $Update) {
            $afterLoser = Read-FixtureManifest (Join-Path $scratch $name)
            if ($afterLoser.Version -ceq $beforeLoser) { throw 'The stale create did not leave a published orphan pack.' }
        }
    } finally {
        if (-not $process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
}

function Read-FixtureManifest($Snapshot) {
    $version = [IO.File]::ReadAllText((Join-Path $Snapshot 'metadata/current-manifest'))
    Get-Content -Raw -LiteralPath (Join-Path $Snapshot "manifests/manifest-$version.json") | ConvertFrom-Json
}

function Assert-FetchFailure($Commit, $ExpectedMessage) {
    $probe = Join-Path $scratch ("probe-" + [Guid]::NewGuid().ToString('N'))
    Invoke-Git init -q --object-format=sha1 $probe
    Push-Location -LiteralPath $probe
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $reply = @("fetch $Commit refs/heads/main", '') | & $helper prototype $name 2>&1
        if ($LASTEXITCODE -eq 0 -or "$reply" -notlike "*$ExpectedMessage*") {
            throw "Fetch did not report $ExpectedMessage. $reply"
        }
    } finally {
        $ErrorActionPreference = $previous
        Pop-Location
    }
}

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    if ($Live) {
        $env:GITREMOTE_PROTOTYPE_FIXTURE_DIRECTORY = $null
        if (-not $env:GITREMOTE_PROTOTYPE_ACCESS_TOKEN) {
            $settings = Join-Path $PSScriptRoot 'GitRemote.OneDrive.IntegrationTests/Graph.local.runsettings'
            if (Test-Path -LiteralPath $settings) {
                $localSettings = [xml](Get-Content -LiteralPath $settings -Raw)
                $env:GITREMOTE_PROTOTYPE_ACCESS_TOKEN =
                    $localSettings.RunSettings.RunConfiguration.EnvironmentVariables.ONEDRIVE_TEST_ACCESS_TOKEN
            }
        }
        if ([string]::IsNullOrWhiteSpace($env:GITREMOTE_PROTOTYPE_ACCESS_TOKEN)) { throw 'Set a OneDrive test access token before using -Live.' }
        $env:GITREMOTE_PROTOTYPE_ACCESS_TOKEN = $env:GITREMOTE_PROTOTYPE_ACCESS_TOKEN.Trim()
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $headers = @{ Authorization = "Bearer $env:GITREMOTE_PROTOTYPE_ACCESS_TOKEN" }
        $drive = Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/me/drive?$select=id' -Headers $headers -TimeoutSec 30
        $driveUrl = "https://graph.microsoft.com/v1.0/drives/$([Uri]::EscapeDataString($drive.id))"
        $itemUrl = "${driveUrl}/root:/${name}"
    } else {
        if ((Split-Path -Leaf $helper) -ne 'GitRemote.OneDrive.Prototype.TestHost.exe') {
            throw 'For offline verification, pass the GitRemote.OneDrive.Prototype.TestHost.exe build output.'
        }
        $fixtureBin = Join-Path $scratch 'helper'
        New-Item -ItemType Directory -Path $fixtureBin | Out-Null
        Copy-Item -Path (Join-Path (Split-Path -Parent $helper) '*') -Destination $fixtureBin -Recurse
        $helper = Join-Path $fixtureBin 'git-remote-onedrive-prototype.exe'
        Copy-Item -LiteralPath (Join-Path $fixtureBin 'GitRemote.OneDrive.Prototype.TestHost.exe') -Destination $helper -Force
        $env:GITREMOTE_PROTOTYPE_ACCESS_TOKEN = $null
        $env:GITREMOTE_PROTOTYPE_FIXTURE_DIRECTORY = $scratch
    }
    $env:PATH = "$(Split-Path -Parent $helper);$oldPath"

    # The name is unique to this invocation. Clean up even if initialization only partly succeeds.
    $cloudCreated = $Live.IsPresent
    & $helper --init $remote
    if ($LASTEXITCODE -ne 0) { throw 'Remote initialization failed.' }
    if (Invoke-Git ls-remote $remote) { throw 'A newly initialized remote advertised refs.' }

    $source = Join-Path $scratch 'source'
    $clone = Join-Path $scratch 'helper-clone'
    Invoke-Git init --object-format=sha1 --initial-branch=main $source | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $source 'nested') | Out-Null
    $payload = 'nested/payload.bin'
    [IO.File]::WriteAllBytes((Join-Path $source $payload), [byte[]](0, 255, 13, 10, 128, 0))
    Invoke-Git -C $source add -- $payload
    Invoke-Git -C $source -c user.name=Prototype -c user.email=prototype@example.invalid -c commit.gpgsign=false commit -m Initial | Out-Null
    $expected = Invoke-Git -C $source rev-parse HEAD
    $expectedBytes = (Get-FileHash -LiteralPath (Join-Path $source $payload)).Hash
    Invoke-Git -C $source push $remote refs/heads/main:refs/heads/main
    $remoteRoot = Join-Path $scratch $name
    if (-not $Live) {
        $initialManifest = Read-FixtureManifest $remoteRoot
        $packLog = Join-Path $scratch 'pack-downloads.log'
    }
    $advertised = Invoke-Git ls-remote --symref $remote
    if ($advertised -cnotcontains "ref: refs/heads/main`tHEAD" -or
        $advertised -cnotcontains "$expected`trefs/heads/main") { throw 'Remote HEAD or main was not published correctly.' }

    Invoke-Git clone $remote $clone
    Assert-Equal $expected (Invoke-Git -C $clone rev-parse HEAD) 'Helper clone commit'
    Assert-Equal $expectedBytes (Get-FileHash -LiteralPath (Join-Path $clone $payload)).Hash 'Helper clone payload'
    Invoke-Git -C $clone fsck --full

    $initial = $expected
    Invoke-Git -C $source switch -c topic
    Invoke-Git -C $source -c user.name=Prototype -c user.email=prototype@example.invalid -c commit.gpgsign=false commit --allow-empty -m Topic | Out-Null
    $topic = Invoke-Git -C $source rev-parse HEAD
    Invoke-Git -C $source switch main
    [IO.File]::WriteAllBytes((Join-Path $source $payload), [byte[]](3, 254, 10, 13, 129, 0))
    Invoke-Git -C $source add -- $payload
    Invoke-Git -C $source -c user.name=Prototype -c user.email=prototype@example.invalid -c commit.gpgsign=false commit -m Update | Out-Null
    $expected = Invoke-Git -C $source rev-parse HEAD
    $expectedBytes = (Get-FileHash -LiteralPath (Join-Path $source $payload)).Hash
    Invoke-Git -C $source push $remote refs/heads/main:refs/heads/main refs/heads/topic:refs/heads/topic
    Invoke-Git -C $source push $remote refs/heads/main:refs/heads/main
    if (-not $Live) {
        $incrementalManifest = Read-FixtureManifest $remoteRoot
        Assert-Equal 2 $incrementalManifest.Packs.Count 'Two branch changes added one shared pack'
        $mainPack = $incrementalManifest.Packs[1].Name
        $initialPack = $incrementalManifest.Packs[0].Name
        $batchContents = Invoke-Git verify-pack -v (Join-Path $remoteRoot "objects/pack/$mainPack.idx")
        if (($batchContents -like "$initial *") -or -not ($batchContents -like "$expected *") -or
            -not ($batchContents -like "$topic *")) {
            throw 'The shared pack did not include both new tips and exclude their verified parent.'
        }
    }

    # Exercise independent outcomes even when a single batch contains invalid updates.
    Push-Location -LiteralPath $source
    try {
        $rejected = 'refs/heads/rejected'
        $response = @('list for-push', "push ${initial}:refs/heads/main",
            'push refs/heads/missing-source:refs/heads/invalid-source', "push ${initial}:$rejected",
            "push ${initial}:$rejected/sub", "push ${topic}:refs/heads/topic",
            "push ${initial}:refs/heads/duplicate", "push ${topic}:refs/heads/duplicate", '') |
            & $helper prototype $name
        if ($LASTEXITCODE -ne 0) { throw 'The helper failed to process the mixed push batch.' }
        $expectedReplies = @(
            'error refs/heads/main non-fast-forward'
            'error refs/heads/invalid-source invalid local source'
            "ok $rejected"
            "error $rejected/sub branch name conflicts with an existing branch"
            'ok refs/heads/topic'
            'error refs/heads/duplicate duplicate destination branch'
            'error refs/heads/duplicate duplicate destination branch'
        )
        $actualReplies = $response | Where-Object { $_ -match '^(ok|error) ' }
        Assert-Equal ($expectedReplies -join "`n") ($actualReplies -join "`n") 'Push replies in command order'

        if (-not $Live) {
            $manifestBeforeNoChange = (Read-FixtureManifest $remoteRoot).Version
            $catalogPath = Join-Path $remoteRoot 'metadata/current-refs'
            $refsBeforeNoChange = [IO.File]::ReadAllText($catalogPath)
        }
        $response = @('list for-push', "push ${topic}:refs/heads/topic", 'push :refs/heads/absent', '') |
            & $helper prototype $name
        if ($LASTEXITCODE -ne 0 -or $response -cnotcontains 'ok refs/heads/topic' -or
            $response -cnotcontains 'ok refs/heads/absent') {
            throw 'The helper did not report both no-change operations as successful.'
        }
        $response = @('list for-push', "push ${topic}:refs/heads/topic", 'push :refs/heads/topic', '') |
            & $helper prototype $name
        if ($LASTEXITCODE -ne 0 -or
            @($response -ceq 'error refs/heads/topic duplicate destination branch').Count -ne 2) {
            throw 'The helper did not reject a delete and a no-change request for the same destination.'
        }
        if (-not $Live) {
            Assert-Equal $manifestBeforeNoChange (Read-FixtureManifest $remoteRoot).Version 'No-change manifest'
            Assert-Equal $refsBeforeNoChange ([IO.File]::ReadAllText($catalogPath)) 'No-change refs'
        }
    } finally {
        Pop-Location
    }
    Push-Location -LiteralPath $clone
    try {
        $response = @('list for-push', "push ${initial}:refs/heads/main", '') | & $helper prototype $name
        if ($LASTEXITCODE -ne 0 -or $response -cnotcontains 'error refs/heads/main fetch first') {
            throw 'The helper did not request a fetch when the remote branch tip was missing locally.'
        }
    } finally {
        Pop-Location
    }
    if (-not $Live) { Clear-Content -LiteralPath $packLog }
    Invoke-Git -C $clone gc
    Invoke-Git -C $clone fetch origin main
    Assert-Equal $initial (Invoke-Git -C $clone rev-parse HEAD) 'Fetch left the working branch unchanged'
    if (-not $Live) {
        $packReads = @(Get-Content -LiteralPath $packLog)
        Assert-Equal 1 $packReads.Count 'Main-only fetch pack count'
        Assert-Equal "$name/objects/pack/$mainPack.pack" $packReads[0] 'Main-only fetch pack identity'
        Invoke-Git -C $clone fetch origin main
        Assert-Equal 1 @(Get-Content -LiteralPath $packLog).Count 'Repeat fetch skipped pack downloads'
    }
    Invoke-Git -C $clone fetch origin
    Assert-Equal "$initial`t$rejected" (Invoke-Git ls-remote $remote $rejected) 'A valid ref was published despite sibling rejections'
    if (Invoke-Git ls-remote $remote "$rejected/sub") { throw 'A conflicting ref was published.' }
    if (Invoke-Git ls-remote $remote 'refs/heads/duplicate') { throw 'A duplicate destination was published.' }
    Assert-Equal $expected (Invoke-Git -C $clone rev-parse refs/remotes/origin/main) 'Remote main after rejected push'
    Assert-Equal $topic (Invoke-Git -C $clone rev-parse refs/remotes/origin/topic) 'Remote topic after main update'
    Invoke-Git -C $clone merge --ff-only origin/main | Out-Null
    Assert-Equal $expected (Invoke-Git -C $clone rev-parse HEAD) 'Updated helper clone commit'
    Assert-Equal $expectedBytes (Get-FileHash -LiteralPath (Join-Path $clone $payload)).Hash 'Updated helper clone payload'

    $snapshot = Join-Path $scratch 'native-snapshot.git'
    Invoke-Git init --bare --object-format=sha1 --initial-branch=main $snapshot | Out-Null
    if ($Live) {
        $next = "${itemUrl}/objects/pack:/children?" + '$select=name'
        while ($next) {
            $page = Invoke-RestMethod -Uri $next -Headers $headers -TimeoutSec 30
            foreach ($item in $page.value) {
                if ($item.name -cmatch '^pack-[0-9a-f]{40}\.(pack|idx)$') {
                    $relative = "objects/pack/$($item.name)"
                    Save-GraphFile $relative (Join-Path $snapshot $relative)
                }
            }
            $next = $page.'@odata.nextLink'
        }
    } else {
        Copy-Item -Path (Join-Path $remoteRoot 'objects/pack/*') -Destination (Join-Path $snapshot 'objects/pack')
    }
    [IO.File]::WriteAllText((Join-Path $snapshot 'packed-refs'), "# pack-refs with: sorted`n$expected refs/heads/main`n$topic refs/heads/topic`n")
    Invoke-Git --git-dir=$snapshot fsck --full
    Assert-Equal $topic (Invoke-Git --git-dir=$snapshot rev-parse refs/heads/topic) 'Bare remote topic'
    $nativeClone = Join-Path $scratch 'native-clone'
    Invoke-Git clone --no-local $snapshot $nativeClone
    Assert-Equal $expected (Invoke-Git -C $nativeClone rev-parse HEAD) 'Native clone commit'
    Assert-Equal $expectedBytes (Get-FileHash -LiteralPath (Join-Path $nativeClone $payload)).Hash 'Native clone payload'

    if (-not $Live) {
        $mainPackPath = Join-Path $remoteRoot "objects/pack/$mainPack.pack"
        $mainIndexPath = Join-Path $remoteRoot "objects/pack/$mainPack.idx"
        $originalPack = [IO.File]::ReadAllBytes($mainPackPath)
        $originalIndex = [IO.File]::ReadAllBytes($mainIndexPath)
        try {
            $corrupt = [byte[]]$originalPack.Clone()
            $corrupt[15] = $corrupt[15] -bxor 1
            [IO.File]::WriteAllBytes($mainPackPath, $corrupt)
            Assert-FetchFailure $expected 'Invalid pack data'
            [IO.File]::WriteAllBytes($mainPackPath, $originalPack)
            [IO.File]::WriteAllBytes($mainIndexPath, [IO.File]::ReadAllBytes((Join-Path $remoteRoot "objects/pack/$initialPack.idx")))
            Assert-FetchFailure $expected 'Invalid index'
        } finally {
            [IO.File]::WriteAllBytes($mainPackPath, $originalPack)
            [IO.File]::WriteAllBytes($mainIndexPath, $originalIndex)
        }
        Assert-FetchFailure ('1' * 40) 'Remote objects unavailable'

        $manifestPath = 'metadata/current-manifest'
        $pointerFile = Join-Path $remoteRoot $manifestPath
        $beforeConcurrentPush = (Read-FixtureManifest $remoteRoot).Version
        Invoke-Git -C $source push $remote 'refs/heads/main:refs/heads/concurrent-push'
        $concurrentManifest = Read-FixtureManifest $remoteRoot
        if ($concurrentManifest.Version -ceq $beforeConcurrentPush) { throw 'The concurrent push did not publish a pack.' }
        $casSwap = Join-Path $scratch 'swap-manifest-on-cas'

        [IO.File]::WriteAllText($pointerFile, $beforeConcurrentPush)
        [IO.File]::WriteAllText($casSwap, $concurrentManifest.Version)
        Invoke-Git -C $source push $remote 'refs/heads/topic:refs/heads/push-cas-probe'
        $mergedManifest = Read-FixtureManifest $remoteRoot
        Assert-Equal ($concurrentManifest.Packs.Count + 1) $mergedManifest.Packs.Count 'Push merged into the concurrent manifest'
        foreach ($entry in $concurrentManifest.Packs) {
            if ($mergedManifest.Packs.Name -cnotcontains $entry.Name) { throw 'Push dropped a pack from the concurrent manifest.' }
        }
        Assert-Equal "$topic`trefs/heads/push-cas-probe" (Invoke-Git ls-remote $remote refs/heads/push-cas-probe) 'Push published its ref after the manifest retry'

        Assert-Equal "$expected`trefs/heads/concurrent-push" (Invoke-Git ls-remote $remote refs/heads/concurrent-push) 'The concurrent push ref remained published'

        [IO.File]::WriteAllText($pointerFile, $beforeConcurrentPush)
        [IO.File]::WriteAllLines((Join-Path $scratch 'swap-manifest-on-pack-read'), @("$name/$manifestPath", $mergedManifest.Version))
        Clear-Content -LiteralPath $packLog
        $retryClone = Join-Path $scratch 'retry-clone'
        Invoke-Git clone --single-branch $remote $retryClone
        Assert-Equal $expected (Invoke-Git -C $retryClone rev-parse HEAD) 'Fetch recovered after a manifest switch'
        $alternatePack = $concurrentManifest.Packs | Select-Object -Last 1
        Assert-Equal "$name/objects/pack/$($alternatePack.Name).pack" (Get-Content -LiteralPath $packLog | Select-Object -Last 1) 'Fetch used the newly published pack'
        Invoke-Git -C $retryClone fsck --full

        Assert-ConcurrentBranchWrites $source $name $remote $expected $initial $topic
        Assert-ConcurrentBranchWrites $source $name $remote $expected $initial $topic -Update
        Invoke-Git -C $source push $remote ':refs/heads/race'
        if (Invoke-Git ls-remote $remote refs/heads/race) { throw 'Branch deletion was not published.' }
        Invoke-Git -C $source push $remote refs/heads/main:refs/heads/main
        Assert-Equal "$expected`trefs/heads/main" (Invoke-Git ls-remote $remote refs/heads/main) 'Fast-forward after force push'

        $manifestBeforeFailure = (Read-FixtureManifest $remoteRoot).Version
        $failUpload = Join-Path $scratch 'fail-pack-upload'
        New-Item -ItemType File -Path $failUpload | Out-Null
        try {
            & git -C $source push $remote 'refs/heads/main:refs/heads/interrupted' ':refs/heads/independent'
            if ($LASTEXITCODE -eq 0) { throw 'An interrupted upload was accepted.' }
        } finally {
            Remove-Item -LiteralPath $failUpload
        }
        Assert-Equal $manifestBeforeFailure (Read-FixtureManifest $remoteRoot).Version 'Interrupted upload did not replace the manifest'
        if (Invoke-Git ls-remote $remote refs/heads/interrupted) { throw 'Interrupted upload published its ref.' }
        if (Invoke-Git ls-remote $remote refs/heads/independent) { throw 'A failed upload blocked a separate branch deletion.' }

        $large = New-Object byte[] (17 * 1024 * 1024)
        (New-Object Random 1729).NextBytes($large)
        [IO.File]::WriteAllBytes((Join-Path $source 'large.bin'), $large)
        $similar = New-Object byte[] (256 * 1024)
        [Array]::Copy($large, $similar, $similar.Length)
        [IO.File]::WriteAllBytes((Join-Path $source 'delta-a.bin'), $similar)
        $similar[1000] = $similar[1000] -bxor 1
        [IO.File]::WriteAllBytes((Join-Path $source 'delta-b.bin'), $similar)
        Invoke-Git -C $source add -- large.bin delta-a.bin delta-b.bin
        Invoke-Git -C $source -c user.name=Prototype -c user.email=prototype@example.invalid -c commit.gpgsign=false commit -qm Large
        $largeTip = Invoke-Git -C $source rev-parse HEAD
        Invoke-Git -C $source push $remote refs/heads/main:refs/heads/main
        if (@(Get-Content -LiteralPath (Join-Path $scratch 'upload-slices.log')).Count -lt 2) { throw 'The large pack did not use a sliced upload.' }
        $largePack = (Read-FixtureManifest $remoteRoot).Packs | Select-Object -Last 1
        $packDetails = Invoke-Git verify-pack -v (Join-Path $remoteRoot "objects/pack/$($largePack.Name).idx")
        if (-not ($packDetails -cmatch '^[a-f0-9]{40}\s+blob\s+\d+\s+\d+\s+\d+\s+\d+\s+[a-f0-9]{40}$')) {
            throw 'The large pack did not exercise a native blob delta.'
        }
        Invoke-Git -C $retryClone fetch origin
        Assert-Equal $largeTip (Invoke-Git -C $retryClone rev-parse origin/main) 'Large uploaded pack was fetched'
        Invoke-Git -C $retryClone fsck --full --no-dangling

        $replacement = Join-Path $scratch 'replacement'
        Invoke-Git init -q --object-format=sha1 --initial-branch=main $replacement
        Invoke-Git -C $replacement -c user.name=Prototype -c user.email=prototype@example.invalid -c commit.gpgsign=false commit --allow-empty -qm Replacement
        $replacementTip = Invoke-Git -C $replacement rev-parse HEAD
        Invoke-Git -C $replacement push --force $remote refs/heads/main:refs/heads/main
        $replacementClone = Join-Path $scratch 'replacement-clone'
        Invoke-Git clone --single-branch $remote $replacementClone
        Assert-Equal $replacementTip (Invoke-Git -C $replacementClone rev-parse HEAD) 'Force pushed without the old history available locally'
        Invoke-Git -C $replacementClone fsck --full --no-dangling

        $legacy = Join-Path $scratch 'legacy.git'
        Invoke-Git init --bare --object-format=sha1 --initial-branch=main $legacy | Out-Null
        Copy-Item -Path (Join-Path $source '.git/objects/*') -Destination (Join-Path $legacy 'objects') -Recurse -Force
        [IO.File]::WriteAllText((Join-Path $legacy 'packed-refs'), "# pack-refs with: sorted`n$initial refs/heads/main`n")
        $legacyClone = Join-Path $scratch 'legacy-clone'
        Invoke-Git clone 'onedrive-prototype::legacy.git' $legacyClone
        Assert-Equal $initial (Invoke-Git -C $legacyClone rev-parse HEAD) 'Legacy loose repository commit'
        Invoke-Git -C $legacyClone fsck --full
    }
} finally {
    $env:PATH = $oldPath
    $env:GITREMOTE_PROTOTYPE_FIXTURE_DIRECTORY = $oldFixture
    $env:GITREMOTE_PROTOTYPE_ACCESS_TOKEN = $oldToken
    try {
        if ($cloudCreated) {
            try {
                Invoke-RestMethod -Method Delete -Uri $itemUrl -Headers $headers -TimeoutSec 30 | Out-Null
            } catch {
                if (-not $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
            }
        }
    } finally {
        if (Test-Path -LiteralPath $scratch) {
            $resolved = (Resolve-Path -LiteralPath $scratch).ProviderPath
            if ((Split-Path -Parent $resolved) -eq $temp -and (Split-Path -Leaf $resolved) -eq [IO.Path]::GetFileNameWithoutExtension($name)) {
                Remove-Item -LiteralPath $resolved -Recurse -Force
            }
        }
    }
}
$mode = if ($Live) { 'OneDrive' } else { 'Graph fixture' }
Write-Output "PASS ($mode) OneDrivePrototypeProtocolAndPackStorage"
