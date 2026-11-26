# s3fs-fuse 1.97 Windows x64 构建说明（r9）

[English](s3fs-win-1.97-x64-r9-BUILD-en.md) | 简体中文

> 本项目（Windows 适配补丁与本文档）由 AI 生成，经人工实测验收。

Linux mingw-w64 交叉编译 + WinFsp 2.2 FUSE3 兼容层。当前权威版本 r9。

## 1. 环境

- 构建树：`/home/somerset/s3fs-win-build`（`src-win/`、`src-linux/`、`build-win.sh`、`build-linux.sh`、`deps.sh`、`dist-r9/`）
- 依赖：MSYS2 sysroot（`deps.sh` 获取，解包在 `ws/mingw64`）、WinFsp 头文件/DLL（`winfsp/`，来自官方 MSI）
- 上游 pristine 源码：`/home/somerset/s3fs-fuse-src`（1.97，commit 9a05ae4）
- 补丁基线：`src-win` git 仓库 commit `88d243c`（"upstream 9a05ae4 pristine"），当前 diff 即 `-r9.patch`

## 2. 构建 Windows 版

```bash
bash /home/somerset/s3fs-win-build/build-win.sh
x86_64-w64-mingw32-strip /home/somerset/s3fs-win-build/src-win/src/s3fs.exe
```

要点（改动会踩坑的地方）：

- `build-win.sh` 已处理：`autogen.sh` 会用本地 git hash 覆盖 `default_commit_hash`，脚本恢复为 `9a05ae4` 并以 `GITCMD=no` 跳过 git 查询，版本串显示 `V1.97(base commit:9a05ae4)`。
- `-fexceptions` 必须开：Windows POSIX 层（`win_posix.h`）的 `-o add_head` regex 用 `std::regex` 实现，异常报错；上游默认 `-fno-exceptions`。
- 编译宏 `-D__MSYS__`，`PKG_CONFIG_PATH` 指向 `winfsp/lib` 与 `ws/mingw64/lib/pkgconfig`。
- 产物 ~1.79M，**脚本不自动 strip**，必须手动 strip（→ ~1.09M）。

## 3. 冒烟与回归

```bash
# wine 冒烟：DLL 必须与 exe 同目录（直接跑 src 目录的 exe rc=53 只是缺 DLL，不是回归）
cp -f /home/somerset/s3fs-win-build/src-win/src/s3fs.exe /home/somerset/s3fs-win-build/dist-r9/
cd /home/somerset/s3fs-win-build/dist-r9
wine ./s3fs.exe --version && wine ./s3fs.exe --help   # 两者 rc=0 且 stderr 0 字节

# Linux 原生回归 + 单测
bash /home/somerset/s3fs-win-build/build-linux.sh
make -C /home/somerset/s3fs-win-build/src-linux/src check   # 10/10
```

## 4. GUI（托盘程序）

```bash
cd /home/somerset/s3fs-win-gui
rm -rf bin obj publish-r8
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-r8
# 产物 publish-r8/s3fs-win.exe（~65.8M 自包含单文件）
```

GUI 源码 12 个 .cs：`MountPlan.cs` 管拼参（选项每项自动补 `-o`，`-f` 结尾；`StripOptionPrefix()` 容忍用户带 `-o` 前缀），`AppConfig.cs` 管 config.json。https 端点自动加 `no_check_certificate`；`tmpdir=` 恒存在，`use_cache` 仅填了目录时加。

## 5. 打包

- 在 stage 目录解压旧 zip，替换 `s3fs-win/s3fs.exe`（挂载程序）与 `s3fs-win/s3fs-win.exe`（GUI）——**这两个名字相近，别互相覆盖**（r8 曾把 GUI 覆盖成控制台程序导致「闪退」），然后 `zip -qr` 回 `/home/somerset/`。
- zip 内含 23 个 DLL、`winfsp-2.2.26323.msi`、mime.types、passwd 模板、mount.bat 等共 34 条目；DLL 集自 r3 起未变。

## 6. 交付物（r9）

| 路径 | 说明 |
| --- | --- |
| `/home/somerset/s3fs-win-1.97-x64-r9.zip` | 34 条目，md5 `882e545ae7d2bb12dc64b8d15c76f000`，s3fs.exe 1 092 096 B（strip 后）；私有交付物，含真实端点示例，**不入仓库** |
| `s3fs-win-1.97-x64-r9.patch` | 32 文件 +2686/−34（对 pristine 1.97 的全部 Windows 适配），在仓库根 |
| `s3fs-win-1.97-x64-r9-BUILD.md` | 本文件，在仓库根 |
| `/home/somerset/s3fs-win-build/dist-r9/` | 发布基线（exe 已 strip，DLL 集齐全） |

补丁自足性验证：

```bash
rm -rf /tmp/opencode/clean && mkdir -p /tmp/opencode/clean
cp -a /home/somerset/s3fs-fuse-src/. /tmp/opencode/clean/
cd /tmp/opencode/clean && git apply --check /home/somerset/s3fs-win-gui/s3fs-win-1.97-x64-r9.patch
```

## 7. Windows 侧关键坑位（重编时必读）

- **路径一律走 W 系 API**：s3fs 的对象名是 UTF-8，CRT 的 A 接口按系统 ANSI 代码页（GBK）解释——中文变乱码，部分字节序列还会破坏路径。`win_posix.h` 的 `s3fs_utf8_to_wide()` 与 `fuse_stat/_wmkdir/_wutime64/s3fs_DIR/s3fs_wopen` 系列已全面覆盖；`open` 不能做宏别名（iostreams 成员 `open()` 会被误伤），fdcache* 直接调 `s3fs_wopen`。
- `_stat64()` 拒绝带尾斜杠的路径（返回 ENOENT）；`mkdirp` 内部先去尾斜杠、先判 `EEXIST`（保存 errno 再复核，否则被 lstat 冲掉）。
- 盘根（`C:/`、`D:/`）`mkdir` 返回 EACCES/ENOENT 而非 EEXIST，`mkdirp` 跳过首段。
- `st_ino` 必须非 0：WinFsp 把它直通 Windows FileId（`IndexNumber`），恒 0 会让 Explorer 粘贴灰掉（拖拽走 PIDL 不受影响）。现用 FNV-1a 路径哈希（`win_inode_from_path`）。
- WinFsp 的 FUSE 层不校验未知 `-o`、静默忽略——wine 只能证明「解析不崩」，选项是否生效只能真机验证。
- `use_cache` 下的性能收益：反复读同一批文件/编辑已有文件最明显；一次性大拷贝提升有限。
