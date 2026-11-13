using System.Runtime.ExceptionServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Google.Protobuf;
using UiAtlas.Catalog;
using UiAtlas.Shell.Catalog;
using UIMaps.Application.Core;
using UIMaps.Protocol.V3;
using Xunit;

namespace UiAtlas.Shell.Tests;

public sealed class RecordingAppendUiTests
{
    internal static void AppendDuringRefreshOpensPickerAndIgnoresObsoleteRefreshFailure()
    {
        var refresh = new TaskCompletionSource<CatalogReply>();
        var maps = new TaskCompletionSource<CatalogReply>(); var appended = false;
        var view = new AtlasRecordingsView(new AtlasCatalogClient(), (request, _) => request.Operation switch
        {
            "recordings" => refresh.Task,
            "maps-summary" => maps.Task,
            "recording-append" => CompleteAppend(request),
            _ => throw new InvalidOperationException(request.Operation)
        });
        Task<CatalogReply> CompleteAppend(CatalogRequest request)
        {
            Assert.Equal("chosen", request.MapId); Assert.Equal("recording", request.RecordingId); appended = true;
            return Task.FromResult(new CatalogReply { Message = "Observations added", MapId = "chosen" });
        }
        var owner = new Window { Content = view }; owner.Show();
        Exception? error = null;
        owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            var picker = owner.OwnedWindows.OfType<AppendMapPicker>().Single();
            try
            {
                Assert.True(picker.IsVisible); Assert.Empty(Descendants<ListBox>(picker).Single().Items);
                refresh.SetException(new IOException("obsolete failure"));
                maps.SetResult(new CatalogReply { Maps = [Map("chosen")] }); await picker.Loading;
                Assert.DoesNotContain(Descendants<TextBlock>(view), t => t.Text.Contains("obsolete failure"));
                Descendants<ListBox>(picker).Single().SelectedIndex = 0;
                Descendants<Button>(picker).Single(b => Equals(b.Content, "Add observations")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception e) { error = e; picker.Close(); }
        }));
        try
        {
            var operation = view.AppendAsync(new("recording", DateTimeOffset.UtcNow, "complete", "fixture", 1, null));
            Assert.True(operation.IsCompletedSuccessfully); if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
            Assert.True(appended);
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "Observations added");
        }
        finally { owner.Close(); }
    }

    [Fact]
    public void PickerReportsLoadFailureAndSupportsRetry() => Sta(() =>
    {
        var reads = 0; Exception? error = null;
        var picker = new AppendMapPicker("fixture", () => ++reads == 1
            ? Task.FromException<CatalogMap[]>(new IOException("catalog offline")) : Task.FromResult(new[] { Map("chosen") }));
        picker.Loaded += (_, _) => picker.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            try
            {
                await picker.Loading;
                Assert.Contains(Descendants<TextBlock>(picker), t => t.Text.Contains("catalog offline"));
                Assert.False(Descendants<Button>(picker).Single(b => Equals(b.Content, "Add observations")).IsEnabled);
                await picker.LoadMapsAsync(); Assert.Single(Descendants<ListBox>(picker).Single().Items);
            }
            catch (Exception e) { error = e; }
            finally { picker.Close(); }
        }));
        Assert.False(picker.ShowDialog()); Assert.Null(picker.SelectedMap);
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    });

    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { action(); } catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public async Task CompletionRefreshWaitsForActivationReadAndShowsPendingDraft()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new DelayedSource(entered, release);
        var model = new MapsLibraryViewModel(source);
        var activation = model.RefreshAsync(CancellationToken.None);
        await entered.Task;
        var completion = model.RefreshAsync(CancellationToken.None, waitForPending: true);
        Assert.False(completion.IsCompleted);
        release.SetResult(); await Task.WhenAll(activation, completion);
        Assert.Equal(2, source.Reads); Assert.True(model.LastRefreshSucceeded, model.Status);
        var row = model.FindTransferredApplication(["chosen"])!.Maps.Single();
        Assert.True(row.IsReady); Assert.True(row.HasAddedObservations); Assert.True(row.CanMarkReady); Assert.False(row.CanMarkDraft);
        var completed = model.FindTransferredApplication(["chosen"], showDraft: true)!;
        Assert.Equal(1, completed.ObservationCount);
        Assert.Null(completed.Maps.Single().Record.Revision);
    }

    private sealed class DelayedSource(TaskCompletionSource entered, TaskCompletionSource release) : IMapsLibrarySource
    {
        public int Reads;
        public async Task<IReadOnlyList<MapsLibraryRecord>> LoadAsync(CancellationToken token)
        {
            if (++Reads == 1) { entered.SetResult(); await release.Task; }
            var map = Record("chosen", "2026-11-11T00:00:00Z"); map.Entry.IsReady = true;
            map.Draft!.Sources.Add(new SourceObservationSet { SourceObservationSetRef = "new-source" });
            return [map with { Revision = new UiMapRevision { MapRevisionRef = "old-revision", Target = new TargetApplicationIdentity { ExecutableName = "fixture" } } }];
        }
    }

    [Fact]
    public async Task CompletionPublishesItsExactMapEvenWhenAnotherMapIsNewer()
    {
        var old = Record("chosen", "2026-11-10T00:00:00Z");
        var newer = Record("other", "2026-11-11T00:00:00Z");
        var source = new Source([old, newer]); var model = new MapsLibraryViewModel(source);
        await model.RefreshAsync(CancellationToken.None);
        var completed = model.FindTransferredApplication([old.Entry.MapDraftRef])!;
        await model.PublishCompletedRecordingAsync(completed, CancellationToken.None);
        Assert.Same(old, source.Published);
    }

    [Fact]
    public void PickerFiltersArchivesAndForeignAppsRequiresSelectionAndCancelsWithoutDestination()
    {
        var maps = new[] { Map("first"), Map("second"), Map("foreign", "other"), Map("archived", archived: true), Map("legacy") with { Generation = "" } };
        Assert.Equal(new[] { "first", "second" }, AppendMapPicker.Candidates(maps, "fixture.exe").Select(c => c.Map.Id));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var picker = new AppendMapPicker(maps, "fixture");
                picker.Loaded += (_, _) => picker.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    try
                    {
                        var add = Descendants<Button>(picker).Single(b => Equals(b.Content, "Add observations"));
                        Assert.False(add.IsEnabled); Assert.Null(picker.SelectedMap);
                        Descendants<ListBox>(picker).Single().SelectedIndex = 1;
                        Assert.True(add.IsEnabled);
                        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception error) { failure = error; picker.Close(); }
                }));
                Assert.True(picker.ShowDialog());
                Assert.Equal("second", picker.SelectedMap!.Id);
                var canceled = new AppendMapPicker(maps, "fixture");
                canceled.Loaded += (_, _) => canceled.Dispatcher.BeginInvoke(new Action(canceled.Close));
                Assert.False(canceled.ShowDialog()); Assert.Null(canceled.SelectedMap);
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static MapsLibraryRecord Record(string id, string updated) => new(new MapLibraryEntry {
        ApplicationRef = "fixture", ApplicationDisplayName = "Fixture", MapDraftRef = id, UpdatedAt = updated }, null,
        new UiMapDraft { MapDraftRef = id, ApplicationRef = "fixture" }, null) { CatalogMapId = id, CatalogGeneration = "generation" };
    private static CatalogMap Map(string id, string executable = "fixture", bool archived = false) =>
        new(id, id, "fixture", "generation", Convert.ToBase64String(new MapLibraryEntry {
            ApplicationRef = "fixture", ApplicationDisplayName = "Fixture", MapDraftRef = id, Archived = archived }.ToByteArray()), null, null, null,
            Application: new("Fixture", executable, "1", "Fixture window", [], null));
    private sealed class Source(MapsLibraryRecord[] records) : IMapsLibrarySource
    {
        public MapsLibraryRecord? Published;
        public Task<IReadOnlyList<MapsLibraryRecord>> LoadAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<MapsLibraryRecord>>(records);
        public Task MarkReadyAsync(MapsLibraryRecord record, CancellationToken token) { Published = record; return Task.CompletedTask; }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
