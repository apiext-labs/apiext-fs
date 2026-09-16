# Apiext.Fs

**Apiext.Fs** 是一个基于 ASP.NET Core 的轻量级、自托管、S3 兼容对象存储服务。它把对象直接保存为普通文件，适合想要自己掌控文件存储、又希望继续使用 S3/MinIO 客户端生态的场景。

## 功能特性

- **S3 兼容 API**：支持常见 S3 客户端、SDK 和工具，例如 AWS CLI、MinIO Client、s3cmd、rclone、boto3 等。
- **普通文件系统存储**：对象会直接落在 `{StoreLocation}/{bucket}/{key}`，没有专有对象格式，方便用文件管理器、`robocopy`、`rsync` 或网络共享直接访问。
- **Web 后台管理**：内置 Blazor 管理界面，可管理 Bucket、浏览对象、上传下载文件、管理用户和权限。
- **用户与权限控制**：支持多用户、Bucket 级 ACL，以及静态单账号模式。
- **Bucket 访问策略**：支持私有读写和公读私写，公读只允许匿名查看/下载对象，不允许匿名列出对象。
- **Multipart Upload**：支持 S3 分片上传协议，适合大文件上传。
- **后台任务**：支持后台任务队列和任务历史，包括从远端 S3 导入对象。
- **SQLite 默认存储元数据**：开箱即用，不依赖外部数据库。
- **Docker / GHCR 镜像发布**：可通过 GitHub Actions 构建并推送到 GitHub Container Registry。

## 快速启动

### Docker 运行

默认容器 HTTP 端口是 `8080`，下面示例把它映射到宿主机 `9000`，方便用 S3/MinIO 客户端连接。

```bash
docker run -d \
  --name apiext-fs \
  --restart unless-stopped \
  -p 9000:8080 \
  -v apiext-fs-data:/datastore \
  ghcr.io/<owner>/<repo>:latest
```

如果使用静态账号模式，可以直接通过环境变量指定 S3 Access Key 和 Secret Key：

```bash
docker run -d \
  --name apiext-fs \
  --restart unless-stopped \
  -p 9000:8080 \
  -v apiext-fs-data:/datastore \
  -e UseStaticAccount=true \
  -e StaticUser=iouser \
  -e StaticPassword=iopassword \
  ghcr.io/<owner>/<repo>:latest
```

启动后访问：

- Web 后台：`http://localhost:9000/_apiextfs`
- S3 Endpoint：`http://localhost:9000`

也可以直接使用仓库中的 Compose 配置：

```bash
docker compose up -d --build
```

Compose 默认使用 `apiext-fs-data` 命名卷，并把对象文件、SQLite 数据库和
Data Protection 密钥统一持久化到容器内的 `/datastore`。端口和静态账号可通过环境变量覆盖：

```bash
APIEXT_FS_PORT=9000 \
APIEXT_FS_USER=iouser \
APIEXT_FS_PASSWORD=change-me \
docker compose up -d --build
```

如果需要把数据直接保存到仓库旁的宿主机目录，可将 `docker-compose.yml` 中的卷改为：

```yaml
volumes:
  - ./data:/datastore
```

Linux 上使用 bind mount 前，需要确保目录可由镜像内的应用用户（默认 UID `1654`）写入：

```bash
mkdir -p data
sudo chown -R 1654:1654 data
```

> GHCR 镜像名由仓库决定，格式通常是 `ghcr.io/用户名或组织名/仓库名:标签`。如果包默认是私有的，需要在 GitHub Packages 里把包权限调整为可访问。

## 配置项

常用配置可以写在 `appsettings.json`，也可以用环境变量覆盖。

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `UseStaticAccount` | `true` | 是否启用静态单账号。 |
| `StaticUser` | 空 | 静态账号用户名，同时作为 S3 Access Key ID。 |
| `StaticPassword` | 空 | 静态账号密码，同时作为 S3 Secret Access Key。 |
| `StoreLocation` | `/datastore` | Bucket 和对象文件的存储根目录。 |
| `ConnectionStrings__DefaultConnection` | `Data Source={StoreLocation}/apiext.fs.db` | SQLite 数据库连接字符串。 |
| `DataProtection__KeysPath` | 容器内为 `/datastore/.data-protection` | 登录 Cookie 加密密钥目录，容器部署时必须持久化。 |
| `S3__BaseHost` | 空 | 可选。启用 virtual-hosted-style 请求改写，例如 `bucket.s3.example.com/key`。 |

示例：

```bash
-e StoreLocation=/data \
-e ConnectionStrings__DefaultConnection="Data Source=/data/apiext.fs.db" \
-e S3__BaseHost=s3.example.com
```

## S3 客户端配置

### 凭据规则

Apiext.Fs 中：

- `Access Key ID` 使用用户名。
- `Secret Access Key` 使用该用户的 S3 Secret，静态账号模式下就是 `StaticPassword`。
- 静态账号默认是超级管理员。
- 后台创建的普通用户，需要在 Web 后台为其配置 Bucket 权限。

### AWS CLI

推荐使用 path-style 访问方式：

```bash
aws configure set aws_access_key_id iouser --profile apiext-fs
aws configure set aws_secret_access_key iopassword --profile apiext-fs
aws configure set region us-east-1 --profile apiext-fs
aws configure set s3.addressing_style path --profile apiext-fs
```

常用命令：

```bash
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

### SDK 配置要点

多数 S3 SDK 需要设置：

- Endpoint：`http://localhost:9000`
- Region：可填 `us-east-1`
- Force path style：`true`
- Disable HTTPS / Use HTTP：本地或内网 HTTP 部署时启用

如果你配置了 `S3:BaseHost` 并完成 DNS/反向代理设置，也可以使用 virtual-hosted-style：

```text
http://demo.s3.example.com/README.md
```

服务端会把它改写成：

```text
http://s3.example.com/demo/README.md
```

## Bucket 权限策略

目前支持两种 Bucket 访问策略：

| 策略 | 说明 |
| --- | --- |
| `Private` | 默认值。对象查看、下载、上传、删除、列出都需要认证和权限。 |
| `PublicRead` | 公读私写。匿名用户只能 `GET` / `HEAD` 指定对象，不能列出 Bucket、不能上传、不能删除。 |

### 后台管理配置

进入 Web 后台：

```text
http://localhost:9000/_apiextfs
```

在 Bucket 页面选择 Bucket 的访问策略：

- `Private`
- `Public read`

切换后立即生效。

### S3 Bucket Policy 配置

也可以使用 S3 风格的 Bucket Policy。当前只支持和 MinIO/S3 类似的公读对象策略：

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

使用 AWS CLI 设置：

```bash
aws --endpoint-url http://localhost:9000 --profile apiext-fs s3api put-bucket-policy \
  --bucket demo \
  --policy file://public-read-policy.json
```

查看策略：

```bash
aws --endpoint-url http://localhost:9000 --profile apiext-fs s3api get-bucket-policy --bucket demo
```

删除策略，恢复私有：

```bash
aws --endpoint-url http://localhost:9000 --profile apiext-fs s3api delete-bucket-policy --bucket demo
```

开启 `PublicRead` 后，下面这种对象直连可以匿名访问：

```text
http://localhost:9000/demo/README.md
```

但匿名列出对象仍会被拒绝：

```text
http://localhost:9000/demo
```

## 预签名链接

Apiext.Fs 支持两类预签名访问：

- AWS SigV4 query-string 预签名 URL，即标准 `X-Amz-*` 参数。
- Apiext.Fs 内置预签名 token，参数名为 `X-Apiext-Fs-Presign`，后台“凭据/预签名”功能会生成这种链接。

对于私有 Bucket，可以使用预签名链接临时分享对象；对于 `PublicRead` Bucket，对象 GET/HEAD 本身已经允许匿名访问。

## 数据目录与数据库

默认情况下，数据都放在 `StoreLocation`：

```text
/datastore
├── apiext.fs.db
├── bucket-a
│   └── path/to/object.txt
└── bucket-b
```

SQLite 数据库默认位于：

```text
{StoreLocation}/apiext.fs.db
```

迁移会在应用启动时自动执行。也可以手动执行：

```cmd
dotnet run --project Apiext.Fs/Apiext.Fs.csproj -- --migrate
```

## GitHub Actions 发布镜像

仓库内置 workflow：

```text
.github/workflows/docker-publish.yml
```

它会在 **手动触发**（`workflow_dispatch`）时用 Docker Buildx 构建并推送镜像到 GHCR：

- Registry：`ghcr.io`
- Image：`${{ github.repository }}`
- Platforms：`linux/amd64,linux/arm64`
- Token：`${{ secrets.GITHUB_TOKEN }}`
- 默认版本标签：`yyMMdd-HHmm`（UTC，例如 `260412-1530`）

手动触发时可覆盖 `tag`。如果不填，自动使用 `yyMMdd-HHmm`。仅推送该版本标签，不再额外打 `latest`。

## GitHub Actions 发布二进制产物

仓库内置统一产物工作流：

```text
.github/workflows/publish-binaries.yml
```

手动触发（`workflow_dispatch`）后，会并行打包 **Linux + Windows** 可运行产物，并可汇总到同一个 GitHub Release。

- 默认版本：`yyMMdd-HHmm`（UTC）
- 形式：`--self-contained true` + `PublishSingleFile=true`
- 输入项：
  - `tag`：自定义版本
  - `build_linux` / `build_windows`：按需开关
  - `create_release`：是否创建 Release

### 产物列表

| 平台 | 文件 |
| --- | --- |
| Windows 安装版 | `Apiext.Fs-win-x64-<version>-Setup.exe`（NSIS，可注册 Windows 服务） |
| Windows 绿色版 | `Apiext.Fs-win-x64-<version>.zip` |
| Linux x64 | `Apiext.Fs-linux-x64-<version>.tar.gz` |
| Linux arm64 | `Apiext.Fs-linux-arm64-<version>.tar.gz` |

### Windows 安装版（服务）

- 默认装到 `Program Files\Apiext.Fs`
- 安装向导可选「安装并启动 Windows 服务」（默认开启）
- 服务名：`ApiextFs`，`start= auto`，失败自动重启
- 默认监听：`http://0.0.0.0:8080`
- Web 后台：`http://localhost:8080/_apiextfs`

```bat
sc start ApiextFs
sc stop ApiextFs
sc query ApiextFs
```

卸载时停止并删除服务；**保留** `App_Data`。

### Windows 绿色版

解压后双击 `Apiext.Fs.exe` 前台运行。

### Linux 包

```bash
tar -xzf Apiext.Fs-linux-x64-<version>.tar.gz
cd Apiext.Fs-linux-x64-<version>
./run.sh
```

包内附带示例 `apiext-fs.service`，可按需安装为 systemd 服务。

### 本地手动打包

Windows：

```cmd
dotnet publish Apiext.Fs/Apiext.Fs.csproj -c Release -r win-x64 --self-contained true -o publish\app /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true /p:PublishReadyToRun=true

copy installer\windows\appsettings.WindowsService.json publish\app\

makensis /DPRODUCT_VERSION=local /DSOURCE_DIR=%CD%\publish\app /DOUTFILE=%CD%\publish\Apiext.Fs-Setup.exe installer\windows\apiext-fs.nsi
```

Linux：

```bash
dotnet publish Apiext.Fs/Apiext.Fs.csproj -c Release -r linux-x64 --self-contained true -o publish/linux-x64 \
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true /p:PublishReadyToRun=true
```

## 本地开发

项目目标框架是 **.NET 10**。

构建：

```cmd
dotnet build Apiext.Fs.slnx
```

运行：

```cmd
dotnet run --project Apiext.Fs/Apiext.Fs.csproj
```

开发环境默认静态账号见 `Apiext.Fs/appsettings.Development.json`：

```text
iouser / iopassword
```

执行测试：

```cmd
dotnet build Apiext.Fs.slnx
Apiext.Fs.Tests\bin\Debug\net10.0\Apiext.Fs.Tests.exe
```

新增 EF Core Migration：

```cmd
dotnet ef migrations add <MigrationName> --project Apiext.Fs
dotnet ef database update --project Apiext.Fs
```

## 许可证

Apiext.Fs 使用 **GNU Affero General Public License v3.0 (AGPL-3.0)** 授权。完整文本见 [LICENSE.txt](LICENSE.txt)。

简要说明：你可以自由使用、修改和分发，包括商业使用；如果你把修改后的版本作为网络服务提供给用户，也需要按 AGPL-3.0 公开对应修改源码。
