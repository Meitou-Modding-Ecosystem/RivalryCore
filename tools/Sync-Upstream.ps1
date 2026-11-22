<#
.SYNOPSIS
Pulls upstream MeitouClient (VlcekM) through the fork into RivalryCore: upstream -> engine master -> engine fork -> RivalryCore beta -> main.

.DESCRIPTION
Run from anywhere inside the RivalryCore checkout, in PowerShell 7. Each step checks where things stand first, so running the script again
after a stop picks up where it left off. Nothing is force-pushed; master only ever fast-forwards.

  Step 1 (Master): fast-forwards the fork's master to upstream/master (a push of upstream's commit; no local checkout needed).
  Step 2 (Fork):   merges master into fork on a branch sync/upstream-<date> and opens a PR into fork. When the merge conflicts it stops,
                   leaving the merge open in engine/ for you to resolve: fix the files, `git add` them, `git commit`, then run the script
                   again with -Step Fork; it pushes the branch and opens the PR.
  Step 3 (Beta):   once the fork PR is merged, points RivalryCore's engine/ at fork's head on a branch engine/sync-<date> and opens a PR into beta.
  Step 4 (Main):   once you have tested beta, opens a PR beta -> main.

With the GitHub CLI (`gh`, signed in) the PRs are opened for you; without it the script prints the links to open them.

.PARAMETER Step
Master, Fork, Beta or Main runs that step alone; All (default) runs Master and Fork, and Beta too when fork already holds master.

.PARAMETER Abort
Abandons an unfinished merge in engine/ and puts engine/ back on the commit beta pins.

.EXAMPLE
pwsh tools/Sync-Upstream.ps1
pwsh tools/Sync-Upstream.ps1 -Step Beta
pwsh tools/Sync-Upstream.ps1 -Abort
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Master', 'Fork', 'Beta', 'Main')]
    [string]$Step = 'All',
    [switch]$Abort
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Org = 'Meitou-Modding-Ecosystem'
$EngineUrl = "https://github.com/$Org/MeitouClient.git"
$UpstreamUrl = 'https://github.com/VlcekM/MeitouClient.git'
$Date = Get-Date -Format 'yyyy-MM-dd'

# The git program itself: PowerShell names are case-insensitive, so a bare `git` inside the `Git` helper below would call the helper again.
$GitExe = (Get-Command git -CommandType Application -ErrorAction Stop)[0].Source

function Git {
    # Runs git and stops the script on failure, with git's own message.
    & $GitExe @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed (exit $LASTEXITCODE)" }
}

function GitOut {
    # Runs git and returns its trimmed output; stops on failure.
    $out = & $GitExe @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed (exit $LASTEXITCODE)" }
    return ($out -join "`n").Trim()
}

function IsAncestor([string]$a, [string]$b) {
    & $GitExe merge-base --is-ancestor $a $b
    return $LASTEXITCODE -eq 0
}

function HasGh { return [bool](Get-Command gh -ErrorAction SilentlyContinue) }

function OpenPr([string]$repo, [string]$base, [string]$head, [string]$title, [string]$body) {
    # Opens a PR with gh when it is installed (or prints the existing one); else prints the compare link.
    if (-not (HasGh)) {
        Write-Host "Open the PR: https://github.com/${Org}/${repo}/compare/${base}...${head}?expand=1" -ForegroundColor Yellow
        return
    }
    $existing = & gh pr list --repo "$Org/$repo" --base $base --head $head --state open --json url --jq '.[0].url'
    if ($existing) {
        Write-Host "PR already open: $existing" -ForegroundColor Yellow
        return
    }
    & gh pr create --repo "$Org/$repo" --base $base --head $head --title $title --body $body
}

# ---------------------------------------------------------------- setup

$root = GitOut rev-parse --show-superproject-working-tree
if (-not $root) { $root = GitOut rev-parse --show-toplevel }
Set-Location $root
if (-not (Test-Path (Join-Path $root 'engine'))) { throw "Run this inside the RivalryCore checkout (no engine/ under $root)." }
$engine = Join-Path $root 'engine'

function InEngine([scriptblock]$body) {
    Push-Location $engine
    try { & $body } finally { Pop-Location }
}

function EnsureEngineRemotes {
    InEngine {
        $remotes = @(& $GitExe remote)
        if ($remotes -notcontains 'origin') { Git remote add origin $EngineUrl } else { Git remote set-url origin $EngineUrl }
        if ($remotes -notcontains 'upstream') { Git remote add upstream $UpstreamUrl } else { Git remote set-url upstream $UpstreamUrl }
        Git fetch --quiet upstream master
        Git fetch --quiet origin master fork
    }
}

function EngineMerging { return InEngine { Test-Path (Join-Path (GitOut rev-parse --git-dir) 'MERGE_HEAD') } }

function EngineDirty { return [bool](InEngine { GitOut status --porcelain --untracked-files=no }) }

function ResetEngineToPin {
    # engine/ back on the commit the superproject's current branch pins.
    Git submodule update --init engine
}

# ---------------------------------------------------------------- abort

if ($Abort) {
    if (EngineMerging) { InEngine { Git merge --abort } }
    ResetEngineToPin
    Write-Host 'engine/ is back on the pinned commit; no merge is open.' -ForegroundColor Green
    return
}

# ---------------------------------------------------------------- steps

function StepMaster {
    InEngine {
        if (IsAncestor upstream/master origin/master) {
            Write-Host 'master already holds upstream.' -ForegroundColor Green
            return
        }
        if (-not (IsAncestor origin/master upstream/master)) {
            throw 'origin/master has commits upstream does not: master must stay an exact mirror. Fix it by hand before syncing.'
        }
        Git push origin upstream/master:refs/heads/master
        Git fetch --quiet origin master
        Write-Host "master fast-forwarded to upstream $(GitOut rev-parse --short upstream/master)." -ForegroundColor Green
    }
}

function StepFork {
    $branch = "sync/upstream-$Date"

    # A merge left open by an earlier run: finish it when it is resolved.
    if (EngineMerging) {
        $conflicts = InEngine { GitOut diff --name-only --diff-filter=U }
        if ($conflicts) {
            Write-Host "The merge in engine/ still has conflicts:`n$conflicts" -ForegroundColor Red
            Write-Host 'Fix them, `git add` each file, `git commit`, then run this again with -Step Fork (or -Abort to give up).'
            exit 1
        }
        throw 'The merge in engine/ is resolved but not committed: run `git commit` in engine/, then this again with -Step Fork.'
    }

    InEngine {
        if (IsAncestor origin/master origin/fork) {
            Write-Host 'fork already holds master.' -ForegroundColor Green
            return
        }

        $current = GitOut rev-parse --abbrev-ref HEAD
        $resuming = $current -like 'sync/upstream-*' -and (IsAncestor origin/master HEAD) -and (IsAncestor origin/fork HEAD)
        if ($resuming) {
            $branch = $current
        }
        else {
            if (EngineDirty) { throw 'engine/ has uncommitted changes: commit or discard them first.' }

            Git checkout --quiet -B $branch origin/fork
            & $GitExe merge --no-ff --no-edit origin/master -m "Merge master (upstream $(GitOut rev-parse --short origin/master)) into fork"
            if ($LASTEXITCODE -ne 0) {
                $conflicts = GitOut diff --name-only --diff-filter=U
                Write-Host "Merging master into fork conflicts in:`n$conflicts" -ForegroundColor Red
                Write-Host 'Keep upstream''s side for code both have; keep fork-only additions (CI, Azure upload, fork-only mod API) beside it.'
                Write-Host 'Then `git add` each file and `git commit` in engine/, and run this again with -Step Fork.'
                exit 1
            }
        }

        Git push --quiet -u origin $branch
        OpenPr 'MeitouClient' 'fork' $branch "Sync: upstream $(GitOut rev-parse --short origin/master) into fork" `
            'Merges master (the upstream mirror) into fork. Merge it once CI passes, then run Sync-Upstream.ps1 -Step Beta.'
        Write-Host "Pushed $branch. Merge its PR into fork once CI passes, then run: pwsh tools/Sync-Upstream.ps1 -Step Beta" -ForegroundColor Green
    }
}

function StepBeta {
    InEngine { Git fetch --quiet origin fork }
    $forkHead = InEngine { GitOut rev-parse origin/fork }
    Git fetch --quiet origin beta

    # fork first: a pin of fork's head means nothing while fork lacks upstream's newest commits.
    if (InEngine { -not (IsAncestor origin/master origin/fork) }) {
        Write-Host 'fork is behind master (upstream has new commits): run -Step Fork and merge its PR first.' -ForegroundColor Yellow
        return
    }

    $pinned = (GitOut ls-tree origin/beta engine).Split()[2]
    if ($pinned -eq $forkHead) {
        Write-Host 'beta already pins fork''s head, which holds upstream.' -ForegroundColor Green
        return
    }
    if ([bool](GitOut status --porcelain --untracked-files=no -- . ':!engine')) {
        throw 'RivalryCore has uncommitted changes: commit or discard them first.'
    }
    if (EngineMerging) { throw 'engine/ has a merge open: finish it (-Step Fork) or run -Abort first.' }

    $short = $forkHead.Substring(0, 9)
    $branch = "engine/sync-$Date"
    Git checkout --quiet -B $branch origin/beta
    InEngine { Git checkout --quiet --detach $forkHead }
    Git add engine
    Git commit --quiet -m "Engine: pin fork $short (upstream sync)"
    Git push --quiet -u origin $branch
    OpenPr 'RivalryCore' 'beta' $branch "Engine: pin fork $short (upstream sync)" `
        "Points engine/ at fork $short, which holds the latest upstream. Merge once CI passes."
    Write-Host "Pushed $branch. Merge its PR into beta once CI passes, then come back to beta:" -ForegroundColor Green
    Write-Host '  git checkout beta; git pull; git submodule update --init engine'
    Write-Host 'Test on beta, then run: pwsh tools/Sync-Upstream.ps1 -Step Main'
}

function StepMain {
    Git fetch --quiet origin beta main
    if (IsAncestor origin/beta origin/main) {
        Write-Host 'main already holds beta.' -ForegroundColor Green
        return
    }
    OpenPr 'RivalryCore' 'main' 'beta' 'Promote beta to main' 'Promotes the tested beta (including the latest engine pin) to main.'
}

EnsureEngineRemotes
switch ($Step) {
    'Master' { StepMaster }
    'Fork' { StepFork }
    'Beta' { StepBeta }
    'Main' { StepMain }
    'All' {
        StepMaster
        StepFork
        if (InEngine { IsAncestor origin/master origin/fork }) { StepBeta }
    }
}
