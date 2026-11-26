using System.Text.RegularExpressions;
using SmartCv.Core.Backup;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Backup;

namespace SmartCv.Tests.Ops;

public sealed class A62RulesTests
{
    private static DateTime At(string value) => DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void First_evaluation_never_invents_missed_history()
    {
        var plan = BackupSchedule.Plan("01:30", false, null, At("2026-11-26T14:00:00"), TimeSpan.FromMinutes(60));
        Assert.Null(plan.RunSlotLocal); Assert.Empty(plan.MissedLocal); Assert.Equal(At("2026-11-26T01:30:00"), plan.HandledLocal);
        Assert.Equal(At("2026-11-27T01:30:00"), BackupSchedule.NextFull("01:30", At("2026-11-26T14:00:00")));
    }

    [Fact]
    public void Due_slot_runs_on_time_or_catches_up_once_and_records_each_missed_night()
    {
        var onTime = BackupSchedule.Plan("01:30", false, At("2026-11-25T01:30:00"), At("2026-11-26T01:31:00"), TimeSpan.FromMinutes(60));
        Assert.Equal(At("2026-11-26T01:30:00"), onTime.RunSlotLocal); Assert.False(onTime.CatchUp); Assert.Empty(onTime.MissedLocal);
        var late = BackupSchedule.Plan("01:30", false, At("2026-11-22T01:30:00"), At("2026-11-25T09:00:00"), TimeSpan.FromMinutes(60));
        Assert.True(late.CatchUp); Assert.Equal(At("2026-11-25T01:30:00"), late.RunSlotLocal);
        Assert.Equal([At("2026-11-23T01:30:00"), At("2026-11-24T01:30:00"), At("2026-11-25T01:30:00")], late.MissedLocal);
        Assert.Null(BackupSchedule.Plan("01:30", true, At("2026-11-20T01:30:00"), At("2026-11-25T09:00:00"), TimeSpan.FromMinutes(60)).RunSlotLocal);
    }

    [Theory]
    [InlineData("25:00", 4, 14, false)]
    [InlineData("01:30'; DROP", 4, 14, false)]
    [InlineData("01:30", 0, 14, false)]
    [InlineData("01:30", 4, 6, false)]
    [InlineData("01:30", 4, 91, false)]
    [InlineData("23:59", 24, 90, true)]
    public void Settings_limits(string time, int hours, int keep, bool valid) =>
        Assert.Equal(valid, new BackupSettingsDto(time, hours, keep, false, true, null).IsValid());

    [Fact]
    public void Only_the_twelve_seeded_rows_may_be_acknowledged_as_demo()
    {
        var seed = File.ReadAllLines(TestSupport.RepoFile("database/seed_demo.sql"));
        var rows = seed.Select(line => Regex.Match(line, @"^\('([0-9A-F-]{36})',.*N'Uploads/CV/.*'([0-9A-F]{64})'"))
            .Where(m => m.Success).ToDictionary(m => m.Groups[1].Value.ToLowerInvariant(), m => m.Groups[2].Value.ToLowerInvariant());
        Assert.Equal(12, rows.Count);
        Assert.Equal(rows.OrderBy(r => r.Key), KnownMissingRules.DemoRows.OrderBy(r => r.Key));
        var first = rows.First();
        Assert.Single(KnownMissingRules.Validate([new(first.Key, first.Value, KnownMissingRules.DemoSeed, "owner", "2026-11-26")]));
        void Refused(KnownMissingCv entry) => Assert.Throws<BackupException>(() => KnownMissingRules.Validate([entry]));
        // Even a seeded demo row is never acknowledged without a named approval.
        Refused(new(first.Key, first.Value, KnownMissingRules.DemoSeed));
        Refused(new(Guid.NewGuid().ToString(), first.Value, KnownMissingRules.DemoSeed, "owner", "2026-11-26"));
        Refused(new(first.Key, new string('a', 64), KnownMissingRules.DemoSeed, "owner", "2026-11-26"));
        Refused(new(Guid.NewGuid().ToString(), new string('a', 64), KnownMissingRules.AcceptedLoss));
        Refused(new(Guid.NewGuid().ToString(), new string('a', 64), "just-skip"));
        Assert.Single(KnownMissingRules.Validate([new(Guid.NewGuid().ToString(), new string('a', 64), KnownMissingRules.AcceptedLoss, "owner", "2026-11-26")]));
    }

    [Fact]
    public void Log_chain_stops_before_a_gap_and_refuses_targets_it_cannot_reach()
    {
        var full = new BackupPoint { LastLsn = 100, SqlFinishedServerTime = At("2026-11-26T01:35:00") };
        BackupPoint Log(decimal first, decimal last, string finished) => new() { FirstLsn = first, LastLsn = last, SqlFinishedServerTime = At(finished) };
        var logs = new[] { Log(90, 150, "2026-11-26T05:00:00"), Log(150, 200, "2026-11-26T09:00:00"), Log(250, 300, "2026-11-26T13:00:00") };
        Assert.Equal(2, RestoreEngine.SelectChain(full, logs, null).Count);
        Assert.Single(RestoreEngine.SelectChain(full, logs, At("2026-11-26T04:00:00")));
        Assert.Equal(2, RestoreEngine.SelectChain(full, logs, At("2026-11-26T08:00:00")).Count);
        Assert.Throws<BackupException>(() => RestoreEngine.SelectChain(full, logs, At("2026-11-26T12:00:00"))); // beyond the gap
        Assert.Throws<BackupException>(() => RestoreEngine.SelectChain(full, logs, At("2026-11-26T01:00:00"))); // before the full
        Assert.Throws<BackupException>(() => RestoreEngine.SelectChain(full, [], At("2026-11-26T02:00:00")));
    }

    [Fact]
    public void Report_counts_finished_jobs_and_never_invents_a_rate()
    {
        var now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        BackupJobDto Job(string type, string trigger, string status, int daysAgo) => new(Guid.NewGuid(), type, trigger, status, now.AddDays(-daysAgo), null, null,
            status == BackupJobStatuses.Queued ? null : now.AddDays(-daysAgo), null, 0, status == BackupJobStatuses.Failed ? "A62_FILES" : null, null, null, null, status == BackupJobStatuses.Queued ? null : 100, null, 1, 10, 1);
        var jobs = new[] { Job("full", "scheduled", "Succeeded", 1), Job("full", "scheduled", "Missed", 2), Job("log", "scheduled", "Failed", 3),
            Job("full", "manual", "Succeeded", 40), Job("verify", "manual", "Queued", 0) };
        var filter = new BackupReportFilter(new(2026, 10, 27), new(2026, 11, 25), null, null, null);
        var report = BackupReportBuilder.Build(jobs, new BackupOverviewDto(), filter, now);
        Assert.Equal((4, 1, 1, 1), (report.Total, report.Succeeded, report.Failed, report.Missed));
        Assert.Equal(33.3, report.SuccessRate); Assert.Equal(300, report.UploadedBytes);
        Assert.Equal(1, BackupReportBuilder.Build(jobs, new BackupOverviewDto(), filter with { Type = "log" }, now).Total);
        Assert.Equal(1, BackupReportBuilder.Build(jobs, new BackupOverviewDto(), filter with { Trigger = "manual" }, now).Total);
        Assert.Null(BackupReportBuilder.Build([], new BackupOverviewDto(), filter, now).SuccessRate);
        using var book = new ClosedXML.Excel.XLWorkbook(new MemoryStream(SmartCv.Infrastructure.Export.BackupReportExcelWriter.Write(report)));
        Assert.Equal(["Tổng quan", "Lịch sử", "Theo tuần"], book.Worksheets.Select(s => s.Name).ToArray());
        Assert.Equal(5, book.Worksheet("Lịch sử").LastRowUsed()!.RowNumber());
        Assert.All(book.Worksheets.SelectMany(s => s.CellsUsed()), cell => Assert.False(cell.HasFormula));
    }
}
