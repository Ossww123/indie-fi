using IndieFi.Web.Modules.Feedback.Application;

namespace IndieFi.Web.Modules.Feedback;

public static class FeedbackModule
{
    public static IServiceCollection AddFeedbackModule(this IServiceCollection services)
    {
        services.AddScoped<ReviewRequestService>();
        services.AddScoped<ReviewClaimService>();
        services.AddScoped<FeedbackSubmissionService>();
        services.AddScoped<FeedbackRatingService>();
        services.AddScoped<ReviewLeaseSweepService>();
        services.AddScoped<ReviewDeadlineNotificationService>();

        // The sweep runs independently so an expired lease is released even when
        // nobody visits a review page.
        services.AddHostedService(provider => new ReviewLeaseSweepWorker(
            // A hosted service is a singleton, so it creates a scope instead of
            // resolving the scoped sweep service directly.
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<ReviewLeaseSweepWorker>>(),
            TimeSpan.FromMinutes(1)));
        services.AddHostedService(provider => new ReviewDeadlineNotificationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<ReviewDeadlineNotificationWorker>>(),
            TimeSpan.FromMinutes(1)));

        return services;
    }
}
