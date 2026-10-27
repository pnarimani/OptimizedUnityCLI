# OptimizedUnity

A Native AOT drop-in `unity` that wraps the official Unity CLI and makes its output compact. It forces `--json`, prunes the noise, and renders terse text, so agents and scripts spend far fewer tokens. Exit codes are kept.

## Benchmark

Real calls against a large Unity 6 project with its Editor open, comparing the plain Unity CLI with the wrapper. Every command was run three ways: the CLI's default output, the CLI with `--json`, and the wrapper. Token counts cover stdout plus stderr, counted with `tiktoken` `cl100k_base`. Claude's tokenizer differs, so compare the ratios rather than the absolute numbers.

| Command | Default | `--json` | Wrapped | Smaller than `--json` |
|---|---:|---:|---:|---:|
| `unity status` | 62 | 182 | 52 | 3.5× |
| `unity list` | 5,271 | 30,980 | 5,032 | 6.2× |
| `unity command` | 203 | 858 | 137 | 6.3× |
| `unity cmd --query scene` | 1,900 | 17,853 | 968 | 18× |
| `unity cmd console get` | 31,206 | 33,720 | 2,896 | 12× |
| `unity cmd console get --level error` | 5,403 | 5,992 | 1,235 | 4.9× |
| `unity cmd scene get_hierarchy` | 3,146 | 5,042 | 744 | 6.8× |
| `unity cmd package list` | 16,998 | 22,447 | 3,362 | 6.7× |
| `unity cmd asset find --type Material` | 16,370 | 20,677 | 1,734 | 12× |
| `unity cmd build get_settings` | 97,837 | 118,724 | 10,926 | 11× |
| `unity cmd test list` | 560,625 | 778,489 | 1,544 | 504× |
| `unity cmd test run` (one assembly, all passing) | 70 | 5,549 | 52 | 107× |
| `unity cmd console get --help` | 983 | — | 178 | 5.5× smaller than default |
| `unity status --help` | 519 | — | 151 | 3.4× smaller than default |

Across the twelve data commands that is 1,040,513 tokens with `--json`, 739,091 with the default output, and 28,682 wrapped: 36× and 26× fewer.

Notes:
- **`test list`:** above 300 tests the wrapper prints counts per assembly and writes the full grouped list to a file, so most of that saving is the list moving out of the reply.
- **Speed:** wall time is the Editor's. The wrapper adds roughly 0.6 s to a 3.5 MB `test list` and less elsewhere. `cmd <command> --help` takes about 1 s instead of being instant, because it reads the live command schema from the Editor.

## Install

macOS, Linux and WSL:

```bash
curl -fsSL https://raw.githubusercontent.com/pnarimani/OptimizedUnityCLI/main/scripts/install.sh | bash
```

Windows (PowerShell):

```powershell
irm https://raw.githubusercontent.com/pnarimani/OptimizedUnityCLI/main/scripts/install.ps1 | iex
```

The installer does the following:
- Installs the Unity CLI if it is missing.
- Downloads the binary for your OS and CPU from the latest GitHub release and verifies its SHA-256.
- Installs it as `unity`, in `~/.optimized-unity/bin` or `%LOCALAPPDATA%\OptimizedUnity\bin`.
- Puts that directory ahead of `~/.unity/bin` on your PATH. It appends a marked block to your shell startup files, or adds itself to the front of the user PATH on Windows.
- On Windows it also adds that block to your PowerShell profile (`Documents\PowerShell\profile.ps1`, plus the Windows PowerShell one when scripts are allowed) and Git Bash's `~/.bashrc`. Terminal multiplexers such as Zellij and psmux start every pane with the PATH their server started with, so a user PATH change does not reach them or the tools launched from them (Claude Code and its shells). The block fixes PATH in each new pane. For other shells, restart the multiplexer server.

Installer options:

| Option | Shell flag (`… \| bash -s -- <flag>`) | Environment variable |
|---|---|---|
| Install a specific release | `--version v0.1.0` | `OPTIMIZED_UNITY_VERSION` |
| Build locally with .NET 10 (installed if missing) | `--from-source` | `OPTIMIZED_UNITY_FROM_SOURCE=1` |
| Don't install the Unity CLI | `--skip-unity-cli` | `OPTIMIZED_UNITY_SKIP_UNITY_CLI=1` |
| Don't edit PATH | `--no-modify-path` | `OPTIMIZED_UNITY_NO_MODIFY_PATH=1` |
| Remove optimized-unity | `--uninstall` | `OPTIMIZED_UNITY_UNINSTALL=1` |

From a checkout, run `./scripts/install.sh --from-source`. To install from a fork, set `OPTIMIZED_UNITY_REPO=<owner>/<repo>`.

The one-liners download from GitHub Releases, so they work once a release has been published (see below).

## Releasing

Push a `v*` tag. `.github/workflows/release.yml` then:
1. Runs the tests.
2. Builds `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, `win-x64` and `win-arm64`.
3. Publishes `optimized-unity-<rid>[.exe]` with `SHA256SUMS`.

## Environment

| Variable | Effect |
|---|---|
| `UNITY_WRAP=off` | Run the real CLI untouched |
| `UNITY_CLI_PATH` | Real CLI location (default `~/.unity/bin/unity`, then PATH, never itself) |
| `UNITY_OP_TIMEOUT_MS` | Opt-in hard timeout; exits 14 |
| `UNITY_WRAP_MAX_LINES` / `UNITY_WRAP_MAX_BYTES` | Output budget |
| `UNITY_WRAP_SPILL_DIR` | Where full outputs and blobs are saved |
