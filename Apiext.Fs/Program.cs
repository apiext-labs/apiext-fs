using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Apiext.Fs.Components;
using Apiext.Fs.Data;
using Apiext.Fs.Endpoints;
using Apiext.Fs.Middleware;
using Apiext.Fs.Services;
using Apiext.Fs.Services.BackgroundJobs;

namespace Apiext.Fs
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // When running as a Windows Service the default working directory is System32.
            // Pin content root to the executable directory so App_Data / appsettings resolve correctly.
            var isWindowsService = OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService();
            var contentRoot = AppContext.BaseDirectory;

            var options = new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = isWindowsService ? contentRoot : null
            };

            var builder = WebApplication.CreateBuilder(options);
            builder.WebHost.UseStaticWebAssets();

            // Enable both console (portable/dev) and Windows Service hosting.
            builder.Host.UseWindowsService();

            if (isWindowsService)
            {
                // Optional service-specific overrides next to the executable.
                builder.Configuration.AddJsonFile(
                    Path.Combine(contentRoot, "appsettings.WindowsService.json"),
                    optional: true,
                    reloadOnChange: false);

                // Prefer explicit URLs from configuration / env; fall back to a LAN-friendly bind.
                var urls = builder.Configuration["Urls"]
                           ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
                           ?? "http://0.0.0.0:8080";
                builder.WebHost.UseUrls(urls);
            }

            // Avoid host-specific providers (for example Windows Event Log) becoming a
            // runtime dependency when the process account cannot write to them.
            builder.Logging.ClearProviders();
            builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
            builder.Logging.AddConsole();
            builder.Logging.AddDebug();
            if (isWindowsService && OperatingSystem.IsWindows())
                builder.Logging.AddEventLog();

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            // AntDesign Pro layout settings
            builder.Services.Configure<AntDesign.ProLayout.ProSettings>(
                builder.Configuration.GetSection("ProSettings"));

            // Ant Design Blazor component library
            builder.Services.AddAntDesign();

            // Cookie authentication
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(o =>
                {
                    o.LoginPath    = "/_apiextfs/login";
                    o.LogoutPath   = "/_apiextfs/auth/logout";
                    o.AccessDeniedPath = "/_apiextfs/login";
                    o.Cookie.HttpOnly  = true;
                    o.Cookie.SameSite  = SameSiteMode.Strict;
                    o.SlidingExpiration = true;
                    o.ExpireTimeSpan    = TimeSpan.FromHours(8);
                });
            builder.Services.AddAuthorization();
            builder.Services.AddCascadingAuthenticationState();

            // S3-compatible API
            builder.Services.AddControllers();
            builder.Services.AddSingleton<IS3XmlSerializer, S3XmlSerializer>();

            // File system storage providers
            builder.Services.AddSingleton<IS3BucketService, FileSystemBucketService>();
            builder.Services.AddSingleton<IS3ObjectService, FileSystemObjectService>();
            builder.Services.AddSingleton<IS3MultipartService, FileSystemMultipartService>();
            builder.Services.AddHttpClient();

            // Background jobs
            builder.Services.AddScoped<IBackgroundJobQueue, BackgroundJobQueue>();
            builder.Services.AddScoped<IBackgroundJobHistoryService, BackgroundJobHistoryService>();
            builder.Services.AddScoped<IBackgroundJob, DownloadJob>();
            builder.Services.AddHostedService<BackgroundJobWorker>();

            // S3 credential store — users ARE the credentials (username=AccessKeyId, password=S3Secret)
            // No separate credential service needed.

            // Pre-signed URL store (in-memory)
            builder.Services.AddSingleton<IPresignedUrlService, PresignedUrlService>();

            // Authorization provider
            builder.Services.AddScoped<IAuthorizationProvider, AuthorizationProvider>();
            builder.Services.AddScoped<IUserManagementService, UserManagementService>();
            builder.Services.AddScoped<IBucketPolicyService, BucketPolicyService>();

            // Database
            var connectionString = PrepareStorageAndConnectionString(builder);
            builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
            builder.Services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(connectionString), ServiceLifetime.Scoped);

            // Keep authentication cookies decryptable when the host account or container changes.
            // The storage directory must be protected with the same permissions as the database.
            var configuredKeyDirectory = builder.Configuration["DataProtection:KeysPath"];
            var keyDirectory = string.IsNullOrWhiteSpace(configuredKeyDirectory)
                ? Path.Combine(builder.Environment.ContentRootPath, ".data-protection")
                : Path.IsPathFullyQualified(configuredKeyDirectory)
                    ? Path.GetFullPath(configuredKeyDirectory)
                    : Path.GetFullPath(configuredKeyDirectory, builder.Environment.ContentRootPath);
            Directory.CreateDirectory(keyDirectory);
            builder.Services.AddDataProtection()
                .SetApplicationName("Apiext.Fs")
                .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));

            var app = builder.Build();

            if (args.Contains("--migrate"))
            {
                MigrateToLatest(app);
                Console.WriteLine("Migration completed successfully");
                return;
            }

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/_apiextfs/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            //app.UseHttpsRedirection();

            // Virtual-hosted style: {bucket}.{host} → /{bucket}/key path rewrite
            app.UseMiddleware<VirtualHostedMiddleware>();

            // Attach x-amz-request-id / x-amz-id-2 to every response
            app.UseMiddleware<S3RequestIdMiddleware>();

            // S3 API authentication (SigV4, SigV2, pre-signed token)
            app.UseMiddleware<S3AuthMiddleware>();

            app.UseAuthentication();
            app.UseAuthorization();
            app.UseAntiforgery();

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            // Auth endpoints (plain form POST — no Blazor involved)
            app.MapAuthEndpoints();

            // S3 credential + pre-signed URL management (UI API)
            app.MapS3ManagementEndpoints();

            // UI-only ZIP download endpoint
            app.MapDownloadZipEndpoint();

            // S3 API controllers — must come after Blazor mapping so Blazor routes win
            app.MapControllers();

            MigrateToLatest(app);
            app.Run();
        }

        private static void MigrateToLatest(WebApplication app)
        {
            using (var scope = app.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                dbContext.Database.Migrate();
            }
        }

        private static string PrepareStorageAndConnectionString(WebApplicationBuilder builder)
        {
            var rawStoreLocation = builder.Configuration["StoreLocation"];
            if (string.IsNullOrWhiteSpace(rawStoreLocation))
                rawStoreLocation = "./datastore";

            var storeLocation = Path.IsPathFullyQualified(rawStoreLocation)
                ? Path.GetFullPath(rawStoreLocation)
                : Path.GetFullPath(rawStoreLocation, builder.Environment.ContentRootPath);
            Directory.CreateDirectory(storeLocation);
            builder.Configuration["StoreLocation"] = storeLocation;

            var connectionStringTemplate = builder.Configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(connectionStringTemplate))
                connectionStringTemplate = "Data Source={StoreLocation}/apiext.fs.db";

            var connectionString = connectionStringTemplate.Replace("{StoreLocation}", storeLocation);
            var sqliteConnection = new SqliteConnectionStringBuilder(connectionString);

            if (!string.IsNullOrWhiteSpace(sqliteConnection.DataSource)
                && !IsInMemorySqliteDataSource(sqliteConnection.DataSource)
                && !sqliteConnection.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                var dataSource = sqliteConnection.DataSource;
                if (!Path.IsPathFullyQualified(dataSource))
                    dataSource = Path.GetFullPath(dataSource);

                var databaseDirectory = Path.GetDirectoryName(dataSource);
                if (!string.IsNullOrWhiteSpace(databaseDirectory))
                    Directory.CreateDirectory(databaseDirectory);

                sqliteConnection.DataSource = dataSource;
            }

            return sqliteConnection.ConnectionString;
        }

        private static bool IsInMemorySqliteDataSource(string dataSource)
            => string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
               || (dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                   && dataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase));
    }
}
