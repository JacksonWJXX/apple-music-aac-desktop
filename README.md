# Apple Music AAC Desktop

轻量的 Windows 中文桌面工具：输入一首 Apple Music 歌曲链接，保存 AAC 音频，按歌曲名称命名。

**这是源码发布。仓库不含任何人的登录、Cookie、加密凭据、音乐、下载缓存、运行环境、Apple 库或 Widevine 设备资料。** 不提供捆绑第三方运行库的“一键全家桶”。

## 功能

- 温和米色界面；下载在后台运行，可取消，已有文件不覆盖。
- 只接受单曲；带 `i=歌曲编号` 的专辑分享链接会转换成单曲，不展开整张专辑。
- 保存前检查 AAC 编码、曲目时长及完整解码；文件扩展名为 `.m4a`。
- 可更改保存文件夹；公开版默认使用当前 Windows 用户的“音乐/AppleMusic”。
- 登录指引和遮蔽粘贴框，不必手动制作 Cookie 文本。
- 可选 Windows DPAPI 当前用户加密记住登录，支持清除。
- 文件导入和用户触发的有限目录扫描作为备用方式，不自动提取浏览器凭据。

## 环境与首次安装

目前锁文件面向 **Windows x64、Python 3.13**，其他架构/版本没有验证。

1. 从 Python 官方来源安装 64 位 Python 3.13，保留 pip。
2. 安装 FFmpeg，并确认 `ffmpeg` 和 `ffprobe` 在 PATH。可使用 Windows 官方 WinGet 索引中的 `Gyan.FFmpeg` 包；本项目不再分发 FFmpeg。
3. 下载本仓库源码，解压到你自己选择的稳定文件夹，例如 `C:\Apps\AppleMusicAAC`。不要把真实 Cookie 放进源码目录。
4. 先阅读 `THIRD_PARTY_NOTICES.md` 和 `SECURITY.md`。下载引擎涉及非官方认证和 DRM 组件，使用前需自行确认授权和服务条款。
5. 在该目录打开终端，执行：

   ```console
   python tools/setup.py --acknowledge-third-party-risk --shortcut
   ```

   该命令创建项目自己的 `.runtime`，按锁文件下载依赖，编译本项目 GUI，并创建桌面快捷方式。不会修改全局 Python、启动服务或更改系统安全设置。

安装会使用固定版本 `applemusic-dl 2.4.12`、`gamdl 3.8.5` 及列出的依赖。增强版从明确的 GitHub 发布地址安装，不使用 PyPI 上同名的原版包。`construct 2.8.8` 只有源代码包：它的哈希被锁定，构建会运行其打包脚本；安装确认参数包含这一风险。其余运行依赖要求使用锁定的 Windows wheel。

**验证范围：**现有 Windows 环境通过了源码编译和离线合成测试，但这套引导安装尚未经过独立的全新电脑安装验证。锁文件固定运行依赖的供应包哈希，不保证不同电脑生成逐字节相同的构建产物。构建工具固定版本但没有单独锁定其供应包哈希。源码构建、上游包可用性及设备资料的合法使用限制仍然适用；请先在你自己的干净环境验证。

如果 Python、FFmpeg、.NET Framework 编译器或兼容 wheel 缺失，安装会停止并显示本机错误；不要通过关闭安全保护解决问题。本工具仍依赖你安装的 Python 基础环境和 FFmpeg，不是可复制到任意电脑的独立运行时。

## 登录指引：不用编辑文本文件

1. 打开工具，点击“登录指引”。
2. 点击“打开 Apple Music”，在浏览器自行登录自己的订阅账号。
3. 按 `Ctrl + Shift + I`，打开 **Application（应用）**；标签隐藏时点 `»`。
4. 左侧展开 **Storage → Cookies**，选择 `https://music.apple.com`。
5. 找到名为 **`media-user-token`** 的行，只复制 **Value（值）**。
6. 返回工具，在隐藏输入框中手动粘贴，然后点击“保存登录”。不需要复制整行，不需要在浏览器控制台执行代码。
7. 请自行复制普通文字覆盖剪贴板。工具不会自动读取或清空你的整个剪贴板。

**不要把值、Cookie 文件、截图或原始错误日志发送到聊天、Issue、邮件或 GitHub。** 本教程的示例占位符 `YOUR_TOKEN_HERE` 不是可用凭据。

“下次保存时加密记住登录”只控制**下一次保存**。勾选时以 DPAPI 密文保存，仅供当前 Windows 用户、本工具向 Apple 认证使用。取消勾选时，新提供的值仅在当前窗口内使用，**不会删除以前保存的加密登录**；关闭窗口后以前的登录仍可能被使用。要移除已有登录，明确点击“清除登录”。登录失效时也应清除后重新提供；无需向维护者提供密码或验证码。DPAPI 不防范拥有同一 Windows 用户权限的恶意软件。

下载库需要临时明文 Cookie 文件。本工具把副本放在用户权限受限的任务目录，正常结束、取消时删除；异常关机的遗留任务会在下次启动清理。你自行选择的原始导入文件不会被删除。

## 扫描匹配（备用）

点击“扫描匹配…”后，自己选择保存 Cookie 文本的下载文件夹。所选目录必须归当前 Windows 用户所有，位于名为 `Download`、`Downloads` 或 `下载` 的目录中（也可直接选择该目录）。工具只检查所选目录当前层，以及本工具的 `import` 目录：

- 文件名为 `cookies*.txt` 或 `applemusic*.txt`，最多匹配检查 100 个候选；不进入子文件夹。
- 排除名称包含 `template`、`example`、`sample`、`模板` 的文件及链接文件。
- 不扫描磁盘根目录、AppData 本身及其子目录、已知浏览器/便携浏览器配置目录或系统凭据库；检测到浏览器配置标记文件也会拒绝扫描。
- 仅检查必需的 Cookie 结构，不显示或记录值。列表显示候选文件路径、修改时间，由你选择导入。
- 不在后台定时扫描。更推荐使用“登录指引”直接粘贴值。

## 下载与保存

1. 粘贴一首歌曲的 Apple Music 链接。
2. 如需更改位置，点击“更改…”，选择可写文件夹。该非敏感设置仅保存于本机。
3. 点击“下载单曲”，等待读取信息、下载和完整性检查。
4. 点击“打开文件夹”。文件名采用歌曲名称；非法文件名字符会替换，同名时添加歌手、歌曲编号或序号。

取消会停止后台进程树并清理本工具的临时凭据及未完成复制；已完成的音频会保留。下载结果不代表获得商用、再分发或其他使用授权。

## 开发、测试和发布

```console
python tools/setup.py --compile-only
.runtime\Scripts\python.exe -I tests/test_tool.py
AppleMusicAAC.exe --self-test
python tools/audit_release.py
```

测试只使用合成音频/虚构凭据，不执行真实 Apple 登录下载。GUI 自检生成自己的空白界面渲染图，不截图浏览器或用户桌面。发布请遵守 `RELEASE.md` 的精确白名单和隐私审查流程。不得把 `.runtime`、上游 wheel、设备资料、私有目录或本机设置放入源码包/Release。

## English overview

A source-only Windows desktop frontend for one-song AAC downloads using the pinned upstream engine. It includes explicit manual login guidance, masked token paste, optional Windows CurrentUser DPAPI storage, bounded user-triggered cookie-file matching, configurable output, safe naming/collision handling, cancellation and audio verification. No browser credential extraction, telemetry, third-party download broker, media or credential data is included. Fresh local authentication is required on each new computer. Read the third-party and security notices before installing dependencies.
