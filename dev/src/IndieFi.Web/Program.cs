using System.Security.Claims;
using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
using IndieFi.Web.Infrastructure.Authentication;
using IndieFi.Web.Modules.Accounts;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Credits;
using IndieFi.Web.Modules.Feedback;
using IndieFi.Web.Modules.Games;
using IndieFi.Web.Modules.KeyDistributions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));

builder.Services.AddAccountsModule();
builder.Services.AddGamesModule();
builder.Services.AddCreditsModule();
builder.Services.AddFeedbackModule();
builder.Services.AddKeyDistributionsModule();

var googleAuthenticationSection = builder.Configuration.GetSection(
    GoogleAuthenticationSettings.SectionName);

// Successful app logins use one cookie. Google first writes only to the short-lived
// external cookie so its claims can be validated before an IndieFi session is issued.
builder.Services
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
    })
    .AddGoogle(ExternalLoginPolicy.GoogleProvider, options =>
    {
        options.ClientId = googleAuthenticationSection[
            nameof(GoogleAuthenticationSettings.ClientId)] ?? string.Empty;
        options.ClientSecret = googleAuthenticationSection[
            nameof(GoogleAuthenticationSettings.ClientSecret)] ?? string.Empty;
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
    });

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

// Order matters: work out who the caller is, then decide what they may do.
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();

// This file has no class of its own: the code above is compiled into a generated
// entry-point class that is internal. Tests need to name that class to boot the app
// in memory, so this line makes it public without changing anything at run time.
public partial class Program;
