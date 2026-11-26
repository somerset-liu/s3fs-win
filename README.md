# s3fs-win 托盘前端（源码）

> ⚠️ **本项目由 AI 生成**（含全部源码与文档），经人工实测验收。 AI-generated; human-reviewed.

Windows 托盘图形前端，包装同目录下的 `s3fs.exe`（s3fs-fuse 1.97，FUSE 层走 WinFsp）。
本目录是重写版的全部源码；旧版源码随 `/tmp` 重启丢了，所以按规格重写。

仓库内容：`*.cs` + `S3fsWinGui.csproj`（托盘前端源码）；`s3fs-win-1.97-x64-r9.patch`（s3fs-fuse 1.97 的全部 Windows 适配，对 pristine 上游可直接 `git apply`）；`s3fs-win-1.97-x64-r9-BUILD.md`（交叉编译步骤与 Windows 坑位）。

- 目标框架：`net8.0-windows` + WinForms，自包含单文件发布（用户机不用装 .NET）
- 在 Linux 上交叉编译 + 用 wine 验证，**没有在 Windows 实机跑过**

## 构建

```bash
dotnet build -c Release --no-incremental                                   # 要求 0 警告 0 错误
dotnet publish -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true                                              # publish 目录只有 1 个 exe
```

发布配置里开了 `IncludeAllContentForSelfExtract` 与 `EnableCompressionInSingleFile`：
前者把 WindowsDesktop 的几个原生 DLL 也打进单文件（否则 publish 目录会多出 5 个 DLL），
后者压一遍体积（65 MB 量级）。代价是首次运行会解压到临时目录，启动多几百毫秒。

产物拷到部署目录后改名/就叫 `s3fs-win.exe`，跟 `s3fs.exe`、23 个 DLL、`mime.types` 放同一层。

## 先装 WinFsp（必须）

`s3fs.exe` 自己不带内核驱动，盘符由 WinFsp 提供。没装就挂不上，报：

```
Cannot create WinFsp-FUSE file system: FSD not found.
```

跑包里的 `winfsp-2.2.26323.msi` 即可（要管理员，装完不用重启）。
程序认得这行日志，会直接在日志框里标出来，不用去猜。

## 界面字段

| 字段 | 对应 s3fs 参数 | 说明 |
| --- | --- | --- |
| 桶名 | 位置参数 | 原样传，不做斜杠转换 |
| 端点 URL | `-o url=` | 例 `https://s3.example.com:9000`；服务端若只收 https，写 `http://` 会被回 400 |
| Access Key / Secret Key | 写进凭据文件 | 两者都非空时程序代写 `passwd-s3fs`；任一为空则不代写、只用文件里已有的内容，并在日志与气泡里明确提示 |
| 挂载点 | 位置参数 | 盘符写 `S:`（`S:\` 会自动归一成 `S:`），也可以是已存在的目录 |
| 凭据文件 | `-o passwd_file=` | 默认 `<exe 目录>\passwd-s3fs`，内容一行 `桶名:AccessKey:SecretKey`（也接受 `AccessKey:SecretKey`），UTF-8 无 BOM |
| mime 文件 | `-o mime=` | 默认 `<exe 目录>\mime.types`。s3fs 在 Windows 上只找 `/etc/mime.types`，不传就是没有 Content-Type |
| 临时目录 tmpdir | `-o tmpdir=` | **必给**，默认 `<exe 目录>\tmp`，挂载前自动创建 |
| 缓存目录 use_cache | `-o use_cache=` | 留空即不带这个选项（磁盘读缓存）。跟 tmpdir 是两回事 |
| 额外选项 options | 逐个 `-o` | 逗号分隔，默认 `use_path_request_style,listobjectsv2,retries=5,connect_timeout=30` |
| 调试日志 | `-o dbglevel=info` | 勾上就加，用的是 `dbglevel`，不是 `-d` |
| 开机自启 | HKCU Run 项 | 见下 |

### 参数拼装的几条硬规矩

1. **进 `-o` 的路径一律转成正斜杠绝对路径**。s3fs 解析 `-o` 的 value 时把 `\` 当转义符吃掉，
   `passwd_file=C:\Soft\s3fs-win\passwd-s3fs` 会变成 `stat(C:Softs3fs-winpasswd-s3fs)` 然后退出码 1。
   相对路径也不行：`tmpdir=tmp` 在 mingw 下 `statvfs` 拿不到容量，会误报 “not enough disk space … already has 0.000 MB”。
2. 参数用 `ProcessStartInfo.ArgumentList` 逐项 `Add`，不手拼字符串，路径带空格也不会断。
3. **同名选项只出现一次**，且写在 options 里的同名项**优先于**程序自动生成的值。
   程序托管的项：`passwd_file` / `url` / `no_check_certificate` / `mime` / `tmpdir` / `use_cache` / `umask` / `dbglevel` / `use_path_request_style`。
   （`use_path_request_style` 的默认值就在 options 文本框里，删掉它表示你要用虚拟主机风格。）
4. `tmpdir` 永远出现在命令行上（s3fs 的临时目录硬编码 `/tmp` 且不读 `TMP`/`TEMP`）；
   目录建不出来只记日志、不弹异常窗（exe 放在 `Program Files` 这类只读目录时常见）。
5. 不带 `-f` 之外的调试开关；挂载是 `-f` 前台子进程，由本程序持有，结束它即卸载。
6. 1.97 没有 `readdir_optimize`，写上去属于未知选项、直接致命退出；读旧配置时会自动剔除。
7. **`no_check_certificate` 始终加上**（跟在 `url` 后面）。服务端只收 https 且证书自签时（没有受信 CA 签发），
   而 s3fs 1.95/1.97 **没有 `CAfile`/`capath`** 可以指定信任哪张证书，只能整块跳过校验；
   少了它就报 `server certificate verification failed` / `SSL: error`。
   换成正式 CA 证书后这个选项可以去掉（在 options 里写同名项即可覆盖，去重后仍只出现一次）。

8. **`umask=000` 始终加上**（跟在 `tmpdir` / `use_cache` 后面）。s3fs 报的是 POSIX 权限，WinFsp 要把
   `st_mode` + uid/gid 翻译成 Windows ACL；默认 umask 下根目录翻出来是 `0755`，而属主 uid 在 Windows 侧
   对不上任何账户，资源管理器可能把整个盘符判成「拒绝访问」。**若出现拒绝访问先加这个**：加上之后拒绝
   访问确实消失了，但两者因果在真机上还没确认（也可能是挂载还没起来时的时序问题），别当成已证实的修复。
   副作用是盘符上不再区分属主，人人可读写；要收紧就在 options 里写 `umask=022`。

## 配置文件 config.json

跟 exe 同目录，**首次运行自动生成**，UTF-8 无 BOM、camelCase；文件缺字段就用默认值，
读不懂（语法坏了）也只用默认值继续跑，不弹窗。字段名与界面一一对应：
`bucket`、`endpoint`、`accessKeyId`、`secretAccessKey`、`mountPoint`、`passwdFile`、`mimeFile`、
`tmpDir`、`cacheDir`、`options`、`debug`、`autoStart`、`autoMountOnStart`、`startInTray`、`minimizeToTray`。

- `minimizeToTray`（默认 true）：点关闭只是隐藏到托盘；托盘菜单里的「退出」才结束程序（退出前自动卸载）。
- `autoMountOnStart`：启动后立刻挂载，配合 `--min` 做无人值守。界面上没有这个勾选框，只改配置文件。

> **从旧版本升级的要手动改一处**：老 `config.json` 里的 `endpoint` 存的是 `http://s3.example.com:9000`，
> 程序读到就照用、**不会**静默改写成 https（只有文件里缺失该字段时才用新的 https 默认值）。
> 服务端 9000 现在只收 https，所以请把 `endpoint` 手改成 `https://s3.example.com:9000`，或在界面上改端点后保存。

## 开机自启

勾选后写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的字符串值 `s3fs-win`，内容
`"<exe 绝对路径>" --min`；取消勾选就删除该值。写的是 HKCU，所以**不需要管理员**。

注意语义是「当前用户**登录时**启动」而不是「开机时启动」，盘符也只对当前登录用户可见。
要真正的开机无人值守，得用任务计划程序在「用户登录时」触发。

如果 `Run` 下已经存在同名但内容不同的值（比如另一个叫 s3fs-win 的程序），程序按「未启用」显示并在日志里提示，
勾选后会被本程序的路径覆盖。

## 命令行开关

| 开关 | 作用 |
| --- | --- |
| `--min` | 启动即进托盘，不弹主窗口（开机自启用的就是它） |
| `--print-args` | 按当前 config.json 渲染将要执行的命令行后退出，并写 `s3fs-win.args.txt`（含逐项 ArgumentList） |
| `--selftest` | 跑参数拼装自检（无窗口），全过退出码 0，并写 `s3fs-win.selftest.txt` |
| `--probe-instance` | 诊断用：报告单实例互斥体是否已被别的进程持有（`S3FS_PROBE_HOLD=秒` 可占住不放） |
| `-h` / `--help` | 帮助 |

单实例：命名互斥体 + 命名唤醒事件，都放在当前会话命名空间（每登录会话一个实例）。
已有实例在跑时再双击 exe，只会把它的窗口唤起来。

## 挂载成功怎么判的

不看单一信号，而是同时看三样：进程是否已退出、日志里有没有致命行
（`FSD not found`、`not enough disk space`、`credentials`/`permission`/`signature`、
`unknown third option`、`curlCode: 7` 等）、进程是否稳定存活 3 秒。
命中致命行时，日志框里那行原样保留并追加中文提示；进程退出时把退出码打出来。

## 常见报错

| 日志里的行 | 怎么办 |
| --- | --- |
| `Cannot create WinFsp-FUSE file system: FSD not found.` | 没装 WinFsp，跑包里的 msi |
| `There is not enough disk space … already has 0.000 MB.` | `tmpdir` 没给或给了相对路径/不存在的目录 |
| `unexpected error from stat(C:Soft…)：No such file or directory` | 有反斜杠混进 `-o`（本程序已自动转正斜杠；手敲命令行时要注意） |
| `specified unknown third option` | options 里有 1.97 不认识的选项（例：`readdir_optimize`） |
| `credentials` / `signature` / `permission` | Access Key、Secret Key 或 region 不对 |
| `curlCode: 7` | 端点连不上：URL、端口、防火墙 |
| `server certificate verification failed` / `SSL: error` | 自签证书没跳过校验：确认命令行上有 `-o no_check_certificate`（本程序默认会加） |
| 回 `400` / `Client sent an HTTP request to an HTTPS server` | 端点写成了 `http://`，本机 9000 只收 https |
| 提示 Access Key / Secret Key 为空 | 程序不会代写凭据文件，直接去改文件；或把密钥填回界面 |
| 盘符能进、资源管理器却报「拒绝访问」 | 先确认命令行上有 `-o umask=000`（本程序与 `mount.bat` 默认都带），见上面第 8 条 |

## 验证到哪一步了

本机（Linux + wine-11.0）已验：

- `dotnet build` / `dotnet publish` 均退出码 0，build 0 警告 0 错误，publish 目录 1 个 exe；
- `--selftest` 全部通过（含路径转正斜杠、同名选项去重与用户优先、`no_check_certificate` 与 `umask=000` 各自始终且只出现一次、
  老配置的 `http` 端点不被改写、`readdir_optimize` 剔除、相对 tmpdir 绝对化、参数表结构、config 容错、注册表读写）；
- 渲染出的命令行喂给包里的 `s3fs.exe`：mime 与凭据路径都被正常接受，走 `url=https://s3.example.com:9000`
  实连服务端无 TLS/证书报错，只停在 wine 缺驱动的 `FSD not found`（与包内 INSTALL.md 的结论一致）；
- GUI 窗口常驻不崩、单实例（第二个实例识别到已有实例并退出码 0）、`autoMountOnStart`
  确实拉起 `s3fs.exe` 子进程并自动建好 tmp 目录、代写的 `passwd-s3fs` 无 BOM。

**需要 Windows 实机确认的**：真实挂载与盘符可见性、托盘图标显示效果、开机自启在真机注册表上的表现。
