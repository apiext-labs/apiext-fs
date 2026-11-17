# Apiext.Fs

基于 ASP.NET Core 的**轻量、自托管、S3 兼容对象存储**。对象以普通文件落盘，既能接入 S3/MinIO 客户端生态，又能用文件管理器、`robocopy`、`rsync` 或网络共享直接管理数据。

- **S3 兼容 API**：AWS CLI、MinIO Client、s3cmd、rclone、boto3 等常见客户端可直接使用
- **文件即对象**：数据位于 `{StoreLocation}/{bucket}/{key}`，无专有格式、无对对象加壳
- **内置 Web 控制台**：Bucket 管理、对象浏览/上传/下载/预览、用户与权限、后台任务
- **多用户与权限**：静态单账号模式，或 DB 用户 + Bucket 级 ACL
- **Bucket 访问策略**：私有读写 / 公读私写（匿名仅可 GET/HEAD 对象，不可列举）
- **Multipart Upload**：完整支持 S3 分片上传协议
- **后台任务**：任务队列 + 历史记录，支持从远端 S3 导入对象
- **开箱即用**：默认 SQLite 存元数据，无外部数据库依赖
- **对象元数据索引**：ETag 与内容类型缓存在 SQLite，列表不再重复读取文件内容，且 List / Get 的 ETag 严格一致
- **多种交付形式**：Docker/GHCR 镜像、Windows 安装包（可注册服务）、Linux 压缩包

> **定位说明**：本项目优先"简单、够用、数据可见"，不是生产级对象存储。版本控制、生命周期、CORS、对象加密、审计日志等高级能力未实现，详见 [S3 API 支持情况](#s3-api-支持情况)。

---

## 目录

- [界面与功能](#界面与功能)
- [快速开始](#快速开始)
- [配置项](#配置项)
- [连接 S3 客户端](#连接-s3-客户端)
- [权限模型](#权限模型)
- [预签名链接](#预签名链接)
- [S3 API 支持情况](#s3-api-支持情况)
- [数据布局](#数据布局)
- [部署与发布](#部署与发布)
- [参与开发](#参与开发)
- [许可证](#许可证)

---

## 界面与功能

启动后访问 `http://<host>:<port>/_apiextfs` 进入 Web 控制台：

| 页面 | 路径 | 能力 |
| --- | --- | --- |
| 对象浏览器 | `/_apiextfs/buckets` | Bucket/对象列表（自动翻页、分页、排序、搜索）、面包屑与虚拟目录、多文件上传（真实进度、可取消、拖拽）、覆盖确认、重命名/移动、复制下载链接、图片/音视频/PDF/Markdown/代码预览、批量下载 ZIP、批量删除、详情抽屉、从 S3 导入 |
| Bucket 设置 | 对象浏览器内 | 访问策略切换（Private / Public read） |
| 用户管理 | `/_apiextfs/users` | 仅超管：新增/编辑/删除用户，配置 Bucket 级读/增/改/删权限 |
| 后台任务 | `/_apiextfs/jobs` | 仅超管：任务历史、状态与进度 |
| API 密钥 | `/_apiextfs/credentials` | 查看 S3 接入参数、生成预签名 URL |
| 个人资料 | `/_apiextfs/profile` | 修改密码 |
| 登录 / 初始化 | `/_apiextfs/login`、`/_apiextfs/setup` | 未配置静态账号时首次创建超级管理员 |

界面基于 Ant Design Blazor + ProLayout，主色为钢青灰蓝 `#4a6b8a`，侧边栏深色、顶栏固定，支持窄屏自适应。

---

## 快速开始

### Docker

```bash
docker run -d \
  --name apiext-fs \
  --restart unless-stopped \
  -p 9000:8080 \
  -v apiext-fs-data:/datastore \
  -v apiext-fs-app:/appdata \
  -e UseStaticAccount=true \
  -e StaticUser=iouser \
  -e StaticPassword=change-me \
  ghcr.io/<owner>/<repo>:latest
```

### Docker Compose（推荐）

仓库自带 `docker-compose.yml`，使用两个独立命名卷：

```bash
docker compose up -d --build
```

```text
/datastore/          # apiext-fs-data：只放 Bucket
└── <bucket-name>/
/appdata/            # apiext-fs-app：只放 SQLite
└── apiext-fs.db
```

端口与账号可用环境变量覆盖：

```bash
APIEXT_FS_PORT=9000 \
APIEXT_FS_USER=iouser \
APIEXT_FS_PASSWORD=change-me \
docker compose up -d --build
```

改用宿主机目录时把卷换成 bind mount，并保证镜像内用户（UID `1654`）可写：

```yaml
volumes:
  - ./data:/datastore
  - ./app:/appdata
```

```bash
mkdir -p data app && sudo chown -R 1654:1654 data app
```

### Windows 安装版 / Linux 包

从 GitHub Release 下载对应产物：

| 平台 | 文件 |
| --- | --- |
| Windows 安装版 | `Apiext.Fs-win-x64-<version>-Setup.exe`（NSIS，可注册 Windows 服务） |
| Windows 绿色版 | `Apiext.Fs-win-x64-<version>.zip` |
| Linux x64 / arm64 | `Apiext.Fs-linux-<arch>-<version>.tar.gz` |

Windows 安装版默认装到 `Program Files\Apiext.Fs`，可选注册服务（服务名 `ApiextFs`，开机自启、失败自动重启），卸载时保留 `App_Data`：

```bat
sc start ApiextFs
sc stop ApiextFs
sc query ApiextFs
```

Linux 包解压后 `./run.sh` 前台运行，包内附 `apiext-fs.service` 示例可安装为 systemd 服务。

### 启动后访问

- Web 控制台：`http://localhost:9000/_apiextfs`
- S3 Endpoint：`http://localhost:9000`

---

## 配置项

配置可写在 `appsettings.json`，也可用环境变量（`__` 表示层级）覆盖。

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `UseStaticAccount` | `true` | 启用静态单账号 |
| `StaticUser` | 空 | 固定账号用户名，同时作为 S3 Access Key ID |
| `StaticPassword` | 空 | 固定账号密码，同时作为 S3 Secret Access Key |
| `StoreLocation` | `App_Data`（容器 `/datastore`） | Bucket 与对象的存储根目录 |
| `ConnectionStrings__DefaultConnection` | `Data Source={StoreLocation}/apiext-fs.db` | SQLite 连接串，支持 `{StoreLocation}` 占位符 |
| `S3__BaseHost` | 空 | 可选，启用 virtual-hosted-style（`bucket.s3.example.com/key`） |
| `ProSettings__NavTheme` | `dark` | 控制台侧边栏主题 |
| `ProSettings__FixedHeader` | `true` | 固定顶栏 |
| `BackgroundJobs__MaxParallelPerType` | 见文件 | 各类型后台任务并发度 |

静态账号默认是超级管理员；静态账号模式下不写数据库，创建的用户也不会参与登录。

---

## 连接 S3 客户端

凭据规则：

- `Access Key ID` = 用户名
- `Secret Access Key` = 该用户的密码/Secret
- 静态账号模式下即 `StaticUser` / `StaticPassword`
- 建议使用 path-style + `us-east-1` + HTTP

### AWS CLI

```bash
aws configure set aws_access_key_id iouser --profile apiext-fs
aws configure set aws_secret_access_key iopassword --profile apiext-fs
aws configure set region us-east-1 --profile apiext-fs
aws configure set s3.addressing_style path --profile apiext-fs

aws --endpoint-url http://localhost:9000 --profile apiext-fs s3 mb s3://demo
aws --endpoint-url http://localhost:9000 --profile apiext-fs s3 cp ./README.md s3://demo/README.md
aws --endpoint-url http://localhost:9000 --profile apiext-fs s3 ls s3://demo
aws --endpoint-url http://localhost:9000 --profile apiext-fs s3 cp s3://demo/README.md ./README.downloaded.md
```

### MinIO Client

```bash
mc alias set apiext http://localhost:9000 iouser iopassword
mc mb apiext/demo
mc cp README.md apiext/demo/README.md
mc ls apiext/demo
mc cat apiext/demo/README.md
```

### SDK 要点

Endpoint 指向服务地址，Region 填 `us-east-1`，Force path style 开启，HTTP 环境关闭 HTTPS 校验。

配置 `S3:BaseHost` 并完成 DNS/反代后也可用 virtual-hosted-style：`http://demo.s3.example.com/README.md` 会被改写为 `http://s3.example.com/demo/README.md`。

---

## 权限模型

三层，从粗到细：

1. **认证**：静态账号，或 DB 用户（用户名=AccessKeyId，密码=SecretKey）。
2. **Bucket ACL**：每个用户对每个 Bucket 的 `Read` / `Add` / `Update` / `Delete` 四项权限，超管不受限。
3. **Bucket 策略**：`Private`（默认）与 `PublicRead`（公读私写）。

`PublicRead` 下匿名只能 GET/HEAD 指定对象，不能列举 Bucket、不能上传删除：

```text
http://localhost:9000/demo/README.md   # 允许
http://localhost:9000/demo             # 拒绝
```

策略也可用 S3 风格 JSON 设置（当前仅支持公读对象策略）：

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "PublicReadGetObject",
      "Effect": "Allow",
      "Principal": "*",
      "Action": "s3:GetObject",
      "Resource": "arn:aws:s3:::demo/*"
    }
  ]
}
```

```bash
aws --endpoint-url http://localhost:9000 --profile apiext-fs s3api put-bucket-policy \
  --bucket demo --policy file://public-read-policy.json
aws --endpoint-url http://localhost:9000 --profile apiext-fs s3api get-bucket-policy --bucket demo
aws --endpoint-url http://localhost:9000 --profile apiext-fs s3api delete-bucket-policy --bucket demo
```

---

## 预签名链接

支持两类临时访问：

- 标准 AWS SigV4 query-string 预签名（`X-Amz-*` 参数），兼容各 SDK 的 `generate_presigned_url`
- 内置 token 预签名（`X-Apiext-Fs-Presign`），由控制台"API 密钥"页或对象浏览器的"获取链接"生成，有效期可选 1 小时 / 24 小时 / 7 天

控制台对象浏览器的"获取链接"同时提供**永久地址**：

| 类型 | 形式 | 说明 |
| --- | --- | --- |
| 永久地址（公开直连） | `http://host/bucket/key` | 仅当 Bucket 策略为「公开读取」时可匿名访问；私有 Bucket 下该地址需要携带凭据 |
| 临时地址（预签名） | `http://host/bucket/key?X-Apiext-Fs-Presign=...` | 到期自动失效，无需凭据即可下载 |

预签名 token 保存在进程内存中，**重启后失效**（永久地址不受影响）。

---

## S3 API 支持情况

| 类别 | 已支持 | 未支持 |
| --- | --- | --- |
| Bucket | ListBuckets、CreateBucket、HeadBucket、DeleteBucket、GetBucketLocation、ListObjects V1/V2（prefix/delimiter/marker/continuation-token）、Get/Put/Delete BucketPolicy | 版本控制、生命周期、CORS、Website、通知、复制、Object Lock、配额 |
| Object | Put/Get/Head/Delete、DeleteObjects 批量、CopyObject（含条件头）、Range 请求、条件请求（If-Match 等）、response-* 覆盖头 | 标签、SSE 加密、存储类型（恒为 `Standard`）、canned ACL、SelectObjectContent |
| Multipart | Create、UploadPart、UploadPartCopy、Complete、Abort、ListParts、ListMultipartUploads | — |
| 鉴权 | SigV4（含 query-string 预签名）、SigV2、内置 presign token | payload SHA256/chunk 签名严格校验、防重放 |

已知行为特点（属于设计取舍）：

- 对象自定义元数据（`x-amz-meta-*`）、标签、加密头**不持久化**；Content-Type 在读取时按扩展名推断。
- ETag 统一为对象内容的 MD5（分片上传为 S3 标准复合 ETag），Put / Get / Head / List 四处一致；元数据缓存在 `object_index` 表中，文件被外部修改或替换后会自动失效并重算。
- 分片上传的 Complete 不校验 part ETag；`.multipart` 暂存目录无自动过期清理。

---

## 数据布局

```text
{StoreLocation}/
├── apiext-fs.db          # SQLite：用户、ACL、Bucket 策略、后台任务、对象元数据索引
├── bucket-a/
│   └── path/to/object.txt
└── bucket-b/
    └── ...
```

数据库迁移在启动时自动执行，也可手动：

```cmd
dotnet run --project Apiext.Fs/Apiext.Fs.csproj -- --migrate
```

登录态使用进程内临时 Data Protection 密钥，**重启后需重新登录**（不产生密钥目录）。

---

## 部署与发布

| 方式 | 说明 |
| --- | --- |
| Docker / Compose | `docker compose up -d --build`；容器内 8080，宿主默认 9000 |
| GHCR 镜像 | 手动触发 `docker-publish.yml`：Buildx 构建 `linux/amd64,linux/arm64`，标签默认 `yyMMdd-HHmm`（UTC） |
| 二进制产物 | 手动触发 `publish-binaries.yml`：`--self-contained` + `PublishSingleFile`，可选创建 Release |
| Windows 服务 | NSIS 安装包注册 `ApiextFs` 服务，默认监听 `http://0.0.0.0:8080` |
| Linux | tar 包 + 示例 systemd unit |

生产部署建议：置于反向代理之后、开启 HTTPS、使用非默认密码、为数据卷做定期备份。默认绑定为 HTTP，`UseHttpsRedirection` 未启用。

---

## 参与开发

开发环境、目录结构、UI 规范、测试与发布流程见 [AGENTS.md](AGENTS.md)。

```powershell
dotnet build Apiext.Fs.slnx
dotnet run --project Apiext.Fs/Apiext.Fs.csproj   # http://localhost:5052
dotnet test Apiext.Fs.slnx
```

开发环境默认账号：`iouser` / `iopassword`。

---

## 许可证

[GNU Affero General Public License v3.0 (AGPL-3.0)](LICENSE.txt)。

你可以自由使用、修改和分发（含商业用途）；若修改后作为网络服务对外提供，需要按 AGPL-3.0 公开对应修改源码。
