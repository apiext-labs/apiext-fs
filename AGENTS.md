# AGENTS.md — Apiext.Fs 开发者指南

面向在本仓库内开发的工程师与编码助手。项目是什么、怎么用，见 [README.md](README.md)。

---

## 1. 设计边界（先读这一节）

Apiext.Fs 的定位是 **轻量、自托管、S3 兼容的"文件即对象"存储**，目标是"模拟 S3 协议、数据可被普通文件工具直接管理"，**不是生产级对象存储**（不对标 MinIO/Ceph）。

因此以下能力是**刻意的取舍，不要主动"优化"**，除非需求明确要求：

- 对象自定义元数据不落库：`x-amz-meta-*`/tagging/SSE 头接收后忽略，无 sidecar 文件；Content-Type 按扩展名推测后缓存在索引表。
- ListObjects 仍需遍历目录以发现文件（文件系统是唯一真相），但对象元数据走 SQLite 索引，不再读取文件内容计算哈希。
- ETag 统一为对象内容 MD5（分片上传为 S3 复合 ETag），Put / Get / Head / List 一致。
- 签名校验只做 seed 签名与时间窗，不校验 payload SHA256 / chunk 签名。
- 密码明文存储在 SQLite，且用户密码同时充当 S3 SecretKey（username=AccessKeyId）。
- 无版本控制、生命周期、CORS、通知、Object Lock、存储统计、审计日志、健康检查/指标。
- 默认 HTTP，不强制 HTTPS。

新增功能时优先保持"文件系统 + SQLite 少量表"的简单模型。

---

## 2. 环境与命令

要求 .NET SDK 10（本机 `10.0.101` 已验证可用；仓库不再提供 `global.json` 版本锁定）。

```powershell
# 构建
dotnet build Apiext.Fs.slnx

# 运行（开发环境，http://localhost:5052，账号 iouser / iopassword）
dotnet run --project Apiext.Fs/Apiext.Fs.csproj

# 测试（xunit.v3 + Microsoft.NET.Test.Sdk）
dotnet test Apiext.Fs.slnx
# 或直接跑测试宿主
Apiext.Fs.Tests\bin\Debug\net10.0\Apiext.Fs.Tests.exe

# 数据库迁移（启动时也会自动执行）
dotnet run --project Apiext.Fs/Apiext.Fs.csproj -- --migrate
dotnet ef migrations add <Name> --project Apiext.Fs
```

端口：开发 `5052`（`Properties/launchSettings.json`），生产/容器默认 `8080`（Windows 服务绑定 `http://0.0.0.0:8080`）。

---

## 3. 目录结构与职责

| 路径 | 职责 |
| --- | --- |
| `Apiext.Fs/Program.cs` | 组合根：DI、认证、中间件顺序、端点映射、启动迁移 |
| `Apiext.Fs/Controllers/` | S3 HTTP API（`BucketController`、`ObjectController`、`S3ControllerBase`） |
| `Apiext.Fs/Endpoints/` | 非 S3 端点：登录/登出/初始化、凭据与预签名 JSON API、ZIP 打包下载 |
| `Apiext.Fs/Middleware/` | `S3AuthMiddleware`（SigV4/SigV2/预签名）、`VirtualHostedMiddleware`、`S3RequestIdMiddleware` |
| `Apiext.Fs/Services/` | 文件系统实现（Bucket/Object/Multipart）、对象元数据索引（`ObjectIndexService`）、ETag（`ObjectETag`）、鉴权、Bucket 策略、预签名存储、后台任务 |
| `Apiext.Fs/Data/` | `AppDbContext` + EF Core Migrations |
| `Apiext.Fs/Models/` | `Entities/`（表实体）与 `Api/`（请求/响应/XML 模型） |
| `Apiext.Fs/Components/` | Blazor Server 管理后台：`Pages/`、`Layout/`、`App.razor` |
| `Apiext.Fs/wwwroot/_assets/` | 全部静态资源唯一目录：`theme.css`（主题变量）、`logo.svg`、`apiextfs-antd.css`（覆盖样式）、prism/marked |
| `Apiext.Fs.Tests/` | xunit 测试（服务层、中间件、XML 序列化、后台任务） |
| `installer/windows/` | NSIS 安装脚本 + Windows 服务配置 |
| `.github/workflows/` | `docker-publish.yml`（GHCR 镜像）、`publish-binaries.yml`（四平台产物） |

### 请求管线顺序（勿随意调整）

```
VirtualHostedMiddleware → S3RequestIdMiddleware → S3AuthMiddleware
→ UseAuthentication → UseAuthorization → UseAntiforgery
→ MapStaticAssets → MapRazorComponents → MapAuthEndpoints
→ MapS3ManagementEndpoints → MapDownloadZipEndpoint → MapControllers
```

`MapControllers` 必须最后，保证 Blazor 路由优先。

---

## 4. 数据与存储

- 对象文件：`{StoreLocation}/{bucket}/{key}`，上传先写 `{path}.tmp~` 再 `File.Move`。
- 分片上传暂存：bucket 下 `.multipart/`（无过期清理任务，注意别在测试里泄漏）。
- SQLite 五张表：`users`、`user_acls`、`bucket_policies`、`background_job_runs`、`object_index`。
- 默认路径：开发 `Apiext.Fs/App_Data/`（运行期生成，不要提交）；容器 `/datastore` + `/appdata/apiext-fs.db`。

### 对象元数据索引（`object_index`）

`IObjectIndexService` / `ObjectIndexService` 缓存 `(bucket, key)` 的 `size`、`last_write_utc_ticks`、`ETag`、`ContentType`，用来：

- 让 List / Get / Head 返回同一个 ETag（文件内容 MD5；分片上传为 `"{md5}-{parts}"` 复合值）；
- 避免每次请求重读文件算哈希（实测 200×64KB：首次 List ~226ms，索引命中后 ~9ms）。

约定：

- **文件系统仍是唯一真相**。读取时只有 `size` 与 `last_write_utc_ticks` 都匹配才信任缓存，否则重算并回写；因此外部直接改文件会自动失效，无需手工重建。
- 写路径（Put / Copy / CompleteMultipart / Delete / DeleteObjects / Abort / DeleteBucket）负责维护索引。
- 索引只是缓存：数据库异常一律记录后降级（重算哈希），不能让 S3 请求失败。
- `ObjectIndexService` 是单例，通过 `IServiceScopeFactory` 取 `IDbContextFactory<AppDbContext>`（项目的 factory 注册为 Scoped，单例不能直接注入）。

---

## 5. Web UI 规范（必须遵守）

技术栈：**Ant Design Blazor 1.6.2 + AntDesign.ProLayout**。不要引入其他 UI 框架或手写组件库替代内置组件。

### 5.1 视觉基调

简约、素雅、克制留白。主色钢青灰蓝 `#4a6b8a`（hover `#5d7f9e` / active `#3a5570`）；背景 `#f5f6f8`；文字主 `#2d3748` / 次 `#718096` / 弱 `#9aa5b1`；侧边栏 ProLayout dark + 固定顶栏；卡片白底、圆角 8px、柔和阴影；Logo 为 `wwwroot/_assets/logo.svg`。

### 5.2 主题机制

- 样式引入顺序（`Components/App.razor`）：`ant-design-blazor.variable.css` → ProLayout css → **`/_assets/theme.css`** → 作用域样式 → `_assets/apiextfs-antd.css`。
- **主题只改 `wwwroot/_assets/theme.css` 里的 `--ant-primary-*` 变量**（含 `--ant-primary-1..10` 与 deprecated 派生变量）。不要直接改 AntDesign 预设色，不要为组件写死主色。
- 应用自有 token 在 `wwwroot/_assets/apiextfs-antd.css` 的 `:root`（`--bg` / `--surface*` / `--text*` / `--accent*` / `--radius`），业务样式一律引用变量。
- 布局配置在 `appsettings.json` 的 `ProSettings`（`NavTheme: dark`、`FixedHeader: true`、`FixSiderbar: true`），经 `Program.cs` 注入 `BasicLayout`。
- **不要启用 `<SettingDrawer />`**：它会加载 `_content/AntDesign.ProLayout/theme/*.css` 预设色板（daybreak/dust/cyan 等），覆盖钢青灰蓝主题并使设置面板与实际主色不一致。主题固定，不做运行时换色。

### 5.3 组件选择

优先内置组件，不自造轮子：

- 数据展示：`Table` / `Descriptions` / `Statistic` / `Card` / `Tag` / `Badge` / `Timeline`
- 录入：`Form` / `Input` / `InputPassword` / `InputNumber` / `Select` / `Switch` / `DatePicker` / `Upload`
- 反馈：`Message` / `Notification` / `Modal` / `Popconfirm` / `Drawer` / `Spin` / `Empty` / `Result` / `Alert`
- 布局：`Row` / `Col` / `Space` / `Divider` / `Tabs` / `Menu`
- 图标：内置 `Icon`

全局反馈依赖 `Components/Routes.razor` 中的 `<AntContainer />`，删除它会导致 `Message`/`Modal` 等失效。

### 5.4 静态资源的重要坑

`S3AuthMiddleware.ShouldSkipAuth` 决定哪些路径绕过 S3 鉴权，否则请求会被当作 S3 API 请求返回 `AccessDenied`。

**规则：所有自定义静态资源一律放到 `wwwroot/_assets/` 下，通过 `/_assets/...` 引用。** 该前缀已在白名单中，新增文件无需改中间件。不要新建 `wwwroot/css/`、`wwwroot/images/` 等根级目录，也不要为此扩白名单。现有白名单：`/_apiextfs`、`/_assets`、`/_framework`、`/_content`、`/Components/`、`/not-found`、`/Apiext.Fs.*`、`/app.*`、`/_blazor/`、`/favicon.ico`。

### 5.5 文案

界面文案统一简体中文（`<html lang="zh-CN">`），避免中英混排残留。

### 5.6 对象浏览器（`Buckets.razor`）交互约定

- **列表加载**：沿 `ContinuationToken` 循环拉取全部页（上限 `MaxListPages = 50` 即 5 万对象），超出时工具栏提示"仅显示前 N 项"；不要只取第一页（否则 >1000 对象的目录会被静默截断）。
- **禁止 N+1**：行数据全部来自 `ListObjectsV2` 响应，Content-Type 用 `MimeTypes.GetMimeType(key)` 推断，**不要为每行调用 HeadObject/GetObject**。
- **分页与排序**：`Table` 客户端分页（`PageSize = 50`，单页时 `HidePagination`），排序在 `_visibleItems` 计算属性里做（文件夹优先）。
- **批量删除**：走 `DeleteObjectsAsync` 并按 1000 个一批；删除虚拟文件夹先用 prefix 列出全部 key 再批量删。
- **上传**：用 `ProgressStream` 包装 `IBrowserFile.OpenReadStream` 得到真实进度，`CancellationTokenSource` 支持取消；服务端 `PutObjectAsync` 已在异常时清理 `.tmp~`。
- **反馈**：新建/删除/重命名/上传/策略变更等写操作成功后必须 `Message.Success(...)`，失败给出可读错误。
- **重命名/移动**：S3 无 rename，使用 `CopyObjectAsync` + `DeleteObjectAsync`；输入纯名称视为当前目录改名，输入含 `/` 视为桶内移动。
- **下载链接**：统一走"获取链接"弹窗（`OpenLinkModal`）。永久地址是 `BuildObjectUrl` 的绝对形式（不带 token，只有 Bucket 为 PublicRead 时匿名可访问）；临时地址用 `Presigned.Create(...)`（1h/24h/7d，服务端上限 604800 秒）。不要把 presign token 拼进永久地址，也不要硬编码有效期文案——切换有效期后 `RebuildTempLink()` 会重新签发 token。

---

## 6. 常见任务

### 新增一个管理页面

1. `Components/Pages/` 新建 `.razor`，`@page "/_apiextfs/xxx"`。
2. 需要登录的页面沿用现有 `AppLayout`（`Components/Layout/AppLayout.razor`），并在 `BuildMenuAsync` 中加 `MenuDataItem`（按 `_isSuperAdmin` 控制可见性）。
3. 需要新 API 时在 `Endpoints/` 增加最小 API 或放 `Controllers/`（注意别与 S3 路由冲突）。

### 新增一个 S3 API 操作

1. 在 `Controllers/ObjectController.cs` 或 `BucketController.cs` 增加 action，路由保持 `/{bucket}/{**key}` 形态。
2. 服务接口在 `Services/I*.cs`，实现放对应 `FileSystem*.cs`；XML 输出走 `S3XmlSerializer`。
3. 鉴权统一交给 `S3AuthMiddleware`，不要在 Controller 里重复解析签名。
4. 补测试：`Apiext.Fs.Tests/FileSystem*Tests.cs`。

### 新增后台任务

1. 实现 `IBackgroundJob`（参考 `Services/BackgroundJobs/DownloadJob.cs`）。
2. `Program.cs` 注册 `AddScoped<IBackgroundJob, XxxJob>()`，并在 `appsettings.json` 的 `BackgroundJobs:MaxParallelPerType` 配置并发。
3. 进度/结果自动落 `background_job_runs`，可在 `/_apiextfs/jobs` 查看。

---

## 7. 测试

- xunit.v3，测试文件按服务划分（鉴权、Bucket/Object/Multipart、策略、预签名、chunked 解码、后台任务、XML、对象索引一致性）。
- `Apiext.Fs.Tests.csproj` 里开启了 `TestingPlatformDotnetTestSupport`，因此 `dotnet test` 可用（原 `global.json` 的 runner 配置已移除）。
- 服务层测试直接构造临时目录；如需 DB 用 `Microsoft.EntityFrameworkCore.InMemory`，索引用 `TestFactory.CreateIndex(name)`（每个测试类用唯一库名避免串数据）。
- 改动后至少跑 `dotnet test Apiext.Fs.slnx` 并通过；UI 改动用 `dotnet build` + 手工访问 `/_apiextfs` 验证。
- 注意 `bin/obj` 下的测试宿主可能残留旧版本，建议 `dotnet test` 而不是直接跑 exe。

---

## 8. 发布

| 场景 | 做法 |
| --- | --- |
| 本地 Docker | `docker compose up -d --build`，宿主端口 `${APIEXT_FS_PORT:-9000}` → 容器 8080 |
| GHCR 镜像 | 手动触发 `docker-publish.yml`（Buildx，amd64/arm64，标签默认 `yyMMdd-HHmm` UTC） |
| 四平台产物 | 手动触发 `publish-binaries.yml`（single-file self-contained + NSIS 安装包） |
| Windows 服务 | `installer/windows/apiext-fs.nsi`，服务名 `ApiextFs`，默认 `Program Files\Apiext.Fs`，卸载保留 `App_Data` |
| Linux | `publish/linux-*` + 包内示例 `apiext-fs.service` |

容器基础镜像为 `aspnet:10.0-noble-chiseled-extra`，应用用户 `$APP_UID`；bind mount 时注意宿主机目录属主。

---

## 9. 常见坑

1. **静态资源 403/AccessDenied**：文件放到了 `_assets` 之外的根级路径（见 5.4），请移入 `wwwroot/_assets/`。
2. **重启后登录态失效**：DataProtection 使用 `UseEphemeralDataProtectionProvider()`，这是刻意设计（不落密钥文件）。
3. **Windows 服务读不到配置**：服务工作目录是 System32，`Program.cs` 已把 ContentRoot 固定为 exe 目录；自建目录结构时注意。
4. **并发 Put 同一 key**：临时文件名固定 `{path}.tmp~`，并发写会冲突（已知限制）。
5. **`dotnet run --no-build` 不生效**：先 `dotnet build`，否则跑的是旧二进制。
6. **UI 主色没变**：确认浏览器缓存，并检查 `ant-design-blazor.variable.css` 是否被换回静态版 `ant-design-blazor.css`（静态版不认 CSS 变量）。
7. **上传弹窗进度条**：当前为完成时直接置 100%，不是真实进度。
8. **改了 ETag 算法要同步四处**：`ObjectETag`、`IObjectIndexService` 的缓存字段、multipart 复合 ETag、以及 `FileSystemBucketService` 的缓存命中判断。缓存失配条件（size + last-write ticks）改动会让已缓存条目全部失效。
9. **直接改文件系统不需要重建索引**：size/mtime 变化会让缓存自动失配并重算；但外部删除的文件会留下孤儿索引行（不影响正确性，可在 DeleteBucket 时一并清理）。
