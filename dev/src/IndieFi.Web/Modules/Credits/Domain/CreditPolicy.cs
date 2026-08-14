namespace IndieFi.Web.Modules.Credits.Domain;

public static class CreditPolicy
{
    // A new account needs enough credit to start the feedback exchange cycle.
    public const int NewAccountGrant = 3;
}
