#Requires -Version 7.2
<#
.SYNOPSIS
Builds the MCP server from source when needed, then runs it over stdio.

.DESCRIPTION
The Copilot CLI plugin starts the server through this script. stdout belongs to
the MCP protocol, so every build message goes to stderr. The executable always
has the same path so Windows keeps one notification identity across plugin
updates. If an update needs a rebuild while an older server is running, the
launcher reports that the old session must be closed.

.PARAMETER DataDirectory
Where build output goes. Defaults to the plugin's PLUGIN_DATA directory, or
%LOCALAPPDATA%\WindowsNotifyMcp when the script runs outside a plugin.
#>
param(
    [string] $DataDirectory = $(if ($env:PLUGIN_DATA) { $env:PLUGIN_DATA } else { Join-Path $env:LOCALAPPDATA 'WindowsNotifyMcp' })
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\WindowsNotifyMcp\WindowsNotifyMcp.csproj'
$outputDirectory = Join-Path $DataDirectory 'build'
$fingerprintFile = Join-Path $outputDirectory '.source-fingerprint'

function Write-Log([string] $Message) {
    [Console]::Error.WriteLine("windows-notify-mcp: $Message")
}

# Include project files and inherited MSBuild configuration so unrelated
# repository changes do not invalidate the cached executable.
function Get-SourceFingerprint {
    $projectDirectory = Split-Path -Parent $project
    $inputs = @(Get-ChildItem -LiteralPath $projectDirectory -Recurse -File |
        Where-Object {
            $relative = [IO.Path]::GetRelativePath($projectDirectory, $_.FullName)
            $relative -notmatch '(^|[\\/])(bin|obj)([\\/]|$)'
        })
    $configurationFiles = @(
        'global.json', 'Directory.Build.props', 'Directory.Build.targets',
        'Directory.Build.rsp', 'Directory.Packages.props', 'NuGet.Config', '.editorconfig'
    )
    $ancestor = Split-Path -Parent $project
    while ($ancestor) {
        foreach ($name in $configurationFiles) {
            $path = Join-Path $ancestor $name
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                $inputs += Get-Item -LiteralPath $path
            }
        }
        $parent = Split-Path -Parent $ancestor
        if (-not $parent -or $parent -eq $ancestor) { break }
        $ancestor = $parent
    }
    $lines = @('runtime=win-x64;configuration=Release;PublishAot=false') + @($inputs |
        Sort-Object -Property FullName -Unique | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($repoRoot, $_.FullName)
        "$relative $((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    })
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).Substring(0, 16).ToLowerInvariant()
}

function Test-ServerRunningFromDataDirectory([DateTime] $StartedBefore = [DateTime]::MaxValue) {
    $dataRoot = [IO.Path]::GetFullPath($DataDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    foreach ($process in [Diagnostics.Process]::GetProcessesByName('WindowsNotifyMcp')) {
        try {
            if ($process.HasExited) { continue }
            try {
                $processPath = [IO.Path]::GetFullPath($process.MainModule.FileName)
            }
            catch [ComponentModel.Win32Exception] {
                continue
            }
            if ($processPath.StartsWith($dataRoot, [StringComparison]::OrdinalIgnoreCase) -and
                $process.StartTime -lt $StartedBefore) {
                return $true
            }
        }
        catch [InvalidOperationException] {
            # The process exited while its executable path was being inspected.
        }
        finally {
            $process.Dispose()
        }
    }
    return $false
}

function Invoke-Build([string] $Fingerprint) {
    Write-Log "building the server from $repoRoot. The first build can take a minute."
    $staging = "$outputDirectory.staging-$PID"
    $previous = "$outputDirectory.previous-$PID"
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }

    # Force MSBuild to compile changed sources even when their timestamps predate
    # prior outputs. PublishAot=false avoids requiring the C++ build tools.
    $intermediateDirectory = Join-Path $DataDirectory 'obj'
    if (Test-Path -LiteralPath $intermediateDirectory) {
        Remove-Item -LiteralPath $intermediateDirectory -Recurse -Force
    }
    $arguments = @(
        'publish', $project,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--artifacts-path', $intermediateDirectory,
        '--output', $staging,
        '--disable-build-servers',
        '-p:PublishAot=false',
        '-nologo'
    )
    Push-Location -LiteralPath $repoRoot
    try {
        & dotnet @arguments 2>&1 | ForEach-Object { [Console]::Error.WriteLine($_) }
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    if ($exitCode -ne 0) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        throw "dotnet publish failed with exit code $exitCode. The plugin needs the .NET SDK version from global.json."
    }

    Set-Content -LiteralPath (Join-Path $staging '.source-fingerprint') -Value $Fingerprint -NoNewline -Encoding ascii
    if (Test-Path -LiteralPath $outputDirectory) {
        if (Test-ServerRunningFromDataDirectory) {
            Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
            throw "Cannot update the server while an older instance is running from its plugin data directory. Close Copilot sessions using windows-notify, then start a new session."
        }
        try {
            Move-Item -LiteralPath $outputDirectory -Destination $previous -ErrorAction Stop | Out-Null
        }
        catch {
            Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
            throw "Could not move the existing build aside before updating it: $($_.Exception.Message)"
        }
    }

    $replacementStartedAt = Get-Date
    try {
        Move-Item -LiteralPath $staging -Destination $outputDirectory -ErrorAction Stop | Out-Null
    }
    catch {
        if (Test-Path -LiteralPath $previous) {
            Move-Item -LiteralPath $previous -Destination $outputDirectory -ErrorAction SilentlyContinue | Out-Null
        }
        throw
    }

    if (Test-ServerRunningFromDataDirectory $replacementStartedAt) {
        $rejected = "$outputDirectory.rejected-$PID"
        try {
            Move-Item -LiteralPath $outputDirectory -Destination $rejected -ErrorAction Stop | Out-Null
            Move-Item -LiteralPath $previous -Destination $outputDirectory -ErrorAction Stop | Out-Null
        }
        catch {
            if (-not (Test-Path -LiteralPath $outputDirectory) -and (Test-Path -LiteralPath $previous)) {
                Move-Item -LiteralPath $previous -Destination $outputDirectory -ErrorAction SilentlyContinue | Out-Null
            }
            throw "A server process started during the update. The previous build was restored where possible; close windows-notify sessions and retry."
        }
        Remove-Item -LiteralPath $rejected -Recurse -Force
        throw "A server process started during the update. The previous build was restored; close windows-notify sessions and retry."
    }

    if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
}

$mutex = $null
$server = $null
try {
    New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
    $fingerprint = Get-SourceFingerprint
    $executable = Join-Path $outputDirectory 'WindowsNotifyMcp.exe'

    # Program.cs takes the same mutex before dispatching activation or MCP startup.
    # Hold it through Process.Start so another launcher cannot swap files first.
    $mutex = [Threading.Mutex]::new($false, 'Local\WindowsNotifyMcp-PluginBuild')
    try {
        try { [void] $mutex.WaitOne() } catch [Threading.AbandonedMutexException] { }
        $cachedFingerprint = if (Test-Path -LiteralPath $fingerprintFile) {
            [IO.File]::ReadAllText($fingerprintFile).Trim()
        }
        if (-not (Test-Path -LiteralPath $executable) -or $cachedFingerprint -ne $fingerprint) {
            if (Test-ServerRunningFromDataDirectory) {
                throw "Cannot update the server while an older instance is running from its plugin data directory. Close Copilot sessions using windows-notify, then start a new session."
            }
            Invoke-Build $fingerprint
        }

        $startInfo = [Diagnostics.ProcessStartInfo]::new($executable)
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardInput = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $server = [Diagnostics.Process]::Start($startInfo)
        $inputTask = [Console]::OpenStandardInput().CopyToAsync($server.StandardInput.BaseStream)
        $outputTask = $server.StandardOutput.BaseStream.CopyToAsync([Console]::OpenStandardOutput())
        $errorTask = $server.StandardError.BaseStream.CopyToAsync([Console]::OpenStandardError())
    }
    finally {
        $mutex.ReleaseMutex()
        $mutex.Dispose()
    }
}
catch {
    Write-Log $_.Exception.Message
    exit 1
}

$serverInput = $server.StandardInput
while (-not $inputTask.IsCompleted -and -not $server.HasExited) {
    Start-Sleep -Milliseconds 50
}
if ($inputTask.IsCompleted) {
    try { $null = $inputTask.GetAwaiter().GetResult() }
    finally { $serverInput.Close() }
}
$server.WaitForExit()
$null = $outputTask.GetAwaiter().GetResult()
$null = $errorTask.GetAwaiter().GetResult()
exit $server.ExitCode
