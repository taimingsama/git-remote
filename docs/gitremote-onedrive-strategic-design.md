# GitRemote.OneDrive 战略设计

当前原型的命令、原生 pack 布局和维护流程见 [GitRemote.OneDrive 原型](gitremote-onedrive-prototype.md)。本文记录初始战略。

GitRemote.OneDrive 让微软 OneDrive 成为 Git 的远端存储。用户安装 `git-remote-onedrive` 后，可以通过 `onedrive::…` 远端地址使用原生 `git clone`、`git fetch` 和 `git push`。OneDrive 中保存一个标准裸仓库，完整下载后可用 Git 直接克隆和检查。 [Git remote helper 协议](https://git-scm.com/docs/gitremote-helpers) · [Git 仓库布局](https://git-scm.com/docs/gitrepository-layout)

## 限界上下文与上下文映射

| 边界 | 定位 | 职责 |
| --- | --- | --- |
| 远端仓库发布 | 核心限界上下文 | 维护已发布的引用集合，决定推送能否基于预期版本生效。 |
| Git 传输 | 入站适配器 | 实现 remote helper 的 `list`、`fetch` 和 `push`，将发布结果返回给 Git。 |
| 原生 Git | 外部工具 | 枚举和验证对象，判断快进关系，生成原生引用文件。 |
| OneDrive | 外部存储 | 通过 Microsoft Graph 读写文件。防腐层将文件标识、ETag 和条件写入结果映射成仓库语言。 |
| 微软账号授权 | 通用能力 | 使用 Microsoft OAuth 和 OneDrive 权限访问所选仓库。 |

Git CLI 通过入站适配器调用核心用例。核心用例以 Git 对象 ID、引用名和预期版本表达规则。OneDrive 防腐层负责 Graph 请求和错误翻译。

## 通用语言

| 术语 | 含义 |
| --- | --- |
| 远端引用（Remote Ref） | 远端公布的分支或标签及其目标 Git 对象 ID。 |
| 已发布引用集合（Published Refs） | 远端已生效的分支和标签，由原生 `packed-refs` 表达。 |
| 预期引用版本（Expected Ref Version） | 本次推送验证所依据的引用集合版本，由 OneDrive 适配器映射到 ETag。 |
| 推送冲突（Push Conflict） | 预期版本在发布前已经变化，推送须读取新版本并重新验证。 |

## 云端仓库布局

```text
repo.git/
├── HEAD
├── config
├── refs/
├── packed-refs
└── objects/
    └── <对象 ID 的前两位>/<其余位>
```

首版使用 SHA-1 和 Git 的 `files` 引用后端。初始化时由 Git 建立裸仓库并写入指向默认分支的 `HEAD`。云端所有写入由 Helper 负责。分支和标签集中存放在 `packed-refs`，由本地 Git 执行 `pack-refs --all` 生成。Git 优先读取松散引用，因此云端维护该约定才能让单个文件成为完整的引用发布点。分支和标签名称保留在文件内容中。 [Git `pack-refs`](https://git-scm.com/docs/git-pack-refs) · [OneDrive 路径规则](https://learn.microsoft.com/en-us/graph/onedrive-addressing-driveitems)

## 推送与拉取

1. **读取并验证。** Helper 读取已发布引用及对应的预期版本。普通分支推送由 Git 验证是否为快进。
2. **上传缺少的对象。** 复用 `git-remote-dropbox` 的增量思路，根据对象可达性确定上传集合，按 Git 对象 ID 存放松散对象。正常推送按内容追加对象。
3. **发布引用。** 确认新引用所需的对象可读后，按预期版本条件替换 `packed-refs`。发布成功才向 Git 报告成功。冲突时重新读取引用，任何后续尝试都以重新验证为前提。
4. **按需拉取。** 从已公布引用的目标对象出发，校验下载对象的 ID，补齐可达历史。只有确认本地已有完整可达历史时，才停止遍历该路径。

发布引用前中断时，旧引用仍指向完整历史。引用集合共享一个版本，因此不同分支的并发推送也可能需要重新验证。松散对象会增加 HTTP 请求量，原生 pack 可在后续用于优化传输。 [git-remote-dropbox 设计](https://raw.githubusercontent.com/anishathalye/git-remote-dropbox/master/DESIGN.md) · [Dropbox 引用发布实现](https://raw.githubusercontent.com/anishathalye/git-remote-dropbox/master/src/git_remote_dropbox/helper.py)

## 首版范围与验证

首版按单个个人微软账号、多台设备使用同一远端设计。交付范围包括初始化远端、首次推送、克隆、拉取、创建分支和快进推送。

首个验证针对 Graph 的条件发布。两个独立客户端持有同一个 ETag，各自完成对象准备，再竞争最终写入同一个 `packed-refs`。一个发布成功，另一个因版本已变而失败。随后读取云端引用，确认它指向成功方的提交。Graph 文档提供上传会话和特定显式提交场景的 `If-Match` 用法，最终写入的原子性需要用目标 OneDrive 账号实测。仓库格式另以完整下载后的 `git fsck --full` 和原生克隆作为验收条件。 [Microsoft Graph 上传会话文档](https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession?view=graph-rest-1.0)
