namespace IndieFi.Web.Modules.Feedback.Domain;

public enum ReviewerGrade
{
    Unranked,
    Bronze,
    Silver,
    Gold,
    Platinum,
}

public sealed record ReviewerReputationScores(
    decimal QualityScore,
    decimal CompletionReliabilityScore,
    decimal TrustScore,
    ReviewerGrade Grade);

public static class ReviewerReputationPolicy
{
    public const int MinimumCompletedReviewCount = 5;
    public const int MinimumRatingCount = 3;
    public const int QualityPriorCount = 5;
    public const int QualityPriorScore = 3;

    public const decimal SilverMinimumScore = 55m;
    public const decimal GoldMinimumScore = 70m;
    public const decimal PlatinumMinimumScore = 85m;

    public static ReviewerReputationScores Calculate(
        int completedReviewCount,
        int expiredAssignmentCount,
        int ratingCount,
        int ratingScoreSum)
    {
        ValidateAggregates(
            completedReviewCount,
            expiredAssignmentCount,
            ratingCount,
            ratingScoreSum);

        var qualityAverage =
            ((QualityPriorScore * QualityPriorCount) + ratingScoreSum)
            / (decimal)(QualityPriorCount + ratingCount);
        var qualityScore = (qualityAverage - 1m) / 4m * 100m;
        var completionReliabilityScore =
            (completedReviewCount + 1m)
            / (completedReviewCount + expiredAssignmentCount + 2m)
            * 100m;
        var trustScore = qualityScore * 0.7m
            + completionReliabilityScore * 0.3m;

        return new ReviewerReputationScores(
            qualityScore,
            completionReliabilityScore,
            trustScore,
            DetermineGrade(
                completedReviewCount,
                ratingCount,
                trustScore));
    }

    public static ReviewerGrade DetermineGrade(
        int completedReviewCount,
        int ratingCount,
        decimal trustScore)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completedReviewCount);
        ArgumentOutOfRangeException.ThrowIfNegative(ratingCount);
        if (trustScore is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(trustScore));
        }

        if (completedReviewCount < MinimumCompletedReviewCount
            || ratingCount < MinimumRatingCount)
        {
            return ReviewerGrade.Unranked;
        }

        if (trustScore < SilverMinimumScore)
        {
            return ReviewerGrade.Bronze;
        }

        if (trustScore < GoldMinimumScore)
        {
            return ReviewerGrade.Silver;
        }

        return trustScore < PlatinumMinimumScore
            ? ReviewerGrade.Gold
            : ReviewerGrade.Platinum;
    }

    private static void ValidateAggregates(
        int completedReviewCount,
        int expiredAssignmentCount,
        int ratingCount,
        int ratingScoreSum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completedReviewCount);
        ArgumentOutOfRangeException.ThrowIfNegative(expiredAssignmentCount);
        ArgumentOutOfRangeException.ThrowIfNegative(ratingCount);
        ArgumentOutOfRangeException.ThrowIfNegative(ratingScoreSum);

        var minimumScoreSum = (long)ratingCount * FeedbackQualityPolicy.MinimumScore;
        var maximumScoreSum = (long)ratingCount * FeedbackQualityPolicy.MaximumScore;
        if ((ratingCount == 0 && ratingScoreSum != 0)
            || ratingScoreSum < minimumScoreSum
            || ratingScoreSum > maximumScoreSum)
        {
            throw new ArgumentOutOfRangeException(nameof(ratingScoreSum));
        }
    }
}
