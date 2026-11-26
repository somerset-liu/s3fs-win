# s3fs-fuse 1.97 Windows x64 Build Guide (r9)

[English](s3fs-win-1.97-x64-r9-BUILD-en.md) | [简体中文](s3fs-win-1.97-x64-r9-BUILD.md)

> This project (the Windows adaptation patch and this document) is AI-generated and has been reviewed and tested by a human.

Linux mingw-w64 cross-compilation + the WinFsp 2.2 FUSE3 compatibility layer. Current authoritative version: r9.

## 1. Environment

- Build tree: `/home/somerset/s3fs-win-build` (`src-win/`, `src-linux/`, `build-win.sh`, `build-linux.sh`, `deps.sh`, `dist-r9/`)
- Dependencies: MSYS2 sysroot (fetched by `deps.sh`, unpacked into `ws/mingw64`), WinFsp headers/DLL (`winfsp/`, from the official MSI)
- Upstream pristine source: `/home/somerset/s3fs-fuse-src` (1.97, commit `9a05ae4`)
- Patch baseline: `src-win` git repo commit `88d243c` ("upstream 9a05ae4 pristine"); the current diff is `-r9.patch`

## 2. Building the Windows version

```bash
bash /home/somerset/s3fs-win-build/build-win.sh
x86_64-w64-mingw32-strip /home/somerset/s3fs-win-build/src-win/src/s3fs.exe
```

Gotchas (places where changes tend to break):

- `build-win.sh` already handles: `autogen.sh` overwrites `default_commit_hash` with the local git hash, so the
  script restores `9a05ae4` and skips git queries with `GITCMD=no`; the version string shows
  `V1.97(base commit:9a05ae4)`.
- `-fexceptions` must be on: the Windows POSIX layer (`win_posix.h`) implements the `-o add_head` regex with
  `std::regex`, which throws; upstream defaults to `-fno-exceptions`.
- Compile define `-D__MSYS__`; `PKG_CONFIG_PATH` points to `winfsp/lib` and `ws/mingw64/lib/pkgconfig`.
- Output is ~1.79 MB and the script does **not** strip it automatically — strip by hand (→ ~1.09 MB).

## 3. Smoke test and regression

```bash
# wine smoke test: the DLLs must sit next to the exe (running the exe straight from the src dir with rc=53
# only means missing DLLs, not a regression)
cp -f /home/somerset/s3fs-win-build/src-win/src/s3fs.exe /home/somerset/s3fs-win-build/dist-r9/
cd /home/somerset/s3fs-win-build/dist-r9
wine ./s3fs.exe --version && wine ./s3fs.exe --help   # both rc=0 with 0 bytes on stderr

# Native Linux regression + unit tests
bash /home/somerset/s3fs-win-build/build-linux.sh
make -C /home/somerset/s3fs-win-build/src-linux/src check   # 10/10
```

## 4. GUI (tray program)

```bash
cd /home/somerset/s3fs-win-gui
rm -rf bin obj publish-r8
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-r8
# output: publish-r8/s3fs-win.exe (~65.8 MB self-contained single file)
```

The GUI is 12 `.cs` files: `MountPlan.cs` builds the argument list (every option gets an automatic `-o`
prefix, `-f` last; `StripOptionPrefix()` tolerates options the user typed with `-o`), `AppConfig.cs` manages
config.json. https endpoints automatically get `no_check_certificate`; `tmpdir=` is always present and
`use_cache` is only added when a directory is filled in.

## 5. Packaging

- Unpack the previous zip into a stage directory, replace `s3fs-win/s3fs.exe` (mounter) and
  `s3fs-win/s3fs-win.exe` (GUI) — **the two names are similar; do not overwrite one with the other** (r8 once
  overwrote the GUI with the console program, causing an instant-exit "flash crash") — then `zip -qr` back to
  `/home/somerset/`.
- The zip contains 23 DLLs, `winfsp-2.2.26323.msi`, mime.types, passwd templates, mount.bat, etc. — 34 entries
  in total; the DLL set is unchanged since r3.

## 6. Deliverables (r9)

| Path | Notes |
| --- | --- |
| `/home/somerset/s3fs-win-1.97-x64-r9.zip` | 34 entries, md5 `882e545ae7d2bb12dc64b8d15c76f000`, s3fs.exe 1 092 096 B (stripped); private deliverable containing real-endpoint examples, **not in the repository** |
| `s3fs-win-1.97-x64-r9.patch` | 32 files, +2686/−34 (all Windows adaptations against pristine 1.97), at the repo root |
| `s3fs-win-1.97-x64-r9-BUILD.md` | This document (Chinese), at the repo root; English: `s3fs-win-1.97-x64-r9-BUILD-en.md` |
| `/home/somerset/s3fs-win-build/dist-r9/` | Release baseline (exe stripped, full DLL set) |

Patch self-containedness check:

```bash
rm -rf /tmp/opencode/clean && mkdir -p /tmp/opencode/clean
cp -a /home/somerset/s3fs-fuse-src/. /tmp/opencode/clean/
cd /tmp/opencode/clean && git apply --check /home/somerset/s3fs-win-gui/s3fs-win-1.97-x64-r9.patch
```

## 7. Key Windows pitfalls (must-read before rebuilding)

- **All local paths go through the W (wide) APIs**: s3fs object names are UTF-8, but the CRT's A interfaces
  interpret them using the system ANSI code page (GBK) — Chinese turns to mojibake, and some byte sequences
  even corrupt the path. `win_posix.h`'s `s3fs_utf8_to_wide()` and the `fuse_stat/_wmkdir/_wutime64/s3fs_DIR/
  s3fs_wopen` family cover this everywhere; `open` must not be macro-renamed (iostreams' member `open()` would
  be caught too), so fdcache* calls `s3fs_wopen` directly.
- `_stat64()` rejects paths with a trailing slash (returns ENOENT); `mkdirp` strips trailing slashes first and
  checks `EEXIST` first (saving errno before re-checking, otherwise lstat clobbers it).
- Drive roots (`C:/`, `D:/`) return EACCES/ENOENT from `mkdir` instead of EEXIST, so `mkdirp` skips the first
  segment.
- `st_ino` must be non-zero: WinFsp passes it straight through as the Windows FileId (`IndexNumber`); leaving
  it at 0 makes Explorer's paste grey out (drag & drop uses PIDLs and is unaffected). Currently an FNV-1a
  path hash (`win_inode_from_path`).
- WinFsp's FUSE layer does not validate unknown `-o` options and silently ignores them — wine can only prove
  "parsing doesn't crash"; whether an option actually works can only be verified on real hardware.
- `use_cache` performance payoff: most visible when reading the same files repeatedly / editing existing
  files; one-shot large copies gain little.
