namespace IndieFi.Web.Modules.Credits;

public static class CreditsModule
{
    public static IServiceCollection AddCreditsModule(this IServiceCollection services)
    {
        // Credits currently has no standalone application service. Keeping its
        // composition root explicit gives future credit use cases one owner.
        return services;
    }
}
