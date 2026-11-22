using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UiAtlas.Presentation;

public sealed class TableCaptureDurationPanel : StackPanel
{
    public TableCaptureDurationPanel() : this(new TableCaptureDurationStore()) { }

    public TableCaptureDurationPanel(TableCaptureDurationStore store)
    {
        const string label = "Table capture duration (minutes)";
        var input = new TextBox { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(input, label);
        Children.Add(new Label { Content = label, Target = input, Padding = new(0, 8, 0, 4) });
        Children.Add(input);
        Children.Add(new TextBlock { Text = $"Default is {new TableCaptureDuration().Minutes} minutes per grid, including scroll restoration.",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 8) });
        var save = new Button { Content = "Save capture duration", HorizontalAlignment = HorizontalAlignment.Left, Padding = new(12, 5, 12, 5) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        Children.Add(save);
        Children.Add(status);
        bool dirty = false;
        input.TextChanged += (_, _) => dirty = true;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible || dirty) return;
            try
            {
                input.Text = store.Load().Minutes.ToString(CultureInfo.InvariantCulture);
                dirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException)
            { status.Text = ex.Message; }
        };
        save.Click += (_, _) =>
        {
            if (!int.TryParse(input.Text, out var minutes) || minutes is < 1 or > TableCaptureDuration.MaximumMinutes)
            { status.Text = $"Enter a whole number from 1 to {TableCaptureDuration.MaximumMinutes} minutes."; return; }
            try
            {
                store.Save(new(minutes));
                dirty = false;
                status.Text = "Saved for the next table capture.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = ex.Message; }
        };
    }
}
