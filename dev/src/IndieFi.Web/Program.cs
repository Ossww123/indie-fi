using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Infrastructure.Authentication;
using IndieFi.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));

builder.Services.AddScoped<KeyClaimService>();
builder.Services.AddScoped<KeyDropSchedulingService>();
builder.Services.AddScoped<GameManagementService>();
builder.Services.AddScoped<AccountRegistrationService>();
builder.Services.AddScoped<AccountAuthenticationService>();
builder.Services.AddScoped<ReviewRequestService>();
builder.Services.AddScoped<ReviewClaimService>();
builder.Services.AddScoped<FeedbackSubmissionService>();
builder.Services.AddScoped<ReviewLeaseSweepService>();
// The sweep runs on its own so a missed deadline frees the request even if nobody
// presses the button on the review page.
builder.Services.AddHostedService(provider => new ReviewLeaseSweepWorker(
    // A hosted service lives as long as the app, so it takes the scope factory rather than
    // the sweep service: resolving that scoped service here is refused by the container.
    provider.GetRequiredService<IServiceScopeFactory>(),
    provider.GetRequiredService<ILogger<ReviewLeaseSweepWorker>>(),
    TimeSpan.FromMinutes(1)));

// Hashes passwords with PBKDF2. Used on its own, without the rest of Identity.
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

// Signing in writes an encrypted cookie; the browser sends it back on every request.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
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
