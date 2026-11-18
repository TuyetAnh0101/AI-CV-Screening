using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SmartCv.Core.Candidates;
using SmartCv.Core.Storage;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Privacy;

namespace SmartCv.Tests.Candidates;

public class A43SafetyTests
{
    [Fact]
    public void Validation_checks_boundaries_and_control_characters()
    {
        Assert.Empty(CandidateErasePlan.Validate(true, new string('a', 100), new string('b', 500)));
        Assert.Contains("handledBy", CandidateErasePlan.Validate(true, new string('a', 101), null).Keys);
        Assert.Contains("note", CandidateErasePlan.Validate(true, "HR", new string('b', 501)).Keys);
        Assert.Contains("handledBy", CandidateErasePlan.Validate(true, "hr\noperator", null).Keys);
        var errors = CandidateErasePlan.Validate(false, null, new string('a', 501));
        Assert.Equal(3, errors.Count);
    }

    [Theory]
    [InlineData("New", true)]
    [InlineData("Reviewing", true)]
    [InlineData("Shortlisted", true)]
    [InlineData("Interview", true)]
    [InlineData("OnHold", true)]
    [InlineData("Rejected", false)]
    public void Withdraws_all_active_stages_and_never_rewrites_rejection(string stage, bool withdraw) =>
        Assert.Equal(withdraw ? 1 : 0, CandidateErasePlan.Withdrawals([(1, stage)]).Count);

    [Fact]
    public void Migration_only_creates_the_journal_and_index_without_Candidate_FK_or_existing_table_changes()
    {
        var migration = new SmartCv.Infrastructure.Data.Migrations.CandidateDataRequests();
        Assert.Equal(2, migration.UpOperations.Count);
        var table = Assert.IsType<CreateTableOperation>(migration.UpOperations[0]);
        Assert.Equal("CandidateDataRequests", table.Name); Assert.Empty(table.ForeignKeys);
        Assert.Single(table.CheckConstraints); Assert.Contains("Export", table.CheckConstraints[0].Sql); Assert.Contains("Erase", table.CheckConstraints[0].Sql);
        var index = Assert.IsType<CreateIndexOperation>(migration.UpOperations[1]);
        Assert.Equal(new[] { "CandidateId", "CreatedAt" }, index.Columns);
        Assert.Equal("nvarchar(max)", table.Columns.Single(c => c.Name == "CvIds").ColumnType);
        Assert.Equal("CandidateDataRequests", Assert.IsType<DropTableOperation>(Assert.Single(migration.DownOperations)).Name);
        using var db = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>()
            .UseSqlServer("Server=(local);Database=A43_NotUsed;Integrated Security=True").Options);
        Assert.False(db.Database.HasPendingModelChanges()); // model inspection only; no database connection
    }

    internal sealed class FaultingFiles(ICvFileStorage storage) : ICvFileStorage, ICvErasureStorage
    {
        private ICvErasureStorage Files => (ICvErasureStorage)storage;
        internal int PrepareCount;
        internal int FailAfterPrepare;
        internal Guid? FailRollback;
        internal Guid? FailComplete;
        public bool PrepareErasure(Guid id, string path)
        {
            var result = Files.PrepareErasure(id, path);
            if (++PrepareCount == FailAfterPrepare) throw new IOException("PRIVATE synthetic failure");
            return result;
        }
        public void RollbackErasure(Guid id) { if (id == FailRollback) throw new IOException("PRIVATE"); Files.RollbackErasure(id); }
        public void CompleteErasure(Guid id) { if (id == FailComplete) throw new IOException("PRIVATE"); Files.CompleteErasure(id); }
        public IReadOnlyList<Guid> PendingErasureIds(int limit) => Files.PendingErasureIds(limit);
        public Task<string> SaveAsync(Guid id, string ext, Stream content, DateTime at, CancellationToken ct = default) => storage.SaveAsync(id, ext, content, at, ct);
        public Stream OpenRead(string path) => storage.OpenRead(path);
        public bool Exists(string path) => storage.Exists(path);
        public void Delete(string path) => storage.Delete(path);
    }

    [Fact]
    public async Task Failure_during_second_file_preparation_restores_both_originals_and_removes_journals()
    {
        var files = new FaultingFiles(TestSupport.Storage(TestSupport.TempRoot())) { FailAfterPrepare = 2 };
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        using var a = new MemoryStream(TestSupport.Pdf("A43 first synthetic"));
        using var b = new MemoryStream(TestSupport.Pdf("A43 second synthetic"));
        var p1 = await files.SaveAsync(first, ".pdf", a, DateTime.UtcNow);
        var p2 = await files.SaveAsync(second, ".pdf", b, DateTime.UtcNow);
        var batch = new CvFileErasureBatch(files);
        batch.Prepare(first, p1);
        Assert.Throws<IOException>(() => batch.Prepare(second, p2));
        Assert.False(files.Exists(p1)); Assert.False(files.Exists(p2));
        batch.Rollback();
        Assert.True(files.Exists(p1)); Assert.True(files.Exists(p2)); Assert.Empty(files.PendingErasureIds(200));
        using var restored = files.OpenRead(p1); using var content = new MemoryStream(); await restored.CopyToAsync(content);
        Assert.Equal(TestSupport.Pdf("A43 first synthetic"), content.ToArray());
    }

    [Fact]
    public async Task Cleanup_failure_keeps_recovery_evidence_and_continues_other_items()
    {
        var files = new FaultingFiles(TestSupport.Storage(TestSupport.TempRoot()));
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        using var a = new MemoryStream(TestSupport.Pdf("synthetic")); using var b = new MemoryStream(TestSupport.Pdf("synthetic"));
        var p1 = await files.SaveAsync(first, ".pdf", a, DateTime.UtcNow);
        var p2 = await files.SaveAsync(second, ".pdf", b, DateTime.UtcNow);
        var batch = new CvFileErasureBatch(files);
        batch.Prepare(first, p1); batch.Prepare(second, p2);
        files.FailComplete = first;
        Assert.Throws<CandidateErasureRecoveryException>(batch.Complete);
        Assert.Equal(new[] { first }, files.PendingErasureIds(200));
        files.FailComplete = null; batch.Complete();
        Assert.False(files.Exists(p1)); Assert.False(files.Exists(p2)); Assert.Empty(files.PendingErasureIds(200));
    }

    [Fact]
    public async Task Rollback_failure_does_not_discard_the_failed_items_only_recovery_copy()
    {
        var files = new FaultingFiles(TestSupport.Storage(TestSupport.TempRoot()));
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        using var a = new MemoryStream(TestSupport.Pdf("synthetic")); using var b = new MemoryStream(TestSupport.Pdf("synthetic"));
        var p1 = await files.SaveAsync(first, ".pdf", a, DateTime.UtcNow);
        var p2 = await files.SaveAsync(second, ".pdf", b, DateTime.UtcNow);
        var batch = new CvFileErasureBatch(files); batch.Prepare(first, p1); batch.Prepare(second, p2);
        files.FailRollback = first;
        Assert.Throws<CandidateErasureRecoveryException>(batch.Rollback);
        Assert.False(files.Exists(p1)); Assert.True(files.Exists(p2)); Assert.Equal(new[] { first }, files.PendingErasureIds(200));
        files.FailRollback = null; batch.Rollback(); Assert.True(files.Exists(p1)); Assert.Empty(files.PendingErasureIds(200));
    }
}
