using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UiAtlas.Catalog;

namespace UiAtlas.Shell;

public sealed class MapReadinessSettingsPanel : UserControl
{
    public const string SettingLabel = "Require review before marking UI maps Ready";
    private readonly MapReadinessSettingsStore _store;
    private readonly CheckBox _requireReview = new() { Content = SettingLabel, Margin = new(0, 8, 0, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) };

    public MapReadinessSettingsPanel(string? catalogRoot = null)
    {
        _store = new(catalogRoot);
        AutomationProperties.SetName(_requireReview, SettingLabel);
        var panel = new StackPanel { Margin = new(24, 12, 24, 12) };
        panel.Children.Add(new TextBlock { Text = "UI Map readiness", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_requireReview);
        panel.Children.Add(new TextBlock
        {
            Text = "When off, Mark ready automatically accepts pending map items. Validation still runs. This setting is shared by MapLock and UI Atlas.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(_status);
        Content = panel;
        Reload();
        Loaded += (_, _) => Reload();
        _requireReview.Click += (_, _) =>
        {
            try
            {
                _store.Save(new(_requireReview.IsChecked == true));
                _status.Text = "Saved. Applies the next time you mark a map Ready.";
            }
            catch (Exception error)
            {
                Reload();
                _status.Text = "Could not save map readiness settings: " + error.GetBaseException().Message;
            }
        };
    }

    private void Reload()
    {
        try { _requireReview.IsChecked = _store.Load().RequireReview; _status.Text = ""; }
        catch (Exception error)
        {
            _requireReview.IsChecked = true;
            _status.Text = "Could not load map readiness settings: " + error.GetBaseException().Message;
        }
    }
}
