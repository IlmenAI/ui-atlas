using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;
using static UiAtlas.Core.Tests.MapperOverlayWindowTests;

namespace UiAtlas.Core.Tests;

[Collection("Mapper desktop")]
public sealed class RecorderFieldReviewTests
{
    [Fact]
    public void MarkingIsCancelableAndReviewReturnsToThePausedRecorderWithoutInventingVerification()
    {
        Sta(() =>
        {
            var targetWindow = new Window { Title = "Field recorder fixture", Width = 500, Height = 280 };
            RecordingControlPanel? panel = null;
            try
            {
                targetWindow.Show();
                var target = WindowCatalog.Resolve(new WindowInteropHelper(targetWindow).Handle.ToInt64());
                panel = new("field-review-fixture", "Fixture", target); panel.Start();
                var window = Field<Window>(panel, "_window");
                var control = new AutomationObservation("name", "", "name", "Guest name", "ControlType.Edit", "Edit",
                    new(30, 40, 140, 25), true, false, "WPF", target.Hwnd);
                var observed = FieldObservationPresentationTests.Field(control) with
                {
                    State = FieldObservationState.ChangeObserved, BeforeValue = "", AfterValue = "Alex", ValueAvailable = true
                };
                var suggestion = observed with
                {
                    Id = "suggested", Label = "Arrival date", State = FieldObservationState.Suggested,
                    BeforeValue = null, AfterValue = null, Kind = FieldKind.Date
                };
                var snapshot = new FieldLearningSnapshot(FieldLearningSnapshot.CurrentFormat, [observed, suggestion]);
                var armed = false;
                var markRequests = 0;
                panel.MarkFieldRequested += () => { markRequests++; armed = !armed; panel.SetFieldMarking(armed); };
                var decisions = new List<(string, FieldReviewDecision)>();
                panel.FieldDecisionChanged += (id, decision) => decisions.Add((id, decision));
                panel.SetFieldObservations(snapshot);
                panel.ShowActiveRecordingState();
                Await(() => window.Dispatcher.Invoke(() => FindButton(window, "AtlasRecorderMarkField").IsEnabled));
                window.Dispatcher.Invoke(() => Click(FindButton(window, "AtlasRecorderMarkField")));
                Await(() => window.Dispatcher.Invoke(() => Equals(FindButton(window, "AtlasRecorderMarkField").Content, "Cancel marking")));
                window.Dispatcher.Invoke(() => Click(FindButton(window, "AtlasRecorderMarkField")));
                Await(() => markRequests == 2 && !armed);

                panel.ShowPausedRecordingState();
                var review = panel.ShowFieldReview();
                Await(() => window.Dispatcher.Invoke(() => FindButton(window, "AtlasRecorderKeepFields").IsVisible));
                window.Dispatcher.Invoke(() =>
                {
                    window.UpdateLayout();
                    Assert.False(FindButton(window, "AtlasRecorderMarkField").IsVisible);
                    var reviewShell = Field<Border>(panel, "_fieldReviewShell");
                    Assert.Contains(Descendants<TextBlock>(reviewShell), text => text.Text == "Empty → Alex");
                    Assert.DoesNotContain(Descendants<TextBlock>(reviewShell), text => text.Text == "Verified");
                    var checks = Descendants<CheckBox>(reviewShell).Where(box => AutomationProperties.GetAutomationId(box).StartsWith("AtlasFieldReview_", StringComparison.Ordinal)).ToArray();
                    Assert.True(Assert.Single(checks, box => AutomationProperties.GetAutomationId(box).EndsWith(observed.Id, StringComparison.Ordinal)).IsChecked);
                    Assert.False(Assert.Single(checks, box => AutomationProperties.GetAutomationId(box).EndsWith(suggestion.Id, StringComparison.Ordinal)).IsChecked);
                    Click(FindButton(window, "AtlasRecorderKeepFields"));
                });
                Await(() => review.IsCompleted);
                Assert.Contains((observed.Id, FieldReviewDecision.Keep), decisions);
                Assert.Contains((suggestion.Id, FieldReviewDecision.Later), decisions);
                window.Dispatcher.Invoke(() => Assert.Equal(RecordingControlPanel.RecordingPanelMode.Paused,
                    Field<RecordingControlPanel.RecordingPanelMode>(panel, "_currentMode")));

                decisions.Clear();
                var deferred = panel.ShowFieldReview(recordingComplete: true);
                Await(() => window.Dispatcher.Invoke(() => FindButton(window, "AtlasRecorderFieldsLater").IsVisible));
                window.Dispatcher.Invoke(() => Click(FindButton(window, "AtlasRecorderFieldsLater")));
                Await(() => deferred.IsCompleted);
                Assert.Empty(decisions); // Later preserves the observations and existing review choices.
            }
            finally { panel?.Dispose(); targetWindow.Close(); }
        });
    }

    private static Button FindButton(Window window, string id) => Descendants<Button>(window)
        .Single(button => AutomationProperties.GetAutomationId(button) == id);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
