# optimized-unity installer for Windows.
#
#   irm https://raw.githubusercontent.com/pnarimani/OptimizedUnityCLI/main/scripts/install.ps1 | iex
#
# Options (set before running the one-liner):
#   $env:OPTIMIZED_UNITY_VERSION = 'v1.0.0'   install a specific release (default: latest)
#   $env:OPTIMIZED_UNITY_DIR = 'C:\tools\ou'   install location (default: %LOCALAPPDATA%\OptimizedUnity)
#   $env:OPTIMIZED_UNITY_SKIP_UNITY_CLI = '1'  don't install the Unity CLI
#   $env:OPTIMIZED_UNITY_NO_MODIFY_PATH = '1'  don't change the user PATH
#   $env:OPTIMIZED_UNITY_UNINSTALL = '1'       remove optimized-unity

function Install-OptimizedUnity {
	$ErrorActionPreference = 'Stop'
	$ProgressPreference = 'SilentlyContinue'
	# Windows PowerShell 5.1 defaults to TLS 1.0.
	[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

	$repo = if ($env:OPTIMIZED_UNITY_REPO) { $env:OPTIMIZED_UNITY_REPO } else { 'pnarimani/OptimizedUnityCLI' }
	$version = if ($env:OPTIMIZED_UNITY_VERSION) { $env:OPTIMIZED_UNITY_VERSION } else { 'latest' }
	$installDir = if ($env:OPTIMIZED_UNITY_DIR) { $env:OPTIMIZED_UNITY_DIR } else { Join-Path $env:LOCALAPPDATA 'OptimizedUnity' }
	$binDir = Join-Path $installDir 'bin'
	$target = Join-Path $binDir 'unity.exe'

	function Say([string]$message) { Write-Host "==> $message" }
	function Fail([string]$message) { throw "optimized-unity install: $message" }

	function Get-UserPath { [Environment]::GetEnvironmentVariable('Path', 'User') }
	function Split-PathList([string]$value) { if ($value) { $value.Split(';') | Where-Object { $_ } } else { @() } }
	function Same-Dir([string]$a, [string]$b) { $a.TrimEnd('\') -ieq $b.TrimEnd('\') }

	if ($env:OPTIMIZED_UNITY_UNINSTALL -eq '1') {
		Say "Removing $installDir"
		if (Test-Path $installDir) { Remove-Item -Recurse -Force $installDir }
		$kept = Split-PathList (Get-UserPath) | Where-Object { -not (Same-Dir $_ $binDir) }
		[Environment]::SetEnvironmentVariable('Path', ($kept -join ';'), 'User')
		Say 'optimized-unity removed. The Unity CLI itself was left installed.'
		return
	}

	$arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
	$rid = switch ($arch) {
		'AMD64' { 'win-x64' }
		'ARM64' { 'win-arm64' }
		default { Fail "unsupported CPU architecture: $arch" }
	}

	# The real Unity CLI that this wrapper drives.
	$realCli = Join-Path $env:USERPROFILE '.unity\bin\unity.exe'
	$haveCli = (Test-Path $realCli) -or ($env:UNITY_CLI_PATH -and (Test-Path $env:UNITY_CLI_PATH))
	if (-not $haveCli) {
		$haveCli = @(Get-Command unity -CommandType Application -All -ErrorAction SilentlyContinue |
			Where-Object { -not (Same-Dir (Split-Path $_.Source) $binDir) }).Count -gt 0
	}
	if (-not $haveCli) {
		if ($env:OPTIMIZED_UNITY_SKIP_UNITY_CLI -eq '1') {
			Write-Warning 'Unity CLI not found; skipped installing it.'
		} else {
			Say 'Installing the Unity CLI (https://unity.com/install.ps1)'
			# Its own process, so an `exit` inside that installer cannot end this one.
			$shell = (Get-Process -Id $PID).Path
			& $shell -NoProfile -ExecutionPolicy Bypass -Command 'irm https://unity.com/install.ps1 | iex'
			if ($LASTEXITCODE -ne 0) { Fail 'the Unity CLI installer failed' }
		}
	}

	$base = if ($version -eq 'latest') { "https://github.com/$repo/releases/latest/download" } else { "https://github.com/$repo/releases/download/$version" }
	$asset = "optimized-unity-$rid.exe"
	$temp = Join-Path ([IO.Path]::GetTempPath()) ("optimized-unity-" + [Guid]::NewGuid().ToString('N'))
	New-Item -ItemType Directory -Force -Path $temp | Out-Null
	try {
		Say "Downloading $asset ($version)"
		$download = Join-Path $temp $asset
		Invoke-WebRequest -UseBasicParsing -Uri "$base/$asset" -OutFile $download
		Invoke-WebRequest -UseBasicParsing -Uri "$base/SHA256SUMS" -OutFile (Join-Path $temp 'SHA256SUMS')
		$line = Get-Content (Join-Path $temp 'SHA256SUMS') | Where-Object {
			$parts = $_.Trim() -split '\s+'
			$parts.Count -ge 2 -and $parts[1].TrimStart('*') -eq $asset
		} | Select-Object -First 1
		if (-not $line) { Fail "$asset is missing from SHA256SUMS" }
		$expected = ($line -split '\s+')[0].ToLowerInvariant()
		$actual = (Get-FileHash -Algorithm SHA256 $download).Hash.ToLowerInvariant()
		if ($expected -ne $actual) { Fail "checksum mismatch for $asset (expected $expected, got $actual)" }

		New-Item -ItemType Directory -Force -Path $binDir | Out-Null
		# A running unity.exe cannot be overwritten, but it can be renamed out of the way.
		if (Test-Path $target) {
			$old = "$target.old"
			if (Test-Path $old) { Remove-Item -Force $old -ErrorAction SilentlyContinue }
			Move-Item -Force $target $old
		}
		Copy-Item $download $target
		Unblock-File $target -ErrorAction SilentlyContinue
	} finally {
		Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
	}
	Say "Installed $target"

	if ($env:OPTIMIZED_UNITY_NO_MODIFY_PATH -eq '1') {
		Say "PATH not modified. Put $binDir before $(Split-Path $realCli) on your PATH."
	} else {
		# First in the user PATH, so it wins over the Unity CLI's own entry.
		$entries = @($binDir) + @(Split-PathList (Get-UserPath) | Where-Object { -not (Same-Dir $_ $binDir) })
		[Environment]::SetEnvironmentVariable('Path', ($entries -join ';'), 'User')
		$env:Path = (@($binDir) + @(Split-PathList $env:Path | Where-Object { -not (Same-Dir $_ $binDir) })) -join ';'
		Say "Added $binDir to the front of your user PATH."

		# The machine PATH is searched before the user PATH.
		$machineHit = Split-PathList ([Environment]::GetEnvironmentVariable('Path', 'Machine')) |
			Where-Object { (Test-Path (Join-Path $_ 'unity.exe')) -or (Test-Path (Join-Path $_ 'unity.cmd')) } | Select-Object -First 1
		if ($machineHit) { Write-Warning "A unity command in the system PATH ($machineHit) takes precedence. Move $binDir ahead of it in the system PATH." }
	}

	$resolved = (Get-Command unity -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
	$cliVersion = $null
	try { $cliVersion = & $target --version 2>$null } catch { $LASTEXITCODE = 1 }
	if ($LASTEXITCODE -eq 0 -and $cliVersion) { Say "Wrapping Unity CLI $cliVersion" } else { Write-Warning 'The Unity CLI did not answer; install it with: irm https://unity.com/install.ps1 | iex' }
	if ($resolved -and -not (Same-Dir (Split-Path $resolved) $binDir)) { Write-Warning "unity currently resolves to $resolved in this session." }
	Write-Host ''
	Write-Host 'Open a new terminal, then check with: Get-Command unity'
	Write-Host 'Bypass the wrapper for one call with: $env:UNITY_WRAP = "off"'
}

Install-OptimizedUnity
