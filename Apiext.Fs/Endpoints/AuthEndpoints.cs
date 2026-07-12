using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Apiext.Fs.Data;
using Apiext.Fs.Models.Entities;
using Apiext.Fs.Services;

namespace Apiext.Fs.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/_apiextfs/auth");

        // POST /_apiextfs/auth/login
        group.MapPost("/login", async (HttpContext ctx, IAuthorizationProvider auth) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var username = form["Username"].ToString().Trim();
            var password = form["Password"].ToString();
            var returnUrl = form["ReturnUrl"].ToString();

            var identity = await auth.AuthenticateAsync(username, password);
            if (identity is null)
                return Results.Redirect("/_apiextfs/login?error=1");

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, identity.UserId),
                new(ClaimTypes.Name, identity.Username),
                new("is_super_admin", identity.IsSuperAdmin.ToString().ToLowerInvariant())
            };
            var principal = new ClaimsPrincipal(
                new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
                new AuthenticationProperties { IsPersistent = false });

            var redirect = !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/')
                ? returnUrl
                : "/_apiextfs";

            return Results.Redirect(redirect);
        }).DisableAntiforgery();

        // POST /_apiextfs/auth/setup  — only allowed when no DB users exist
        group.MapPost("/setup", async (HttpContext ctx, IDbContextFactory<AppDbContext> dbFactory) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            if (await db.Users.AnyAsync())
                return Results.Redirect("/_apiextfs/login");

            var form = await ctx.Request.ReadFormAsync();
            var username = form["Username"].ToString().Trim();
            var password = form["Password"].ToString();
            var confirm  = form["Confirm"].ToString();

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return Results.Redirect("/_apiextfs/setup?error=empty");

            if (password != confirm)
                return Results.Redirect("/_apiextfs/setup?error=mismatch");

            db.Users.Add(new User
            {
                Username     = username,
                Password     = password,
                IsSuperAdmin = true
            });
            await db.SaveChangesAsync();

            return Results.Redirect("/_apiextfs/login?created=1");
        }).DisableAntiforgery();

        // GET /_apiextfs/auth/logout
        group.MapGet("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/_apiextfs/login");
        });
    }
}
