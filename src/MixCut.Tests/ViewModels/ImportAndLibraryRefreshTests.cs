using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MixCut.Data;
using MixCut.Models;
using MixCut.ViewModels;
using Xunit;

namespace MixCut.Tests.ViewModels;

public sealed class ImportAndLibraryRefreshTests
{
    [Fact]
    public async Task ImportBatch_AcceptsSameNameDifferentContent_AndSkipsContentCopies()
    {
        var root = Path.Combine(Path.GetTempPath(), "mixcut-import-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "a"));
        Directory.CreateDirectory(Path.Combine(root, "b"));
        try
        {
            var first = Path.Combine(root, "a", "01.mp4");
            var second = Path.Combine(root, "b", "01.mp4");
            var copy = Path.Combine(root, "copy.mp4");
            await File.WriteAllTextAsync(first, "first video");
            await File.WriteAllTextAsync(second, "other video");
            File.Copy(first, copy);
            var batch = await ImportViewModel.PrepareImportBatchAsync(new[] { first, second, copy }, Array.Empty<string>());
            Assert.Equal(new[] { first, second }, batch.Accepted.Select(x => x.Path));
            Assert.Single(batch.Skipped);
            Assert.All(batch.Accepted, x => Assert.NotNull(x.Hash));
            var next = await ImportViewModel.PrepareImportBatchAsync(new[] { second, copy }, new[] { ImportViewModel.ComputeFileHash(first)! });
            Assert.Equal(second, Assert.Single(next.Accepted).Path);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LinkExistingVideo_DoesNotDuplicateAssociationOrScheduleAnalysisAgain()
    {
        using var fixture = new Database();
        var vm = fixture.Import();
        var first = await vm.ImportOrLinkVideoAsync("unused", fixture.SecondProject.Id, "hash");
        var second = await vm.ImportOrLinkVideoAsync("unused", fixture.SecondProject.Id, "hash");
        Assert.Equal(fixture.VideoId, first.VideoId);
        Assert.True(first.NeedsAnalysis);
        Assert.False(second.NeedsAnalysis);
        using var db = fixture.CreateDbContext();
        Assert.Single(db.ProjectVideos.Where(pv => pv.ProjectId == fixture.SecondProject.Id));
    }

    [Fact]
    public async Task RemoveSharedVideo_PreservesOtherProjectsAnalyzingStateAndCancellation()
    {
        using var fixture = new Database();
        var vm = fixture.Import();
        await vm.ImportOrLinkVideoAsync("unused", fixture.SecondProject.Id, "hash");
        vm.DeleteVideo(fixture.VideoId, fixture.SecondProject.Id);
        using var db = fixture.CreateDbContext();
        Assert.Equal(VideoStatus.Analyzing, db.Videos.Single().Status);
        Assert.Null(db.Videos.Single().ErrorMessage);
        Assert.Equal(fixture.FirstProject.Id, db.ProjectVideos.Single().ProjectId);
        // CheckCancelled 是实际分析各阶段调用的入口；共享移除不得留下取消标志。
        typeof(ImportViewModel).GetMethod("CheckCancelled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(vm, new object[] { fixture.VideoId });
    }

    [Fact]
    public void SameProjectRefresh_KeepsFilterSelectionOrderAndUnchangedGroups_NewProjectResets()
    {
        using var fixture = new Database();
        using var vm = new SegmentLibraryViewModel(fixture, NullLogger<SegmentLibraryViewModel>.Instance);
        vm.LoadSegments(fixture.FirstProject);
        vm.Filter.SearchText = "keep";
        vm.ApplyFilter();
        vm.SetSelectionMode(true);
        var a = vm.AllSegments[0];
        var b = vm.AllSegments[1];
        vm.ToggleSelection(b);
        vm.ToggleSelection(a);
        vm.RebuildGroups();
        var group = Assert.Single(vm.Groups);
        vm.LoadSegments(fixture.FirstProject);
        vm.RebuildGroups(preserveExistingGroups: true);
        Assert.Same(group, Assert.Single(vm.Groups));
        Assert.Equal("keep", vm.Filter.SearchText);
        Assert.True(vm.IsSelectionMode);
        Assert.Equal(new[] { b.Id, a.Id }, vm.SelectedSegmentsInOrder.Select(s => s.Id));
        using (var db = fixture.CreateDbContext())
        {
            db.Segments.Remove(db.Segments.Single(s => s.Id == b.Id));
            db.SaveChanges();
        }
        vm.LoadSegments(fixture.FirstProject);
        Assert.Equal(a.Id, Assert.Single(vm.SelectedSegmentsInOrder).Id);
        vm.LoadSegments(fixture.SecondProject);
        Assert.Empty(vm.SelectedSegmentIds);
        Assert.Empty(vm.Filter.SearchText);
        Assert.False(vm.IsSelectionMode);
    }

    private sealed class Database : IDbContextFactory<MixCutDbContext>, IDisposable
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        private readonly DbContextOptions<MixCutDbContext> _options;
        public Project FirstProject { get; } = new() { Name = "first" };
        public Project SecondProject { get; } = new() { Name = "second" };
        public Guid VideoId { get; } = Guid.NewGuid();
        public Database()
        {
            _connection.Open();
            _options = new DbContextOptionsBuilder<MixCutDbContext>().UseSqlite(_connection).Options;
            using var db = CreateDbContext();
            db.Database.EnsureCreated();
            db.Projects.AddRange(FirstProject, SecondProject);
            db.Videos.Add(new Video { Id = VideoId, Name = "01.mp4", LocalPath = "unused", ContentHash = "hash", Status = VideoStatus.Analyzing });
            db.ProjectVideos.Add(new ProjectVideo { ProjectId = FirstProject.Id, VideoId = VideoId });
            db.Segments.AddRange(
                new Segment { VideoId = VideoId, Text = "keep A", StartTime = 0, EndTime = 1 },
                new Segment { VideoId = VideoId, Text = "keep B", StartTime = 1, EndTime = 2 });
            db.SaveChanges();
        }
        public MixCutDbContext CreateDbContext() => new(_options);
        public ImportViewModel Import() => new(this, null!, null!, null!, null!, null!, null!, null!, NullLogger<ImportViewModel>.Instance);
        public void Dispose() => _connection.Dispose();
    }
}
