using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class EmailStorageSchemaTests
{
    private readonly PostgreSqlFixture _fixture;

    public EmailStorageSchemaTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UsersTable_StoresOnlyRequiredProtectedEmailColumns()
    {
        await using var db = _fixture.NewDbContext();
        var columns = await db.Database
            .SqlQueryRaw<UserColumn>(
                """
                SELECT column_name AS "Name", is_nullable AS "IsNullable"
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'users'
                """)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(columns, column => column.Name == "email");
        Assert.DoesNotContain(columns, column => column.Name == "normalized_email");

        AssertRequired(columns, "email_ciphertext");
        AssertRequired(columns, "email_nonce");
        AssertRequired(columns, "email_authentication_tag");
        AssertRequired(columns, "email_encryption_key_id");
    }

    private static void AssertRequired(
        IReadOnlyList<UserColumn> columns,
        string name)
    {
        var column = Assert.Single(columns, column => column.Name == name);
        Assert.Equal("NO", column.IsNullable);
    }

    public sealed record UserColumn(string Name, string IsNullable);
}
