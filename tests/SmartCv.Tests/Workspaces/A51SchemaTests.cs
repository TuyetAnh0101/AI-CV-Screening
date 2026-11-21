using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SmartCv.Core.Auth;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Data.Migrations;

namespace SmartCv.Tests;

/// <summary>A51: kiểm tra mô hình EF và migration AddWorkspaces, không cần SQL Server.</summary>
public class A51SchemaTests
{
    internal const string PreviousMigration = "20261120114152_AddRankingExportLogs";
    internal const string WorkspaceMigration = "20261121040902_AddWorkspaces";

    private static IModel Model()
    {
        using var db = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>()
            .UseSqlServer("Server=(local);Database=SmartCv_A51_ModelOnly;Integrated Security=True").Options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    [Fact]
    public void Model_has_no_pending_changes_after_the_migration_snapshot()
    {
        using var db = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>()
            .UseSqlServer("Server=(local);Database=SmartCv_A51_ModelOnly;Integrated Security=True").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(A53JournalSqlTests.Current, db.Database.GetMigrations().Last());
        Assert.Equal(WorkspaceMigration, db.Database.GetMigrations().SkipLast(1).Last());
        Assert.Equal(PreviousMigration, db.Database.GetMigrations().SkipLast(2).Last());
    }

    [Fact]
    public void Constants_follow_the_contract_and_keep_system_role_separate_from_workspace_role()
    {
        Assert.Equal(1, WorkspaceKinds.InternalWorkspaceId);
        Assert.Equal(new[] { "Internal", "Personal" }, WorkspaceKinds.All);
        Assert.Equal(new[] { "Owner", "Member" }, WorkspaceRoles.All);
        Assert.Equal(new[] { "Admin", "Local", "google", "linkedin" }, SignupSources.All);
        // Quyền hệ thống (Admin/HR) và quyền workspace (Owner/Member) không được dùng chung giá trị.
        Assert.Empty(UserRoles.All.Intersect(WorkspaceRoles.All));
        Assert.Equal(WorkspaceRoles.Member, new WorkspaceMember().Role);
        Assert.Equal(SignupSources.Admin, new User().SignupSource);
    }

    [Theory]
    [InlineData(typeof(Job))]
    [InlineData(typeof(Cv))]
    [InlineData(typeof(Candidate))]
    [InlineData(typeof(Application))]
    [InlineData(typeof(PromptVersion))]
    [InlineData(typeof(EvaluationSet))]
    [InlineData(typeof(EvaluationRun))]
    [InlineData(typeof(ExportLog))]
    public void Root_entities_are_workspace_owned_with_required_WorkspaceId_unset_until_stamped(Type type)
    {
        Assert.True(typeof(IWorkspaceOwned).IsAssignableFrom(type));
        var property = Model().FindEntityType(type)!.FindProperty(nameof(IWorkspaceOwned.WorkspaceId))!;
        Assert.False(property.IsNullable);
        // A52: entity mới để WorkspaceId = Unassigned (-1); SmartCvDbContext đóng dấu workspace hiện hành khi lưu (xem A52SaveGuardTests).
        Assert.Equal(WorkspaceKinds.UnassignedWorkspaceId, ((IWorkspaceOwned)Activator.CreateInstance(type)!).WorkspaceId);
    }

    [Fact]
    public void AuditEvent_has_nullable_WorkspaceId_and_is_not_IWorkspaceOwned()
    {
        Assert.False(typeof(IWorkspaceOwned).IsAssignableFrom(typeof(AuditEvent)));
        var model = Model();
        var audit = model.FindEntityType(typeof(AuditEvent))!;
        Assert.True(audit.FindProperty(nameof(AuditEvent.WorkspaceId))!.IsNullable);
        Assert.Contains(audit.GetIndexes(), i => i.GetDatabaseName() == "IX_AuditEvents_Workspace_Time"
            && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "WorkspaceId", "OccurredAtUtc" }));
        // Workspace, WorkspaceMember, token, đăng nhập ngoài là dữ liệu toàn cục: không đánh dấu IWorkspaceOwned.
        foreach (var type in new[] { typeof(Workspace), typeof(WorkspaceMember), typeof(UserToken), typeof(UserExternalLogin), typeof(User) })
            Assert.False(typeof(IWorkspaceOwned).IsAssignableFrom(type));
    }

    [Fact]
    public void Unique_constraints_are_scoped_to_the_workspace_and_legacy_global_ones_are_gone()
    {
        var model = Model();
        var job = model.FindEntityType(typeof(Job))!;
        Assert.DoesNotContain(job.GetIndexes(), i => i.GetDatabaseName() == "UQ_Jobs_Title");
        var title = Assert.Single(job.GetIndexes(), i => i.GetDatabaseName() == "UQ_Jobs_Workspace_Title");
        Assert.True(title.IsUnique);
        Assert.Equal(new[] { "WorkspaceId", "Title" }, title.Properties.Select(p => p.Name));
        Assert.Contains(job.GetKeys(), k => k.GetName() == "AK_Jobs_Workspace_Job" && k.Properties.Select(p => p.Name).SequenceEqual(new[] { "WorkspaceId", "JobId" }));

        var cv = model.FindEntityType(typeof(Cv))!;
        Assert.DoesNotContain(cv.GetKeys(), k => k.Properties.Count == 1 && k.Properties[0].Name == nameof(Cv.FileHash));
        var hash = Assert.Single(cv.GetIndexes(), i => i.GetDatabaseName() == "IX_Cvs_Workspace_FileHash");
        Assert.True(hash.IsUnique);   // giữ bất biến cũ "một hash một CV" trong phạm vi workspace; chặn tải đồng thời
        Assert.Equal(new[] { "WorkspaceId", "FileHash" }, hash.Properties.Select(p => p.Name));
        Assert.Contains(cv.GetKeys(), k => k.GetName() == "AK_Cvs_Workspace_Cv");

        var prompt = model.FindEntityType(typeof(PromptVersion))!;
        Assert.DoesNotContain(prompt.GetIndexes(), i => i.GetDatabaseName() == "UX_PromptVersions_OneActive");
        var active = Assert.Single(prompt.GetIndexes(), i => i.GetDatabaseName() == "UX_PromptVersions_Workspace_OneActive");
        Assert.True(active.IsUnique);
        Assert.Equal(new[] { "WorkspaceId" }, active.Properties.Select(p => p.Name));
        Assert.Equal("[IsActive] = 1", active.GetFilter());
        Assert.Contains(prompt.GetKeys(), k => k.GetName() == "AK_PromptVersions_Workspace_Prompt");
        Assert.Contains(prompt.GetKeys(), k => k.GetName() == "UQ_PromptVersions_Workspace_VersionName");
        Assert.DoesNotContain(prompt.GetKeys(), k => k.GetName() == "UQ_PromptVersions_VersionName");

        var set = model.FindEntityType(typeof(EvaluationSet))!;
        Assert.Contains(set.GetKeys(), k => k.GetName() == "UQ_EvaluationSets_Workspace_Name");
        Assert.DoesNotContain(set.GetKeys(), k => k.GetName() == "UQ_EvaluationSets_Name");
    }

    [Theory]
    [InlineData(typeof(Application), "FK_Applications_Jobs", typeof(Job))]
    [InlineData(typeof(Application), "FK_Applications_Cvs", typeof(Cv))]
    [InlineData(typeof(EvaluationRun), "FK_EvaluationRuns_PromptVersions", typeof(PromptVersion))]
    [InlineData(typeof(EvaluationRun), "FK_EvaluationRuns_EvaluationSets", typeof(EvaluationSet))]
    [InlineData(typeof(EvaluationSet), "FK_EvaluationSets_Jobs", typeof(Job))]
    [InlineData(typeof(Cv), "FK_Cvs_Candidates", typeof(Candidate))]
    public void Cross_entity_relations_use_composite_workspace_foreign_keys(Type dependent, string name, Type principal)
    {
        var fk = Assert.Single(Model().FindEntityType(dependent)!.GetForeignKeys(), f => f.GetConstraintName() == name);
        Assert.Equal(principal, fk.PrincipalEntityType.ClrType);
        Assert.Equal(2, fk.Properties.Count);
        Assert.Equal("WorkspaceId", fk.Properties[0].Name);
        Assert.Equal("WorkspaceId", fk.PrincipalKey.Properties[0].Name);
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);   // cùng hành vi xóa như trước A51
    }

    [Fact]
    public void Workspace_tables_have_the_documented_keys_checks_and_indexes()
    {
        var model = Model();
        var member = model.FindEntityType(typeof(WorkspaceMember))!;
        Assert.Equal(new[] { "WorkspaceId", "UserId" }, member.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Contains(member.GetCheckConstraints(), c => c.Name == "CK_WorkspaceMembers_Role" && c.Sql.Contains("'Owner'") && c.Sql.Contains("'Member'"));
        Assert.Contains(member.GetIndexes(), i => i.GetDatabaseName() == "IX_WorkspaceMembers_UserId");
        Assert.All(member.GetForeignKeys(), f => Assert.Equal(DeleteBehavior.NoAction, f.DeleteBehavior));

        var ws = model.FindEntityType(typeof(Workspace))!;
        Assert.Equal(100, ws.FindProperty(nameof(Workspace.Name))!.GetMaxLength());
        Assert.Contains(ws.GetCheckConstraints(), c => c.Name == "CK_Workspaces_Kind" && c.Sql.Contains("'Internal'") && c.Sql.Contains("'Personal'"));
        var owner = Assert.Single(ws.GetIndexes(), i => i.GetDatabaseName() == "UX_Workspaces_PersonalOwner");
        Assert.True(owner.IsUnique);
        Assert.Contains("'Personal'", owner.GetFilter());
        Assert.True(ws.FindProperty(nameof(Workspace.OwnerUserId))!.IsNullable);
        var seed = Assert.Single(ws.GetSeedData());
        Assert.Equal(1, seed["WorkspaceId"]);
        Assert.Equal("Internal", seed["Kind"]);
        Assert.False(seed.ContainsKey("OwnerUserId") && seed["OwnerUserId"] is not null);

        var token = model.FindEntityType(typeof(UserToken))!;
        Assert.True(Assert.Single(token.GetIndexes(), i => i.GetDatabaseName() == "UX_UserTokens_TokenHash").IsUnique);
        Assert.Equal(DeleteBehavior.Cascade, Assert.Single(token.GetForeignKeys()).DeleteBehavior);
        var external = model.FindEntityType(typeof(UserExternalLogin))!;
        Assert.True(Assert.Single(external.GetIndexes(), i => i.GetDatabaseName() == "UX_UserExternalLogins_Provider_Key").IsUnique);
        Assert.True(Assert.Single(external.GetIndexes(), i => i.GetDatabaseName() == "UX_UserExternalLogins_User_Provider").IsUnique);
        Assert.Equal(DeleteBehavior.Cascade, Assert.Single(external.GetForeignKeys()).DeleteBehavior);
    }

    [Fact]
    public void User_keeps_non_nullable_PasswordHash_and_HasPassword_is_computed_not_stored()
    {
        var user = Model().FindEntityType(typeof(User))!;
        Assert.False(user.FindProperty(nameof(User.PasswordHash))!.IsNullable);
        Assert.Null(user.FindProperty(nameof(User.HasPassword)));
        Assert.True(user.FindProperty(nameof(User.EmailVerifiedAt))!.IsNullable);
        Assert.True(user.FindProperty(nameof(User.TermsAcceptedAt))!.IsNullable);
        Assert.Equal(SignupSources.Admin, user.FindProperty(nameof(User.SignupSource))!.GetDefaultValue());
        Assert.Contains(user.GetCheckConstraints(), c => c.Name == "CK_Users_SignupSource");
        Assert.Contains(user.GetCheckConstraints(), c => c.Name == "CK_Users_Role" && !c.Sql.Contains("Owner") && !c.Sql.Contains("Member"));
        Assert.False(new User().HasPassword);
        Assert.True(new User { PasswordHash = "x" }.HasPassword);
    }

    [Fact]
    public void Empty_password_hash_never_verifies_so_external_only_accounts_cannot_log_in_with_a_password()
    {
        var hasher = new PasswordHasher<User>();
        var user = new User { Email = "a@example.test", PasswordHash = "" };
        foreach (var attempt in new[] { "x", "password", "Aa1!aaaaaaaa", " " })
            Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(user, user.PasswordHash, attempt));
    }

    [Fact]
    public void Migration_backfills_without_touching_seed_jobs_or_account_credentials_and_is_reversible()
    {
        var migration = new AddWorkspaces();
        var up = migration.UpOperations;

        // Không xóa / chèn lại 5 Job seed (sẽ phá dữ liệu đã có và vướng khóa ngoại).
        Assert.DoesNotContain(up, o => o is DeleteDataOperation);
        Assert.DoesNotContain(up.OfType<InsertDataOperation>(), o => o.Table != "Workspaces");
        var workspace = Assert.Single(up.OfType<InsertDataOperation>());
        Assert.Equal(1, workspace.Values[0, 0]);

        // Thứ tự: tạo bảng -> chèn workspace 1 -> cột Users -> backfill -> NOT NULL -> ràng buộc mới.
        int IndexOf(Func<MigrationOperation, bool> match) => up.ToList().FindIndex(o => match(o));
        var createWorkspaces = IndexOf(o => o is CreateTableOperation { Name: "Workspaces" });
        var insertWorkspace = IndexOf(o => o is InsertDataOperation);
        var firstSql = IndexOf(o => o is SqlOperation);
        var alterJobs = IndexOf(o => o is AlterColumnOperation { Table: "Jobs", Name: "WorkspaceId" });
        var addJobKey = IndexOf(o => o is AddUniqueConstraintOperation { Name: "AK_Jobs_Workspace_Job" });
        var addFk = IndexOf(o => o is AddForeignKeyOperation { Name: "FK_Applications_Jobs" });
        Assert.True(createWorkspaces < insertWorkspace && insertWorkspace < firstSql && firstSql < alterJobs && alterJobs < addJobKey && addJobKey < addFk);

        var alters = up.OfType<AlterColumnOperation>().ToList();
        Assert.All(alters, a => { Assert.Equal("WorkspaceId", a.Name); Assert.False(a.IsNullable); Assert.True(a.OldColumn.IsNullable); });
        Assert.Equal(new[] { "Applications", "Candidates", "Cvs", "EvaluationRuns", "EvaluationSets", "Jobs", "PromptVersions" }, alters.Select(a => a.Table).Order());

        // Mọi câu SQL DML nằm trong EXEC (script idempotent) và không ghi vào cột nhạy cảm của Users.
        var sql = up.OfType<SqlOperation>().Select(o => o.Sql).ToList();
        Assert.All(sql, s => Assert.StartsWith("EXEC(N'", s.TrimStart()));
        var all = string.Join("\n", sql);
        foreach (var forbidden in new[] { "PasswordHash", "SecurityStamp", "DELETE" })
            Assert.DoesNotContain(forbidden, all);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(all, @"UPDATE \[Users\]").Count);   // chỉ điền cột mới EmailVerifiedAt
        Assert.Contains("UPDATE [Users] SET [EmailVerifiedAt] = [CreatedAt]", all);
        Assert.DoesNotContain("UPDATE [ExportLogs]", all);
        Assert.Equal(new[] { "DROP CONSTRAINT [DF_ExportLogs_WorkspaceId_Tmp]" }, System.Text.RegularExpressions.Regex.Matches(all, @"DROP [A-Z]+ \[[A-Za-z_]+\]").Select(m => m.Value));   // trigger TR_ExportLogs_NoChange chặn UPDATE

        // Down() đảo ngược đầy đủ.
        var down = migration.DownOperations;
        Assert.Equal(new[] { "UserExternalLogins", "UserTokens", "WorkspaceMembers", "Workspaces" },
            down.OfType<DropTableOperation>().Select(o => o.Name));
        Assert.Equal(9, down.OfType<DropColumnOperation>().Count(c => c.Name == "WorkspaceId"));
        Assert.Contains(down, o => o is CreateIndexOperation { Name: "UQ_Jobs_Title" });
        Assert.Contains(down, o => o is AddUniqueConstraintOperation { Name: "UQ_Cvs_FileHash" });
    }

    [Fact]
    public void Historical_migrations_are_not_edited_to_depend_on_workspaces()
    {
        var root = FindRepositoryRoot();
        var seedJobs = File.ReadAllText(Path.Combine(root, "src", "SmartCv.Infrastructure", "Data", "Migrations", "20261106142155_SeedJobs.cs"));
        Assert.DoesNotContain("Workspace", seedJobs);
        foreach (var file in Directory.GetFiles(Path.Combine(root, "src", "SmartCv.Infrastructure", "Data", "Migrations"), "2026*.cs")
                     .Where(f => string.CompareOrdinal(Path.GetFileName(f), WorkspaceMigration) < 0))
            Assert.DoesNotContain("WorkspaceId", File.ReadAllText(file));
    }

    internal static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartCv.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("SmartCv.slnx not found above the test output directory.");
    }
}
