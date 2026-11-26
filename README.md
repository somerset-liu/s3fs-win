# s3fs-win Tray Frontend (Source)

[English](README.md) | [简体中文](README.zh-CN.md)

> ⚠️ **This project is AI-generated** (all source code and documentation) and has been reviewed and tested by a human.

A Windows tray GUI frontend that wraps `s3fs.exe` (s3fs-fuse 1.97, with the FUSE layer provided by WinFsp).
This directory contains the full source of the rewrite.

Repository contents: `*.cs` + `S3fsWinGui.csproj` (tray frontend source); `s3fs-win-1.97-x64-r9.patch` (all Windows adaptations for s3fs-fuse 1.97, applies to pristine upstream with `git apply`); `s3fs-win-1.97-x64-r9-BUILD.md` (cross-compilation steps and Windows pitfalls).

- Target framework: `net8.0-windows` + WinForms, published as a self-contained single file (no .NET install needed on target machines)
- Cross-compiled on Linux, and run on real Windows machines.

## Build

```bash
dotnet build -c Release --no-incremental                                   # must be 0 warnings, 0 errors
dotnet publish -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true                                              # publish dir contains a single exe
```

The publish configuration enables `IncludeAllContentForSelfExtract` and `EnableCompressionInSingleFile`:
the former packs the WindowsDesktop native DLLs into the single file (otherwise the publish directory would
contain 5 extra DLLs), the latter compresses it (on the order of 65 MB). The cost is that the first run
extracts to a temp directory, adding a few hundred milliseconds to startup.

Copy the output to the deployment directory named `s3fs-win.exe`, alongside `s3fs.exe`, the 23 DLLs and `mime.types`.

## Install WinFsp first (required)

`s3fs.exe` does not ship a kernel driver; the drive letter is provided by WinFsp. Without it, mounting fails with:

```
Cannot create WinFsp-FUSE file system: FSD not found.
```

Run the bundled `winfsp-2.2.26323.msi` (requires admin, no reboot needed).
The program recognizes this log line and flags it directly in the log pane.

## UI fields

| Field | s3fs argument | Notes |
| --- | --- | --- |
| Bucket | positional | Passed through as-is, no slash conversion |
| Endpoint URL | `-o url=` | e.g. `https://s3.example.com:9000`; if the server only accepts https, `http://` gets a 400 |
| Access Key / Secret Key | written to the credential file | When both are non-empty the program writes `passwd-s3fs` for you; if either is empty it does not, only uses what the file already contains, and says so in the log and a notification |
| Mount point | positional | Drive letter like `S:` (`S:\` is normalized to `S:`), or an existing directory |
| Credential file | `-o passwd_file=` | Defaults to `<exe dir>\passwd-s3fs`, one line `bucket:AccessKey:SecretKey` (`AccessKey:SecretKey` also accepted), UTF-8 without BOM |
| MIME file | `-o mime=` | Defaults to `<exe dir>\mime.types`. On Windows s3fs only looks for `/etc/mime.types`; without this option there is no Content-Type |
| Temp dir tmpdir | `-o tmpdir=` | **Always set**, defaults to `<exe dir>\tmp`, created before mounting |
| Cache dir use_cache | `-o use_cache=` | Empty = option not passed (on-disk read cache). Separate from tmpdir |
| Extra options | one `-o` each | Comma-separated, defaults to `use_path_request_style,listobjectsv2,retries=5,connect_timeout=30` |
| Debug log | `-o dbglevel=info` | Added when checked; uses `dbglevel`, not `-d` |
| Start with Windows | HKCU Run entry | See below |

### Hard rules of argument assembly

1. **Paths that go into `-o` are always converted to absolute paths with forward slashes.** s3fs treats `\`
   as an escape character when parsing `-o` values: `passwd_file=C:\Soft\s3fs-win\passwd-s3fs` turns into
   `stat(C:Softs3fs-winpasswd-s3fs)` and exit code 1. Relative paths fail too: `tmpdir=tmp` makes mingw's
   `statvfs` unable to report capacity, producing a bogus "not enough disk space … already has 0.000 MB".
2. Arguments are added one by one via `ProcessStartInfo.ArgumentList`, never hand-concatenated, so paths with
   spaces survive.
3. **Each option name appears at most once**, and the same option written in the options field **takes priority
   over** program-generated values. Program-managed options: `passwd_file` / `url` / `no_check_certificate` /
   `mime` / `tmpdir` / `use_cache` / `umask` / `dbglevel` / `use_path_request_style`.
   (The `use_path_request_style` default sits in the options textbox; removing it switches you to
   virtual-host-style addressing.)
4. `tmpdir` is always on the command line (s3fs hardcodes `/tmp` as its temp dir and ignores `TMP`/`TEMP`);
   if the directory cannot be created it is only logged, no exception dialog (common when the exe sits in a
   read-only directory like `Program Files`).
5. No debug switches other than `-f`; mounting runs as a `-f` foreground child process owned by this program,
   and stopping it unmounts.
6. 1.97 has no `readdir_optimize`; writing it is an unknown option and exits fatally. It is removed
   automatically when reading older configs.
7. **`no_check_certificate` is always added** (right after `url`). When the server only accepts https with a
   self-signed certificate, and s3fs 1.95/1.97 has **no `CAfile`/`capath`** to trust a specific certificate,
   skipping verification entirely is the only option; without it you get
   `server certificate verification failed` / `SSL: error`.
   Once you switch to a proper CA certificate the option can be dropped (write the same option in the options
   field to override; dedup keeps a single occurrence).

8. **`umask=000` is always added** (after `tmpdir` / `use_cache`). s3fs reports POSIX permissions and WinFsp
   translates `st_mode` + uid/gid into Windows ACLs; with the default umask the root dir translates to `0755`,
   while the owner uid matches no Windows account, so Explorer may show the whole drive as "Access denied".
   **If you see access denied, add this first**: adding it did make the error disappear, but the causality has
   not been confirmed on real hardware (it may also have been a timing issue while the mount was not yet up),
   so treat it as an unproven fix. Side effect: the drive no longer distinguishes owners, everyone can read
   and write; tighten with `umask=022` in the options field if needed.

## Config file config.json

Lives next to the exe, **auto-created on first run**, UTF-8 without BOM, camelCase; missing fields fall back
to defaults, and an unreadable (broken-syntax) file also just falls back to defaults without any dialog.
Field names map 1:1 to the UI: `bucket`, `endpoint`, `accessKeyId`, `secretAccessKey`, `mountPoint`,
`passwdFile`, `mimeFile`, `tmpDir`, `cacheDir`, `options`, `debug`, `autoStart`, `autoMountOnStart`,
`startInTray`, `minimizeToTray`.

- `minimizeToTray` (default true): clicking close only hides to the tray; "Exit" in the tray menu ends the
  program (auto-unmount before exit).
- `autoMountOnStart`: mounts immediately after launch, combine with `--min` for unattended use. There is no
  checkbox for it in the UI; edit the config file only.

> **Manual change needed when upgrading from older versions**: old `config.json` files store `http://…` in
> `endpoint`; the program uses it as-is and does **not** silently rewrite it to https (the new https default
> only applies when the field is missing). If your server only accepts https, change `endpoint` to the
> `https://…` form manually, or edit the endpoint in the UI and save.

## Start with Windows

When checked, the program writes a string value `s3fs-win` under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` containing `"<absolute exe path>" --min`;
unchecking deletes it. It is HKCU, so **no admin rights needed**.

Note the semantics: it starts at **user logon**, not at boot, and the drive letter is only visible to that
user. For true unattended boot mounting, use Task Scheduler triggered "at log on".

If a value with the same name but different content already exists under `Run` (e.g. another program named
s3fs-win), the program shows "disabled" and logs a hint; checking the box overwrites it with this program's path.

## Command-line switches

| Switch | Purpose |
| --- | --- |
| `--min` | Start straight to the tray, no main window (used by autostart) |
| `--print-args` | Render the command line for the current config.json and exit, writing `s3fs-win.args.txt` (including the per-item ArgumentList) |
| `--selftest` | Run the argument-assembly self test (no window); exit code 0 when all pass, writes `s3fs-win.selftest.txt` |
| `--probe-instance` | Diagnostic: report whether the single-instance mutex is held by another process (`S3FS_PROBE_HOLD=seconds` holds it) |
| `-h` / `--help` | Help |

Single instance: a named mutex plus a named wakeup event, both in the current session namespace (one instance
per logon session). Double-clicking the exe while an instance is running merely wakes its window.

## How "mount succeeded" is decided

No single signal; three things are checked together: whether the process exited, whether the log contains a
fatal line (`FSD not found`, `not enough disk space`, `credentials`/`permission`/`signature`,
`unknown third option`, `curlCode: 7`, etc.), and whether the process stays alive for 3 seconds.
When a fatal line hits, it stays in the log pane with a hint appended; on exit the exit code is printed.

## Common errors

| Log line | What to do |
| --- | --- |
| `Cannot create WinFsp-FUSE file system: FSD not found.` | WinFsp not installed; run the bundled MSI |
| `There is not enough disk space … already has 0.000 MB.` | `tmpdir` missing, relative, or nonexistent |
| `unexpected error from stat(C:Soft…): No such file or directory` | A backslash slipped into `-o` (this program converts to forward slashes automatically; beware when typing commands by hand) |
| `specified unknown third option` | An option unknown to 1.97 in the options field (e.g. `readdir_optimize`) |
| `credentials` / `signature` / `permission` | Wrong Access Key, Secret Key or region |
| `curlCode: 7` | Endpoint unreachable: URL, port, firewall |
| `server certificate verification failed` / `SSL: error` | Self-signed certificate without skipping verification: confirm `-o no_check_certificate` is on the command line (this program adds it by default) |
| `400` / `Client sent an HTTP request to an HTTPS server` | Endpoint written as `http://` while the server only accepts https |
| Prompt that Access Key / Secret Key are empty | The program will not write the credential file; edit the file directly, or fill the keys back in the UI |
| Drive opens but Explorer says "Access denied" | First confirm `-o umask=000` is on the command line (both this program and `mount.bat` add it by default), see rule 8 above |

Verified locally (Linux + wine-11.0):

- `dotnet build` / `dotnet publish` both exit 0; build has 0 warnings 0 errors; the publish directory has a single exe;
- `--selftest` fully passes (forward-slash paths, same-option dedup with user priority, `no_check_certificate`
  and `umask=000` each present exactly once, legacy `http` endpoints not rewritten, `readdir_optimize` removal,
  relative tmpdir made absolute, argument list structure, config fault tolerance, registry read/write);
- the rendered command line was fed to the bundled `s3fs.exe`: mime and credential paths accepted,
  connecting to `url=https://s3.example.com:9000` shows no TLS/certificate errors and stops only at wine's
  missing-driver `FSD not found` (consistent with the bundled INSTALL.md);
- the GUI window stays up without crashing, single instance works (a second instance detects the first and
  exits 0), `autoMountOnStart` really launches the `s3fs.exe` child, creates the tmp dir, and the
  auto-written `passwd-s3fs` has no BOM.

## License

This project (GUI source, Windows adaptation patch, documentation) is distributed under **GPL-2.0-or-later**,
matching upstream s3fs-fuse; the patch is derived from
[s3fs-fuse](https://github.com/s3fs-fuse/s3fs-fuse) (whose copyright belongs to the upstream authors).
Full license text: [LICENSE](LICENSE).
