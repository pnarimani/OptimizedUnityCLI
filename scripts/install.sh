#!/usr/bin/env bash
# optimized-unity installer for macOS, Linux and WSL.
#
#   curl -fsSL https://raw.githubusercontent.com/pnarimani/OptimizedUnityCLI/main/scripts/install.sh | bash
#
# Options (append after `bash -s --`, or set the matching environment variable):
#   --version <tag>     install a specific release           OPTIMIZED_UNITY_VERSION
#   --from-source       build with .NET instead of download  OPTIMIZED_UNITY_FROM_SOURCE=1
#   --skip-unity-cli    don't install the Unity CLI          OPTIMIZED_UNITY_SKIP_UNITY_CLI=1
#   --no-modify-path    don't touch shell startup files      OPTIMIZED_UNITY_NO_MODIFY_PATH=1
#   --uninstall         remove optimized-unity               OPTIMIZED_UNITY_UNINSTALL=1
# Install location: OPTIMIZED_UNITY_DIR (default ~/.optimized-unity).
set -euo pipefail

REPO="${OPTIMIZED_UNITY_REPO:-pnarimani/OptimizedUnityCLI}"
VERSION="${OPTIMIZED_UNITY_VERSION:-latest}"
INSTALL_DIR="${OPTIMIZED_UNITY_DIR:-$HOME/.optimized-unity}"
BIN_DIR="$INSTALL_DIR/bin"
FROM_SOURCE="${OPTIMIZED_UNITY_FROM_SOURCE:-0}"
SKIP_UNITY_CLI="${OPTIMIZED_UNITY_SKIP_UNITY_CLI:-0}"
NO_MODIFY_PATH="${OPTIMIZED_UNITY_NO_MODIFY_PATH:-0}"
UNINSTALL="${OPTIMIZED_UNITY_UNINSTALL:-0}"
MARK_BEGIN="# >>> optimized-unity >>>"
MARK_END="# <<< optimized-unity <<<"

say() { printf '\033[1m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[33mwarning:\033[0m %s\n' "$*" >&2; }
die() { printf '\033[31merror:\033[0m %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
	case "$1" in
		--version) VERSION="${2:?--version needs a tag}"; shift ;;
		--version=*) VERSION="${1#*=}" ;;
		--from-source) FROM_SOURCE=1 ;;
		--skip-unity-cli) SKIP_UNITY_CLI=1 ;;
		--no-modify-path) NO_MODIFY_PATH=1 ;;
		--uninstall) UNINSTALL=1 ;;
		-h|--help) sed -n '2,15p' "${BASH_SOURCE[0]:-/dev/null}" 2>/dev/null || true; exit 0 ;;
		*) die "unknown option: $1" ;;
	esac
	shift
done

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

is_wsl() { [ "$(uname -s)" = Linux ] && grep -qiE 'microsoft|wsl' /proc/sys/kernel/osrelease 2>/dev/null; }

detect_rid() {
	local os arch
	case "$(uname -s)" in
		Darwin) os=osx ;;
		Linux) os=linux ;;
		*) die "unsupported OS $(uname -s); on Windows use: irm https://raw.githubusercontent.com/$REPO/main/scripts/install.ps1 | iex" ;;
	esac
	case "$(uname -m)" in
		arm64|aarch64) arch=arm64 ;;
		x86_64|amd64) arch=x64 ;;
		*) die "unsupported CPU $(uname -m)" ;;
	esac
	# An x64 shell under Rosetta still runs on Apple silicon.
	if [ "$os" = osx ] && [ "$arch" = x64 ] && [ "$(sysctl -n sysctl.proc_translated 2>/dev/null || echo 0)" = 1 ]; then arch=arm64; fi
	echo "$os-$arch"
}

download() {
	if command -v curl >/dev/null 2>&1; then curl -fsSL --retry 3 -o "$2" "$1"
	elif command -v wget >/dev/null 2>&1; then wget -q -O "$2" "$1"
	else die "curl or wget is required"; fi
}

sha256() {
	if command -v shasum >/dev/null 2>&1; then shasum -a 256 "$1" | cut -d' ' -f1
	else sha256sum "$1" | cut -d' ' -f1; fi
}

# Startup files that get the PATH block. zsh uses ~/.zshenv, like the Unity CLI installer: it is
# the only file non-interactive `zsh -c` shells (agents, scripts) read. bash files are only touched
# when they exist, or when bash is the login shell.
rc_files() {
	local files=()
	if [ -f "$HOME/.zshenv" ] || [ "$(basename "${SHELL:-}")" = zsh ]; then files+=("$HOME/.zshenv"); fi
	local bash_file found=0
	for bash_file in "$HOME/.bashrc" "$HOME/.bash_profile" "$HOME/.profile"; do
		if [ -f "$bash_file" ]; then files+=("$bash_file"); found=1; fi
	done
	if [ "$found" = 0 ] && [ "$(basename "${SHELL:-}")" = bash ]; then
		if [ "$(uname -s)" = Darwin ]; then files+=("$HOME/.bash_profile"); else files+=("$HOME/.bashrc"); fi
	fi
	# bash 3.2 (macOS) treats an empty array as unbound under `set -u`.
	if [ "${#files[@]}" -gt 0 ]; then printf '%s\n' "${files[@]}"; fi
}

# Every file a previous install may have written to.
all_rc_files() {
	printf '%s\n' "$HOME/.zshenv" "$HOME/.zshrc" "$HOME/.zprofile" "$HOME/.bashrc" "$HOME/.bash_profile" "$HOME/.profile"
}

strip_block() {
	[ -f "$1" ] || return 0
	grep -qF "$MARK_BEGIN" "$1" || return 0
	awk -v b="$MARK_BEGIN" -v e="$MARK_END" '$0==b{skip=1;next} $0==e{skip=0;next} !skip' "$1" > "$TMP/rc" && cat "$TMP/rc" > "$1"
}

configure_path() {
	local file fish="$HOME/.config/fish/config.fish"
	while IFS= read -r file; do strip_block "$file"; done < <(all_rc_files)
	while IFS= read -r file; do
		# Appended last, so it wins over the Unity CLI's own `~/.unity/bin` PATH entry.
		printf '%s\nexport PATH="%s:$PATH"\n%s\n' "$MARK_BEGIN" "$BIN_DIR" "$MARK_END" >> "$file"
	done < <(rc_files)
	if [ -f "$fish" ]; then
		strip_block "$fish"
		printf '%s\nfish_add_path --move --prepend "%s"\n%s\n' "$MARK_BEGIN" "$BIN_DIR" "$MARK_END" >> "$fish"
	fi
}

uninstall() {
	say "Removing $INSTALL_DIR"
	rm -rf "$INSTALL_DIR"
	local file
	while IFS= read -r file; do strip_block "$file"; done < <(all_rc_files)
	strip_block "$HOME/.config/fish/config.fish"
	say "optimized-unity removed. The Unity CLI itself was left installed."
}

ensure_unity_cli() {
	if is_wsl; then
		command -v unity.exe >/dev/null 2>&1 && return 0
		warn "unity.exe is not reachable from WSL. Install the Unity CLI on Windows (PowerShell: irm https://unity.com/install.ps1 | iex)."
		return 0
	fi
	[ -x "$HOME/.unity/bin/unity" ] && return 0
	if [ -n "${UNITY_CLI_PATH:-}" ] && [ -x "$UNITY_CLI_PATH" ]; then return 0; fi
	if [ "$SKIP_UNITY_CLI" = 1 ]; then
		warn "Unity CLI not found; skipped installing it (--skip-unity-cli)."
		return 0
	fi
	say "Installing the Unity CLI (https://unity.com/install.sh)"
	download https://unity.com/install.sh "$TMP/unity-install.sh"
	bash "$TMP/unity-install.sh" </dev/null || die "the Unity CLI installer failed"
}

install_release() {
	local rid="$1" base asset expected actual
	if [ "$VERSION" = latest ]; then base="https://github.com/$REPO/releases/latest/download"
	else base="https://github.com/$REPO/releases/download/$VERSION"; fi
	asset="optimized-unity-$rid"
	say "Downloading $asset ($VERSION)"
	download "$base/$asset" "$TMP/unity" || die "download failed: $base/$asset"
	download "$base/SHA256SUMS" "$TMP/SHA256SUMS" || die "download failed: $base/SHA256SUMS"
	expected="$(awk -v a="$asset" '$2==a || $2=="*"a {print $1}' "$TMP/SHA256SUMS")"
	actual="$(sha256 "$TMP/unity")"
	[ -n "$expected" ] || die "$asset is missing from SHA256SUMS"
	[ "$expected" = "$actual" ] || die "checksum mismatch for $asset (expected $expected, got $actual)"
	place "$TMP/unity"
}

find_dotnet() {
	local candidate
	for candidate in "$INSTALL_DIR/dotnet/dotnet" "$(command -v dotnet 2>/dev/null || true)" "$HOME/.dotnet/dotnet" /usr/local/share/dotnet/dotnet; do
		[ -n "$candidate" ] && [ -x "$candidate" ] || continue
		"$candidate" --list-sdks 2>/dev/null | grep -qE '^(1[0-9])\.' && { echo "$candidate"; return 0; }
	done
	return 1
}

install_from_source() {
	local rid="$1" src dotnet here
	here="$(cd "$(dirname "${BASH_SOURCE[0]:-.}")" 2>/dev/null && pwd || true)"
	if [ -n "$here" ] && [ -f "$here/../OptimizedUnity.slnx" ]; then
		src="$(cd "$here/.." && pwd)"
	else
		command -v git >/dev/null 2>&1 || die "git is required for --from-source"
		say "Cloning $REPO"
		local clone=(git clone --quiet --depth 1)
		[ "$VERSION" != latest ] && clone+=(--branch "$VERSION")
		"${clone[@]}" "https://github.com/$REPO.git" "$TMP/src"
		src="$TMP/src"
	fi

	# Native AOT links with the platform toolchain.
	if [ "$(uname -s)" = Darwin ]; then
		xcode-select -p >/dev/null 2>&1 || { xcode-select --install 2>/dev/null || true; die "Xcode Command Line Tools are required; finish their installation, then re-run."; }
	else
		command -v clang >/dev/null 2>&1 || die "clang and zlib are required (Debian/Ubuntu: sudo apt-get install -y clang zlib1g-dev)"
	fi

	if ! dotnet="$(find_dotnet)"; then
		say "Installing the .NET 10 SDK into $INSTALL_DIR/dotnet"
		download https://dot.net/v1/dotnet-install.sh "$TMP/dotnet-install.sh"
		bash "$TMP/dotnet-install.sh" --channel 10.0 --install-dir "$INSTALL_DIR/dotnet" >/dev/null
		dotnet="$INSTALL_DIR/dotnet/dotnet"
	fi

	say "Building $rid with Native AOT (this takes a minute)"
	DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "$dotnet" publish "$src/src/OptimizedUnity" -c Release -r "$rid" -o "$TMP/out" -nologo -v quiet >/dev/null \
		|| die "build failed; run: $dotnet publish $src/src/OptimizedUnity -c Release -r $rid"
	place "$TMP/out/unity"
}

place() {
	mkdir -p "$BIN_DIR"
	# Replace atomically so a running shim is never half-written.
	cp "$1" "$BIN_DIR/.unity.new"
	chmod 755 "$BIN_DIR/.unity.new"
	if [ "$(uname -s)" = Darwin ]; then xattr -d com.apple.quarantine "$BIN_DIR/.unity.new" 2>/dev/null || true; fi
	mv -f "$BIN_DIR/.unity.new" "$BIN_DIR/unity"
}

main() {
	if [ "$UNINSTALL" = 1 ]; then uninstall; return; fi
	local rid
	rid="$(detect_rid)"
	ensure_unity_cli
	if [ "$FROM_SOURCE" = 1 ]; then install_from_source "$rid"; else install_release "$rid"; fi
	say "Installed $BIN_DIR/unity"

	if [ "$NO_MODIFY_PATH" = 1 ]; then
		say "PATH not modified. Put $BIN_DIR before ~/.unity/bin on your PATH."
	else
		configure_path
		say "Added $BIN_DIR to PATH in your shell startup files."
	fi

	if "$BIN_DIR/unity" --version >/dev/null 2>&1; then
		say "Wrapping Unity CLI $("$BIN_DIR/unity" --version 2>/dev/null | tail -n1)"
	else
		warn "the Unity CLI did not answer; install it with: curl -fsSL https://unity.com/install.sh | bash"
	fi
	printf '\nOpen a new terminal, then check with: command -v unity   (expect %s/unity)\n' "$BIN_DIR"
	printf 'Bypass the wrapper for one call with UNITY_WRAP=off unity ...\n'
}

main
