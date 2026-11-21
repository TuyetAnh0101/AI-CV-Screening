using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Data.Migrations;

namespace SmartCv.Tests.Auth;

public class A27MigrationTests
{
    internal const string Previous = "20261119024613_ScappWorkflowV2";
    internal const string Current = "20261119110208_AddUsers";

    [Theory]
    [InlineData("Server=localhost;Database=SmartCvDb;Integrated Security=True")]
    [InlineData("Server=localhost;Database=master;Integrated Security=True")]
    [InlineData("Server=localhost;Integrated Security=True")]
    [InlineData("Server=localhost;Database=smartcv_a27_test_wrong_case;Integrated Security=True")]
    [InlineData("Server=localhost;Database=SmartCv_A27_Test_designated;AttachDBFilename=C:\\synthetic.mdf;Integrated Security=True")]
    public void SQL_test_guard_rejects_application_system_or_attached_catalogs(string connection) =>
        Assert.Throws<ArgumentException>(() => A27SqlFactAttribute.Validate(new SqlConnectionStringBuilder(connection)));

    [Fact]
    public void SQL_test_guard_accepts_only_an_explicit_designated_test_connection() =>
        A27SqlFactAttribute.Validate(new SqlConnectionStringBuilder("Server=localhost;Database=SmartCv_A27_Test_designated;Integrated Security=True"));

    internal static SmartCvDbContext ModelDb() => new(new DbContextOptionsBuilder<SmartCvDbContext>()
        .UseSqlServer("Server=(local);Database=SmartCv_A27_ModelOnly;Integrated Security=True").Options);

    [Fact]
    public void Users_schema_matches_all_ten_columns_and_required_constraints()
    {
        using var db = ModelDb();
        var user = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(User))!;
        var contract = new (string Name, string Type, bool Nullable)[]
        {
            ("UserId", "int", false), ("Email", "nvarchar(254)", false), ("FullName", "nvarchar(150)", false),
            ("PasswordHash", "varchar(500)", false), ("Role", "nvarchar(20)", false), ("IsActive", "bit", false),
            ("CreatedAt", "datetime2", false), ("LastLoginAt", "datetime2", true),
            ("MustChangePassword", "bit", false), ("SecurityStamp", "varchar(64)", false),
            // A51: ba cột mới (HasPassword là thuộc tính tính toán, không có cột)
            ("EmailVerifiedAt", "datetime2", true), ("SignupSource", "varchar(20)", false), ("TermsAcceptedAt", "datetime2", true)
        };
        Assert.Equal(contract.OrderBy(c => c.Name), user.GetProperties().Select(p => (p.Name, p.GetColumnType()!, p.IsNullable)).OrderBy(c => c.Name));
        Assert.Equal("PK_Users", user.FindPrimaryKey()!.GetName());
        Assert.Equal(SqlServerValueGenerationStrategy.IdentityColumn, user.FindProperty("UserId")!.GetValueGenerationStrategy());
        Assert.Equal("SYSUTCDATETIME()", user.FindProperty("CreatedAt")!.GetDefaultValueSql());
        var email = Assert.Single(user.GetIndexes());
        Assert.Equal("UQ_Users_Email", email.GetDatabaseName()); Assert.True(email.IsUnique);
        Assert.Equal("Email", Assert.Single(email.Properties).Name);
        Assert.Equal(new[] { "CK_Users_Role", "CK_Users_SignupSource" }, user.GetCheckConstraints().Select(c => c.Name).Order());   // A51 thêm CK_Users_SignupSource
        var check = Assert.Single(user.GetCheckConstraints(), c => c.Name == "CK_Users_Role");
        Assert.Contains("N'Admin', N'HR'", check.Sql);
        Assert.Contains("Latin1_General_100_BIN2", check.Sql);
        Assert.Contains("DATALENGTH", check.Sql);
    }

    [Fact]
    public void History_has_nullable_non_cascading_user_link_and_preserves_all_A35_metadata()
    {
        using var db = ModelDb();
        var model = db.GetService<IDesignTimeModel>().Model;
        var previous = db.GetService<IModelRuntimeInitializer>().Initialize(new ScappWorkflowV2().TargetModel, designTime: true);
        foreach (var oldEntity in previous.GetEntityTypes())
        {
            var current = model.FindEntityType(oldEntity.Name)!;
            Assert.Equal(oldEntity.GetCheckConstraints().Select(c => (c.Name, c.Sql)), current.GetCheckConstraints().Select(c => (c.Name, c.Sql)));
        }
        var ev = model.FindEntityType(typeof(ApplicationEvent))!;
        Assert.True(ev.FindProperty("UserId")!.IsNullable);
        var fk = Assert.Single(ev.GetForeignKeys(), f => f.PrincipalEntityType.ClrType == typeof(User));
        Assert.Equal("FK_ApplicationEvents_Users", fk.GetConstraintName());
        Assert.False(fk.IsRequired); Assert.Equal(DeleteBehavior.NoAction, fk.DeleteBehavior);
        Assert.Contains(ev.GetIndexes(), i => i.GetDatabaseName() == "IX_ApplicationEvents_UserId");
        Assert.Contains(ev.GetDeclaredTriggers(), t => t.ModelName == "TR_ApplicationEvents_NoChange");
        Assert.False(ev.IsSqlOutputClauseUsed());
        Assert.Equal(DeleteBehavior.Restrict, Assert.Single(ev.GetForeignKeys(), f => f.PrincipalEntityType.ClrType == typeof(Application)).DeleteBehavior);
        Assert.Equal(100, ev.FindProperty("Actor")!.GetMaxLength());
        Assert.Equal(500, ev.FindProperty("Note")!.GetMaxLength());
    }

    [Fact]
    public void Up_and_Down_are_exactly_the_A27_allowlist()
    {
        var migration = new AddUsers();
        Assert.Equal(5, migration.UpOperations.Count);
        var column = Assert.Single(migration.UpOperations.OfType<AddColumnOperation>());
        Assert.Equal(("ApplicationEvents", "UserId", "int", true), (column.Table, column.Name, column.ColumnType, column.IsNullable));
        Assert.Null(column.DefaultValue); Assert.Null(column.DefaultValueSql);
        var table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal("Users", table.Name); Assert.Equal(10, table.Columns.Count);
        Assert.Equal("CK_Users_Role", Assert.Single(table.CheckConstraints).Name);
        Assert.Equal(new[] { "IX_ApplicationEvents_UserId", "UQ_Users_Email" }, migration.UpOperations.OfType<CreateIndexOperation>().Select(i => i.Name).Order());
        var fk = Assert.Single(migration.UpOperations.OfType<AddForeignKeyOperation>());
        Assert.Equal(("FK_ApplicationEvents_Users", "ApplicationEvents", "Users", ReferentialAction.NoAction),
            (fk.Name, fk.Table, fk.PrincipalTable, fk.OnDelete));
        Assert.Collection(migration.DownOperations,
            op => Assert.Equal("FK_ApplicationEvents_Users", Assert.IsType<DropForeignKeyOperation>(op).Name),
            op => Assert.Equal("Users", Assert.IsType<DropTableOperation>(op).Name),
            op => Assert.Equal("IX_ApplicationEvents_UserId", Assert.IsType<DropIndexOperation>(op).Name),
            op => Assert.Equal("UserId", Assert.IsType<DropColumnOperation>(op).Name));
    }

    [Fact]
    public void Actual_model_delta_matches_migration_and_snapshot_has_no_pending_changes()
    {
        using var db = ModelDb();
        var model = db.GetService<IDesignTimeModel>().Model;
        var old = db.GetService<IModelRuntimeInitializer>().Initialize(new ScappWorkflowV2().TargetModel, designTime: true);
        var differences = db.GetService<IMigrationsModelDiffer>().GetDifferences(old.GetRelationalModel(), model.GetRelationalModel());
        Assert.Equal(new AddUsers().UpOperations.Select(o => o.GetType()), differences.Select(o => o.GetType()));
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(Current, db.Database.GetMigrations().Last());
    }

    [Fact]
    public void Generated_delta_scripts_never_rewrite_history_constraints_or_trigger()
    {
        using var db = ModelDb();
        var migrator = db.GetService<IMigrator>();
        var up = migrator.GenerateScript(Previous, Current);
        var down = migrator.GenerateScript(Current, Previous);
        Assert.Contains("ALTER TABLE [ApplicationEvents] ADD [UserId] int NULL", up);
        Assert.Contains("CREATE TABLE [Users]", up);
        Assert.Contains("CREATE UNIQUE INDEX [UQ_Users_Email]", up);
        foreach (var sql in new[] { up, down })
        {
            foreach (var forbidden in new[] { "DROP TABLE [ApplicationEvents]", "CREATE TABLE [ApplicationEvents]", "TRIGGER", "CK_ApplicationEvents_", "UPDATE [ApplicationEvents]", "DELETE FROM [ApplicationEvents]", "ALTER TABLE [Cvs]", "ALTER TABLE [Applications]", "ALTER TABLE [EvaluationRuns]", "ALTER TABLE [EvaluationSets]" })
                Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("DROP TABLE", up, StringComparison.OrdinalIgnoreCase);
    }
}
