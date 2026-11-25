# GitRemote.OneDrive 原型

原型使用 Git for Windows 2.55 和 .NET 11，由两个应用项目和一个共享库组成。

- GitRemote.OneDrive.Prototype.Cli 生成 git-onedrive.exe，负责登录、仓库管理和 GC 命令。
- GitRemote.OneDrive.Prototype 生成 git-remote-onedrive-prototype.exe，负责 Git Remote Helper 协议和原生 Git 服务。
- GitRemote.OneDrive.Prototype.Core 提供共享的认证、Graph 存储、Git 对象、引用发布和维护逻辑。

将两个程序发布到同一个目录，再把该目录加入 PATH。

## 登录与建仓

在 Microsoft Entra 注册支持个人 Microsoft 账号的公开客户端，启用设备码流程，配置 Microsoft Graph 的委托权限 Files.ReadWrite。将客户端 ID 放入 GITREMOTE_PROTOTYPE_CLIENT_ID，再运行 git onedrive login。Git 操作使用 Windows 加密缓存续期。git onedrive logout 清除本机账号缓存。

    $prototypeBin = Join-Path $env:LOCALAPPDATA 'GitRemote.OneDrive/Prototype'
    dotnet publish src/GitRemote.OneDrive.Prototype/GitRemote.OneDrive.Prototype.csproj -o $prototypeBin
    dotnet publish src/GitRemote.OneDrive.Prototype.Cli/GitRemote.OneDrive.Prototype.Cli.csproj -o $prototypeBin
    $env:PATH = "$prototypeBin;$env:PATH"
    git onedrive login
    git onedrive config root Dev/Git
    git onedrive init project
    git onedrive list
    git clone onedrive-prototype::Dev/Git/project.git

专用目录默认 GitRepositories。配置通过 OneDrive 同步到其他设备。列表仅列出已初始化的仓库，并读取所有页面。仓库名称可省略 .git，磁盘上仍保存为 project.git。管理命令接受短名称或完整 URL。init 和 url 会输出完整路径，便于跨设备使用。可用 init project --initial-branch develop --object-format sha256 创建采用 SHA-256 的仓库。

## Git 传输

- 默认地址支持完整历史的 Clone、Fetch 和 Push。git ls-remote、分支、标签、剪枝、镜像、强推、lease、原子推送和 dry-run 沿用 Git 的规则。
- 地址添加 ?transport=smart 后使用原生 Git 服务，支持浅克隆、深化、部分克隆、按需取回对象、远程归档、签名推送及 Push Options。部分克隆保存带参数的远端地址。Git archive 与签名推送依赖相应的原生 Git 功能。
- ?transport=stateless 提供 Git v2 无状态读取，支持引用公告、Fetch 和对象信息查询。同一地址的普通 Push 可使用直传。
- Fast Import / Export 流对任意原生 Git 对象的保真有限，原型的实际对象传输使用原生 pack。

    git clone --depth=1 'onedrive-prototype::Dev/Git/project.git?transport=smart' shallow
    git clone --filter=blob:none 'onedrive-prototype::Dev/Git/project.git?transport=smart' partial

直接复制静止状态下的 OneDrive 仓库目录可得到原生裸仓库。完整复制 HEAD、config、refs、packed-refs 和 objects，再运行 git fsck --full 和 git clone。原型管理云端写入。

## 并发和 GC

Push 上传不可变的 pack，并在校验后按 pack、索引的顺序发布。packed-refs 是唯一引用发布点，通过 ETag 做条件更新。并发更新同一个引用时检查原预期对象 ID，更新不同引用时可以重新验证并合并。Fetch 按所需对象查询已校验的索引，避免下载无关的 pack。

    git onedrive gc project
    git onedrive gc project --apply
    git onedrive gc project --apply --prune=now
    git onedrive status project
    git onedrive recover project <operation-id>

GC 默认预览。应用时调用原生 git gc，遵循默认保留期和原生 .mtimes。替代对象先通过 fsck，再在短时独占维护中复核引用和 pack 清单并发布。后续按持久化计划及文件 ID 和 ETag 逐个清理旧文件。

中断后先查看 status。recover 可在同一设备确认原进程已退出后继续。其他设备须先确认该进程已停止，再使用 --owner-stopped。恢复根据记录核对 OneDrive 实际版本，无法确认的写入保持保护。正常操作的令牌和传输 URL 不写入操作日志。

## 验收

    ./tests/Verify-OneDrivePrototype.ps1
    ./tests/Verify-OneDrivePrototype.ps1 -Live

原型的测试和测试宿主位于 tests/GitRemote.OneDrive.Prototype.IntegrationTests。默认系统测试使用真实 Git 和隔离的本机 Graph 测试服务，覆盖官方行为对照、并发、分片恢复和异常中断。真实 OneDrive 测试读取 ONEDRIVE_TEST_ACCESS_TOKEN，也可在原型测试目录中放置 Graph.local.runsettings 或通过验收脚本的 -Settings 指定已有配置。真实测试在独立临时目录验证条件操作、Push、Clone、GC 和裸仓库原文件。
