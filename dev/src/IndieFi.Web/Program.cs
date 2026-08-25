using System.Security.Claims;
using System.Net;
using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
using IndieFi.Web.Infrastructure.Authentication;
using IndieFi.Web.Infrastructure.Realtime;
using IndieFi.Web.Modules.Accounts;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Credits;
using IndieFi.Web.Modules.Feedback;
using IndieFi.Web.Modules.Games;
using IndieFi.Web.Modules.KeyDistributions;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.Media;
using IndieFi.Web.Modules.Notifications;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Profiles;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Query strings on authentication links contain one-time tokens. Keep request-start
// and SQL-command categories above Information even if a broad logging override is
// enabled, and never allow EF parameter values into logs.
builder.Logging.AddFilter(
    "Microsoft.AspNetCore.Hosting.Diagnostics",
    LogLevel.Warning);
builder.Logging.AddFilter(
    "Microsoft.AspNetCore.HttpLogging",
    LogLevel.Warning);
builder.Logging.AddFilter(
    "Microsoft.EntityFrameworkCore.Database.Command",
    LogLevel.Warning);

// Add services to the container.
builder.Services.AddRazorPages();
var secureCookiePolicy = builder.Environment.IsDevelopment()
    ? CookieSecurePolicy.SameAsRequest
    : CookieSecurePolicy.Always;

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = secureCookiePolicy;
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<SessionCookieAuthenticationEvents>();
builder.Services.AddScoped<AccountSignInService>();
builder.Services.AddOptions<GoogleAuthenticationSettings>()
    .BindConfiguration(GoogleAuthenticationSettings.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<
    IValidateOptions<GoogleAuthenticationSettings>,
    GoogleAuthenticationSettingsValidator>();

var databaseConnectionString =
    builder.Configuration.GetConnectionString("Database");
if (string.IsNullOrWhiteSpace(databaseConnectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:Database is required for the web application.");
}

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(databaseConnectionString);
    options.EnableSensitiveDataLogging(false);
    if (!builder.Environment.IsDevelopment())
    {
        options.EnableDetailedErrors(false);
    }
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.RequireHeaderSymmetry = true;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);

    foreach (var value in builder.Configuration
                 .GetSection("ReverseProxy:KnownProxies")
                 .Get<string[]>() ?? [])
    {
        if (!IPAddress.TryParse(value, out var address))
        {
            throw new InvalidOperationException(
                $"ReverseProxy:KnownProxies contains an invalid IP address: '{value}'.");
        }

        options.KnownProxies.Add(address);
    }
});
builder.Services.AddHttpsRedirection(options =>
{
    options.HttpsPort = 443;
    options.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
});
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(180);
});
builder.Services.AddIndieFiDataProtection(
    builder.Configuration,
    builder.Environment);

builder.Services.AddAccountsModule();
builder.Services.AddProfilesModule();
builder.Services.AddGamesModule();
builder.Services.AddCreditsModule();
builder.Services.AddFeedbackModule();
builder.Services.AddIndieFiRealtime(builder.Configuration);
builder.Services.AddKeyDistributionsModule(builder.Configuration);
builder.Services.AddNotificationsModule(builder.Configuration);
builder.Services.AddMediaModule(builder.Configuration, MediaHostRole.Web);

var googleAuthenticationSection = builder.Configuration.GetSection(
    GoogleAuthenticationSettings.SectionName);
var googleAuthenticationEnabled = googleAuthenticationSection.GetValue<bool>(
    nameof(GoogleAuthenticationSettings.Enabled));
var googleClientId = googleAuthenticationSection[
    nameof(GoogleAuthenticationSettings.ClientId)] ?? string.Empty;
var googleClientSecret = googleAuthenticationSection[
    nameof(GoogleAuthenticationSettings.ClientSecret)] ?? string.Empty;

// Successful app logins use one cookie. Google first writes only to the short-lived
// external cookie so its claims can be validated before an IndieFi session is issued.
var authenticationBuilder = builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = secureCookiePolicy;
        options.EventsType = typeof(SessionCookieAuthenticationEvents);
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    })
    .AddCookie(ExternalAuthenticationDefaults.TemporaryScheme, options =>
    {
        options.Cookie.Name = ExternalAuthenticationDefaults.TemporaryCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = secureCookiePolicy;
        options.Cookie.MaxAge = ExternalAuthenticationDefaults.TemporaryLifetime;
        options.ExpireTimeSpan = ExternalAuthenticationDefaults.TemporaryLifetime;
        options.SlidingExpiration = false;
    });

if (googleAuthenticationEnabled
    && !string.IsNullOrWhiteSpace(googleClientId)
    && !string.IsNullOrWhiteSpace(googleClientSecret))
{
    authenticationBuilder.AddGoogle(ExternalLoginPolicy.GoogleProvider, options =>
    {
        options.ClientId = googleClientId;
        options.ClientSecret = googleClientSecret;
        options.SignInScheme = ExternalAuthenticationDefaults.TemporaryScheme;
        options.SaveTokens = false;
        options.RemoteAuthenticationTimeout =
            ExternalAuthenticationDefaults.TemporaryLifetime;
        options.CorrelationCookie.HttpOnly = true;
        options.CorrelationCookie.SameSite = SameSiteMode.None;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;

        // Keep only the three values needed to decide login, signup, or linking.
        options.ClaimActions.Clear();
        options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "sub");
        options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
        options.ClaimActions.MapJsonKey(
            ExternalAuthenticationDefaults.GoogleEmailVerifiedClaim,
            "email_verified",
            ClaimValueTypes.Boolean);
        options.Events.OnRemoteFailure = async context =>
        {
            context.HandleResponse();
            await context.HttpContext.SignOutAsync(
                ExternalAuthenticationDefaults.TemporaryScheme);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Redirect("/Login?googleError=remote");
        };
    });
}

var app = builder.Build();

// Configure the HTTP request pipeline.
// This must run before HTTPS redirection and authentication. Only the explicitly
// trusted loopback proxy (or configured addresses) may describe the public scheme.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

if (app.Environment.IsProduction())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Browser pages get a useful, generic status screen. Machine-facing endpoints keep
// their original empty or structured responses so clients never receive HTML errors.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api")
        && !context.Request.Path.StartsWithSegments("/hubs")
        && !context.Request.Path.StartsWithSegments("/health"),
    branch => branch.UseStatusCodePagesWithReExecute("/Status/{0}"));

app.UseRouting();

// Order matters: work out who the caller is, then decide what they may do.
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();
app.MapMediaEndpoints(builder.Configuration);
app.MapHub<WaitingRoomHub>("/hubs/waiting-room")
   .RequireAuthorization();
app.MapHub<NotificationHub>("/hubs/notifications")
   .RequireAuthorization();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    AllowCachingResponses = false,
});

app.Run();

// This file has no class of its own: the code above is compiled into a generated
// entry-point class that is internal. Tests need to name that class to boot the app
// in memory, so this line makes it public without changing anything at run time.
public partial class Program;
