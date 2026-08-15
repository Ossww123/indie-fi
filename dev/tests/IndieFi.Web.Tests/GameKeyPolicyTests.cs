using IndieFi.Web.Modules.KeyDistributions.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class GameKeyPolicyTests
{
    [Fact]
    public void ParseBatch_TrimsKeysSkipsBlankLinesAndPreservesOrder()
    {
        var result = GameKeyPolicy.ParseBatch(" FIRST \r\n\nSECOND\n   ");

        Assert.True(result.IsValid);
        Assert.Equal(
            ["FIRST", "SECOND"],
            result.Keys.Select(key => key.Value));
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void ParseBatch_WithOnlyBlankLines_ReturnsEmptyViolation()
    {
        var result = GameKeyPolicy.ParseBatch(" \r\n\n   ");

        Assert.False(result.IsValid);
        var violation = Assert.Single(result.Violations);
        Assert.Equal(GameKeyInputError.Empty, violation.Error);
        Assert.Null(violation.LineNumber);
    }

    [Fact]
    public void ParseBatch_WithDuplicateAfterTrimming_ReturnsLineWithoutSecret()
    {
        var result = GameKeyPolicy.ParseBatch("SECRET-KEY\n  SECRET-KEY  ");

        Assert.False(result.IsValid);
        Assert.Single(result.Keys);
        var violation = Assert.Single(result.Violations);
        Assert.Equal(GameKeyInputError.Duplicate, violation.Error);
        Assert.Equal(2, violation.LineNumber);
        Assert.DoesNotContain("SECRET-KEY", violation.ToString());
    }

    [Fact]
    public void ParseBatch_TreatsDifferentLetterCaseAsDifferentOpaqueKeys()
    {
        var result = GameKeyPolicy.ParseBatch("ABC-123\nabc-123");

        Assert.True(result.IsValid);
        Assert.Equal(2, result.Keys.Count);
    }

    [Fact]
    public void ParseBatch_WithTooLongKey_ReturnsItsLineNumber()
    {
        var result = GameKeyPolicy.ParseBatch(
            $"VALID\n{new string('K', GameKeyPolicy.MaxLength + 1)}");

        Assert.False(result.IsValid);
        var violation = Assert.Single(result.Violations);
        Assert.Equal(GameKeyInputError.TooLong, violation.Error);
        Assert.Equal(2, violation.LineNumber);
    }

    [Fact]
    public void ParseBatch_WithEmbeddedControlCharacter_ReturnsItsLineNumber()
    {
        var result = GameKeyPolicy.ParseBatch("VALID\nKEY\tVALUE");

        Assert.False(result.IsValid);
        var violation = Assert.Single(result.Violations);
        Assert.Equal(GameKeyInputError.ContainsControlCharacter, violation.Error);
        Assert.Equal(2, violation.LineNumber);
    }

    [Fact]
    public void ParseBatch_AtMaximumLength_IsValid()
    {
        var result = GameKeyPolicy.ParseBatch(new string('K', GameKeyPolicy.MaxLength));

        Assert.True(result.IsValid);
        Assert.Equal(GameKeyPolicy.MaxLength, Assert.Single(result.Keys).Value.Length);
    }
}
