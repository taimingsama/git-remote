# GitRemote.OneDrive 原型

`GitRemote.OneDrive.Prototype` 生成 `git-remote-onedrive-prototype`。把生成目录放进 `PATH` 后，Git 会为 `onedrive-prototype::<name>.git` 地址启动这个 helper。原型使用个人 OneDrive 根目录中的独立仓库目录，并支持 SHA-1 Git 仓库。

```powershell
git-remote-onedrive-prototype --init onedrive-prototype::example.git
git remote add onedrive onedrive-prototype::example.git
git push onedrive main
git fetch onedrive
```

`HEAD` 默认指向 `refs/heads/main`。Git 负责更新本地的远程跟踪分支。Helper 将对象导入本地对象数据库。

## 模块

- `RemoteHelper` 处理 `capabilities`、`list`、`fetch`、`push` 和批量协议响应。它保留向 Git 宣告的推送引用快照，诊断写到 stderr。
- `PushRequest` 按命令顺序保存原始来源、目标和强制标志。空来源表达删除意图。`PushService` 结合本地提交和已宣告的远端快照，把需要改变引用的意图转换成 `BranchCreate`、`BranchUpdate` 或 `BranchDelete`。无变化的请求直接记录成功。
- `PushOperation` 收集一批 `BranchOperation`，并拒绝同批次的重复目标。每种分支操作负责自己的验证和执行，强制标志只影响 `BranchUpdate` 的快进检查。
- `PushService` 逐分支验证，记录无变化和验证失败的分支。`LocalBranch` 检查快进和本地提交完整性。`LocalRepository` 为待推送分支生成一个共享包，只排除双方已具备的完整历史。对象可见后，`RemoteRepository` 条件创建新分支，`RemoteBranch` 条件更新已有分支。随后单独执行删除，并逐分支返回结果。
- `FetchService` 使用协议给出的目标 Object ID。`LocalRepository` 调用 Git 逐轮发现缺失的可达对象。`RemoteRepository` 从当前清单的原生 `.idx` 找到候选 pack，每导入一个完整 pack 后重新检查本地缺失对象。Fetch 验证目标完整性，并在遇到清单切换时重新加载索引。
- `Packfile` 管理包的身份、路径、校验和索引读取。`PackManifest` 管理有效包集合，检查重复与冲突。`RemoteRepository` 负责读写清单，并在 Push 遭遇 CAS 冲突时读取最新清单再合并。
- `GitClient` 封装 Git 原生命令，`OneDriveFileSystem` 封装 Graph 文件操作、分片上传和条件写入。

## OneDrive 存储与并发

```text
repository/
├── HEAD
├── metadata/
│   ├── current-manifest
│   └── current-refs
├── manifests/
│   └── manifest-<version>.json
├── refs/heads/
│   └── <version>.ref
└── objects/pack/
    ├── pack-<sha1>.pack
    └── pack-<sha1>.idx
```

清单固定一组不可变的 pack 和索引，记录两个文件各自的 SHA-256。Push 回读校验共享包、按 ETag 发布清单之后，才逐个发布引用。

`current-refs` 将完整分支名映射到不可变的分支记录。每次更新先核对目标分支仍处于 `list for-push` 时的版本，并检查分支名冲突，再按当前目录的 ETag 提交这一项变更。若同时修改了其他分支，可以读取新目录后重试本次更新。同一批次允许部分分支成功。

远端清单通过追加 Packfile 推进版本，历史清单和对象文件会保留。旧版 `packed-refs` 仓库支持只读克隆和 Fetch。

## 验收

在 Windows 上用 .NET 11 SDK 构建，并运行验收脚本。

```powershell
dotnet build GitRemote.slnx
$testHost = 'tests/GitRemote.OneDrive.Prototype.TestHost/bin/Debug/net11.0/GitRemote.OneDrive.Prototype.TestHost.exe'
./tests/Verify-OneDrivePrototype.ps1 -HelperPath $testHost
$helper = 'src/GitRemote.OneDrive.Prototype/bin/Debug/net11.0/git-remote-onedrive-prototype.exe'
./tests/Verify-OneDrivePrototype.ps1 -HelperPath $helper -Live
```

默认模式使用真正的 Git 命令和模拟 Graph 传输，覆盖首次及增量 Push/Fetch、按需下载、并发引用冲突、强制推送、删除、远端清单切换、损坏的 pack 或索引、缺失对象、上传中断和大文件分片上传。脚本还用原生 Git 对复原的裸仓库执行 `fsck` 和克隆。

验收还模拟清单条件写入冲突，验证 Push 重新读取并合并新增包，以及 Fetch 能在清单切换后找到新发布的候选包。

`-Live` 使用 `GITREMOTE_PROTOTYPE_ACCESS_TOKEN` 或集成测试本地配置中的 `ONEDRIVE_TEST_ACCESS_TOKEN`，在 OneDrive 中创建并清理本次验收专用的临时仓库。
