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

	# Terminal multiplexers (Zellij, psmux) start every pane with the environment their server started
	# with, so a user PATH change never reaches them, nor anything launched from them (Claude Code,
	# its Git Bash and PowerShell tools). Shell startup files run in every new pane, so they also get
	# a block that puts $binDir first again.
	$markBegin = '# >>> optimized-unity >>>'
	$markEnd = '# <<< optimized-unity <<<'
	$userHome = if ($env:HOME -and (Test-Path -LiteralPath $env:HOME)) { $env:HOME } else { $env:USERPROFILE }
	$documents = [Environment]::GetFolderPath('MyDocuments')
	$pwshProfile = Join-Path $documents 'PowerShell\profile.ps1'
	$winPsProfile = Join-Path $documents 'WindowsPowerShell\profile.ps1'
	$bashFiles = @((Join-Path $userHome '.bashrc'), (Join-Path $userHome '.bash_profile'))

	# Replace the marked block in a startup file, or remove it when $lines is empty. Keeps the file's
	# line endings; .ps1 files keep (or get) a BOM so Windows PowerShell reads them as UTF-8.
	function Set-StartupBlock([string]$file, [string[]]$lines) {
		$exists = Test-Path -LiteralPath $file
		if (-not $exists -and -not $lines) { return }
		$isPs = $file -like '*.ps1'
		$text = ''
		$hadBom = $false
		if ($exists) {
			$bytes = [IO.File]::ReadAllBytes($file)
			$hadBom = $bytes.Length -ge 2 -and ($bytes[0] -in 0xEF, 0xFF, 0xFE)
			try { $text = [IO.File]::ReadAllText($file, (New-Object Text.UTF8Encoding $false, $true)) }
			catch { Write-Warning "Skipped $file`: it is not UTF-8 text."; return }
		}
		$newline = if ($text.Contains("`r`n") -or (-not $exists -and $isPs)) { "`r`n" } else { "`n" }
		$pattern = '(?s)' + [regex]::Escape($markBegin) + '.*?' + [regex]::Escape($markEnd) + '\r?\n?'
		$updated = [regex]::Replace($text, $pattern, '')
		if ($lines) {
			if ($updated -and -not $updated.EndsWith("`n")) { $updated += $newline }
			$updated += ((@($markBegin) + $lines + @($markEnd)) -join $newline) + $newline
		}
		if ($exists -and $updated -ceq $text) { return }
		New-Item -ItemType Directory -Force -Path (Split-Path $file) | Out-Null
		[IO.File]::WriteAllText($file, $updated, (New-Object Text.UTF8Encoding ($isPs -and (-not $exists -or $hadBom))))
		$file
	}

	function Remove-StartupBlocks {
		foreach ($file in @($pwshProfile, $winPsProfile) + $bashFiles) { Set-StartupBlock $file @() | Out-Null }
	}

	if ($env:OPTIMIZED_UNITY_UNINSTALL -eq '1') {
		Say "Removing $installDir"
		if (Test-Path $installDir) { Remove-Item -Recurse -Force $installDir }
		$kept = Split-PathList (Get-UserPath) | Where-Object { -not (Same-Dir $_ $binDir) }
		[Environment]::SetEnvironmentVariable('Path', ($kept -join ';'), 'User')
		Remove-StartupBlocks
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
		Say "PATH not modified. Put $binDir before the Unity CLI's directory on your PATH."
	} else {
		# First in the user PATH, so it wins over the Unity CLI's own entry.
		$entries = @($binDir) + @(Split-PathList (Get-UserPath) | Where-Object { -not (Same-Dir $_ $binDir) })
		[Environment]::SetEnvironmentVariable('Path', ($entries -join ';'), 'User')
		$env:Path = (@($binDir) + @(Split-PathList $env:Path | Where-Object { -not (Same-Dir $_ $binDir) })) -join ';'
		Say "Added $binDir to the front of your user PATH."

		$psDir = $binDir.TrimEnd('\') -replace "'", "''"
		$psLines = @("`$env:Path = '$psDir;' + ((`$env:Path -split ';' | Where-Object { `$_ -and `$_.TrimEnd('\') -ine '$psDir' }) -join ';')")
		$bashDir = if ($binDir -match '^([A-Za-z]):\\(.*)$') { '/' + $Matches[1].ToLowerInvariant() + '/' + $Matches[2] } else { $binDir }
		$bashDir = $bashDir.TrimEnd('\').Replace('\', '/') -replace "'", "'\''"
		$bashLines = @("export PATH='$bashDir':`"`$PATH`"")

		# Windows PowerShell's default Restricted policy refuses to run a profile, so it only gets a new
		# one when scripts are allowed. PowerShell 7 defaults to RemoteSigned.
		$scriptsAllowed = try { (Get-ExecutionPolicy) -notin 'Restricted', 'AllSigned' } catch { $false }
		$isCore = $PSVersionTable.PSEdition -eq 'Core'
		$hasPwsh = $isCore -or [bool](Get-Command pwsh -CommandType Application -ErrorAction SilentlyContinue)
		$hasBash = [bool](Get-Command git, bash -CommandType Application -ErrorAction SilentlyContinue)
		$targets = [ordered]@{
			$pwshProfile = (Test-Path -LiteralPath $pwshProfile) -or ($hasPwsh -and ($scriptsAllowed -or -not $isCore))
			$winPsProfile = (Test-Path -LiteralPath $winPsProfile) -or (-not $isCore -and $scriptsAllowed)
			# Git Bash (and Claude Code's Bash tool) reads ~/.bashrc; ~/.bash_profile only matters when it does not source it.
			$bashFiles[0] = (Test-Path -LiteralPath $bashFiles[0]) -or $hasBash
			$bashFiles[1] = (Test-Path -LiteralPath $bashFiles[1]) -and -not (Select-String -LiteralPath $bashFiles[1] -Pattern 'bashrc' -Quiet)
		}
		$configured = foreach ($file in $targets.Keys) {
			$lines = if (-not $targets[$file]) { @() } elseif ($file -like '*.ps1') { $psLines } else { $bashLines }
			Set-StartupBlock $file $lines | Out-Null
			if ($lines) { $file }
		}
		if ($configured) { Say "Startup files that put it first again: $(@($configured) -join ', ')" }

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
	Write-Host 'Zellij and psmux keep the PATH their server started with: new PowerShell and Git Bash panes'
	Write-Host 'fix it from their startup files; for other shells, restart the multiplexer server.'
	Write-Host 'Bypass the wrapper for one call with: $env:UNITY_WRAP = "off"'
}

Install-OptimizedUnity
