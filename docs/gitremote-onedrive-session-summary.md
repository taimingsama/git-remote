# GitRemote.OneDrive 会话总结

本次会话确定了用微软 OneDrive 存放原生 Git 裸仓库的战略设计，并建立了 .NET 项目骨架和 Microsoft Graph 验证项目。当前可运行的是 Graph 集成测试。Git remote helper 的实现仍待开始。

## 已确定的设计

- OneDrive 保存标准裸仓库布局。首版使用 SHA-1、Git 的 `files` 引用后端和按对象 ID 存储的松散对象。完整下载后，应能用原生 Git 克隆和检查。
- 核心领域是远端仓库发布。Git 负责对象、引用和快进规则，Graph 适配器负责存储及 ETag。远端仓库发布负责确保引用只在所需对象已经可读时更新。
- 参考 `git-remote-dropbox` 按需传输 Git 对象。先上传对象，再以预期 ETag 条件发布原生 `packed-refs`。引用集中在这个文件里，一次条件写入更新整组引用，因此不同分支的并发推送也可能需要重新验证。 [战略设计](gitremote-onedrive-strategic-design.md) · [git-remote-dropbox 设计](https://raw.githubusercontent.com/anishathalye/git-remote-dropbox/master/DESIGN.md)

## 仓库交付物

- `GitRemote.slnx` 注册了 .NET 11 控制台项目和 xUnit v3 集成测试项目。 [控制台项目](../src/GitRemote.OneDrive/GitRemote.OneDrive.csproj) · [集成测试项目](../tests/GitRemote.OneDrive.IntegrationTests/GitRemote.OneDrive.IntegrationTests.csproj)
- `GraphTestFile` 使用 Microsoft Graph .NET SDK 管理临时文件。按路径上传和下载时保留 SDK 的专用请求构建器。这个类型的 `Content` 会保留文件路径，解决了先前将构建器声明成基类后上传请求指向文件夹的问题。 [Graph 测试夹具](../tests/GitRemote.OneDrive.IntegrationTests/Graph/GraphTestFile.cs)
- 测试通过 `ONEDRIVE_TEST_ACCESS_TOKEN` 显式启用。本地 `Graph.local.runsettings` 由 Git 忽略，仓库只保留模板。Rider 中已确认可用的方式是在 **Unit Testing → Test Runner → Test Settings** 选择本地文件。项目的 `RunSettingsFilePath` 经 MSBuild 验证能解析到该文件，Rider 在未显式选择时的行为仍待确认。 [本地配置模板](../tests/GitRemote.OneDrive.IntegrationTests/Graph.local.runsettings.example)

## Graph 验证用例

| 用例 | 验证行为 |
| --- | --- |
| `ConditionalWrite_WithSameETag_AllowsOnlyOneWriter` | 两个客户端使用同一 ETag 替换文件，恰好一个成功，另一个返回 `412`，远端内容等于成功方提交的值。 |
| `CreateIfAbsent_WithSameFileName_AllowsOnlyOneCreator` | 两个客户端使用 `conflictBehavior=fail` 竞争创建同名文件，恰好一个成功，另一个返回 `409`，远端保留成功方内容。 |
| `ReadContent_WhenFileChangesAfterReadingETag_ExposesVersionChange` | 在首次读取 ETag 和下载之间替换文件。下载得到新内容，前后 ETag 不同，可以据此识别这次版本变化。 |
| `DownloadByPath_AfterUpload_ReturnsUploadedBytesToAnotherClient` | 上传二进制内容后，另一客户端立即按路径下载，逐字节验证内容。 |

四个用例已通过本地模拟 Graph 传输。解决方案也成功发现这四个用例，无令牌时全部按预期跳过。原生裸仓库格式另用本地样例通过了 `git fsck --full` 和原生克隆。 [条件写入测试](../tests/GitRemote.OneDrive.IntegrationTests/Graph/ConditionalWriteTests.cs) · [存储验证测试](../tests/GitRemote.OneDrive.IntegrationTests/Graph/OneDriveStorageTests.cs)

## 待验证事项

- 运行四个真实 OneDrive 集成测试，记录目标账号的状态码和回读结果。当前的条件写入用例探测小文件 `PUT /content` 的 `If-Match` 行为。该端点的文档没有列出此请求头，真实服务结果决定它能否用作 `packed-refs` 的发布点。 [Microsoft Graph 小文件上传文档](https://learn.microsoft.com/en-us/graph/api/driveitem-put-content?view=graph-rest-1.0)
- 处理测试依赖的 NuGet 审计警告。当前 SDK 的间接依赖 `Microsoft.Kiota.Abstractions` 1.21.1 触发 `NU1903`。 [安全公告](https://github.com/advisories/GHSA-7j59-v9qr-6fq9)
