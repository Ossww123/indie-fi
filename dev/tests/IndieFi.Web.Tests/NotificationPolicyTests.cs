using IndieFi.Web.Modules.Notifications.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class NotificationPolicyTests
{
    [Fact]
    public void GetDefaults_CoversEverySupportedKind()
    {
        foreach (var kind in Enum.GetValues<NotificationKind>())
        {
            var defaults = NotificationPolicy.GetDefaults(kind);

            Assert.True(defaults.WebEnabled);
        }
    }

    [Theory]
    [InlineData(NotificationKind.ReviewAssigned)]
    [InlineData(NotificationKind.ReviewDeadlineSoon)]
    [InlineData(NotificationKind.FeedbackCompleted)]
    [InlineData(NotificationKind.KeyDropOpened)]
    [InlineData(NotificationKind.ReportResolved)]
    public void GetDefaults_TimeSensitiveOrResultEvent_EnablesEmail(
        NotificationKind kind)
    {
        Assert.True(NotificationPolicy.GetDefaults(kind).EmailEnabled);
    }

    [Theory]
    [InlineData(NotificationKind.ReviewExpired)]
    [InlineData(NotificationKind.FavoriteGameKeyDropCreated)]
    [InlineData(NotificationKind.FollowedUserGamePublished)]
    public void GetDefaults_InformationalEvent_DisablesEmail(
        NotificationKind kind)
    {
        Assert.False(NotificationPolicy.GetDefaults(kind).EmailEnabled);
    }

    [Fact]
    public void GetDefaults_AccountStatusChange_IsMandatoryOnBothChannels()
    {
        var defaults = NotificationPolicy.GetDefaults(
            NotificationKind.AccountStatusChanged);

        Assert.True(defaults.WebEnabled);
        Assert.True(defaults.EmailEnabled);
        Assert.True(defaults.IsMandatory);
    }

    [Fact]
    public void TimingPolicy_MatchesTheApprovedOperatingRules()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), NotificationPolicy.ReviewDeadlineLeadTime);
        Assert.Equal(TimeSpan.FromDays(90), NotificationPolicy.WebRetention);
        Assert.Equal(
            [
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(1),
                TimeSpan.FromHours(6),
            ],
            NotificationPolicy.EmailRetryDelays);
    }
}
