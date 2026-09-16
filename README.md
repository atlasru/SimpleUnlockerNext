# SimpleUnlocker Next

Experimental Windows 10/11 x64 recovery toolkit (Foundation). Source code is available in `src/`, offline tests in `tests/`, and the Windows portable build workflow in `.github/workflows/build.yml`.

This Foundation is independently written and does **not** yet contain the original application's source or feature set. Inspired by [DesConnet's SimpleUnlocker](https://github.com/theDesConnet/SimpleUnlocker). Distributed under GPL-2.0; see LICENSE.

**Not production-ready.** The Windows binary builds and the recovery tests pass in CI, but launch and recovery behavior on clean Windows 10/11 machines have not yet been validated.

## Download and startup diagnostics

Download the `SimpleUnlockerNext-Foundation-win-x64` artifact from the latest successful [GitHub Actions build](https://github.com/atlasru/SimpleUnlockerNext/actions). Extract the **inner portable ZIP completely**, keeping all DLL files next to `Unlocker.Desktop.exe`. Avoid launching the executable directly from a ZIP archive.

If the process immediately exits without showing a window:

1. Run `Diagnose-Startup.cmd` **from the extracted portable folder**. It launches the bundled diagnostic script in a new PowerShell process, without modifying system-wide execution policy or requiring administrator rights.
2. Alternatively, in PowerShell from that folder run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\Diagnose-Startup.ps1"`. Only run scripts you obtained from the trusted project archive.
3. Open `startup-diagnostics.txt` generated next to the executable. Review/redact local user names and file paths before sharing the report. The diagnostics also capture `%LOCALAPPDATA%\SimpleUnlockerNext\Logs\startup.log` when available.

A successful CI build and offline unit tests do **not** prove that WinUI launched successfully on a user's desktop. Do not assume that any particular runtime DLL caused a startup crash until the Windows Application event report or other diagnostic evidence identifies it.
