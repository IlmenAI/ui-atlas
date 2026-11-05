using System.IO;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Image = System.Windows.Controls.Image;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UIMaps.Application.Core;

namespace UiAtlas.Shell;

/// <summary>Selects a single application map without changing its visibility or ownership.</summary>
public sealed class UiMapPickerWindow : Window
{
    private readonly MapsLibraryViewModel _model;
    private readonly IMapsLibrarySource _source;
    private readonly Func<MapsLibraryRecord, string, Task> _link;
    private readonly Action<MapsLibraryRecord> _open;
    private readonly Func<MapsLibraryRecord, string> _scope;
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _details = new() { Margin = new Thickness(24) };
    private readonly Button _confirm = new() { Content = "Link UI Map", IsEnabled = false, MinWidth = 130, Padding = new Thickness(20, 10, 20, 10), Background = new SolidColorBrush(Color.FromRgb(79, 54, 224)), Foreground = Brushes.White };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray };
    private int _selection;
    private bool _linking;
    public MapApplicationPresentation? SelectedApplication => _list.SelectedItem as MapApplicationPresentation;
    public UiMapPickerWindow(IMapsLibrarySource source, string organization, Func<MapsLibraryRecord, string, Task> link,
        Action<MapsLibraryRecord> open, Func<MapsLibraryRecord, string> scope)
    {
        _source = source; _model = new(source); _link = link; _open = open; _scope = scope;
        Title = "Link UI Map"; Width = 1180; Height = 790; MinWidth = 960; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brushes.White; FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        var root = new Grid { Margin = new Thickness(30) }; Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel(); heading.Children.Add(new TextBlock { Text = "Link UI Map", FontSize = 32, FontWeight = FontWeights.Light });
        heading.Children.Add(new TextBlock { Text = "Choose an application map for " + organization + ". One map per application.", Foreground = Brushes.SlateGray, Margin = new Thickness(0, 8, 0, 22) }); root.Children.Add(heading);
        var filters = new Grid { Margin = new Thickness(0, 0, 0, 18) }; Grid.SetRow(filters, 1); root.Children.Add(filters);
        filters.ColumnDefinitions.Add(new()); filters.ColumnDefinitions.Add(new() { Width = new GridLength(180) }); filters.ColumnDefinitions.Add(new() { Width = new GridLength(180) });
        var search = new TextBox { Height = 38, Padding = new Thickness(10), ToolTip = "Search applications or executables" }; System.Windows.Automation.AutomationProperties.SetName(search, "Search application maps");
        var searchBox = new Grid(); searchBox.Children.Add(search);
        var hint = new TextBlock { Text = "Search applications or executables", Foreground = Brushes.SlateGray, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false }; searchBox.Children.Add(hint);
        search.TextChanged += (_, _) => { _model.SearchText = search.Text; hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; }; filters.Children.Add(searchBox);
        var status = new ComboBox { ItemsSource = _model.Filters, DisplayMemberPath = "Label", SelectedItem = _model.SelectedFilter, Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(status, 1); filters.Children.Add(status); status.SelectionChanged += (_, _) => _model.SelectedFilter = (MapLibraryFilterOption)status.SelectedItem;
        var sort = new ComboBox { ItemsSource = _model.SortOptions, DisplayMemberPath = "Label", SelectedItem = _model.SelectedSort, Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(sort, 2); filters.Children.Add(sort); sort.SelectionChanged += (_, _) => _model.SelectedSort = (MapLibrarySortOption)sort.SelectedItem;
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new GridLength(1.6, GridUnitType.Star) }); body.ColumnDefinitions.Add(new()); Grid.SetRow(body, 2); root.Children.Add(body);
        _list.Background = Brushes.White;
        _list.ItemContainerStyle = (Style)System.Windows.Markup.XamlReader.Parse("""
<Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ListBoxItem"><Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="Background" Value="White"/><Setter Property="BorderBrush" Value="#DFE6EF"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem"><Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="5" Margin="0,0,0,5"><ContentPresenter/></Border></ControlTemplate></Setter.Value></Setter><Style.Triggers><Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="#EDF5FF"/><Setter Property="BorderBrush" Value="#68A5EF"/></Trigger><Trigger Property="IsMouseOver" Value="True"><Setter Property="BorderBrush" Value="#68A5EF"/></Trigger></Style.Triggers></Style>
""");
        _list.ItemsSource = _model.Groups;
        _list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Border Padding="12,18" BorderBrush="#DFE6EF" BorderThickness="0,0,0,1"><Grid><Grid.ColumnDefinitions><ColumnDefinition Width="26"/><ColumnDefinition Width="52"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions><RadioButton IsHitTestVisible="False" Focusable="False" VerticalAlignment="Top" Margin="0,12,0,0" IsChecked="{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}, Mode=OneWay}"/><Border Grid.Column="1" Width="40" Height="40" CornerRadius="7" Background="{Binding IconColor}" VerticalAlignment="Top"><TextBlock Text="{Binding IconText}" Foreground="White" FontWeight="SemiBold" HorizontalAlignment="Center" VerticalAlignment="Center"/></Border><StackPanel Grid.Column="2"><TextBlock Text="{Binding DisplayName}" FontWeight="SemiBold" FontSize="16" TextWrapping="Wrap"/><TextBlock Text="{Binding ExecutableName}" Foreground="SlateGray" Margin="0,4,0,0"/><TextBlock Text="{Binding RecordingSummary}" Foreground="SlateGray"/><TextBlock Text="{Binding LibraryStatus}" Foreground="{Binding LibraryStatusColor}" Margin="0,5,0,0"/><TextBlock Text="{Binding LastRecorded, StringFormat=Last recorded: {0}}" Foreground="SlateGray" FontSize="12" Margin="0,5,0,0"/><TextBlock Text="{Binding MapUpdated, StringFormat=Map updated: {0}}" Foreground="SlateGray" FontSize="12"/></StackPanel></Grid></Border></DataTemplate>
""");
        body.Children.Add(_list); var detailBorder = new Border { Background = new SolidColorBrush(Color.FromRgb(248, 250, 253)), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Margin = new Thickness(18, 0, 0, 0), Child = new ScrollViewer { Content = _details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }; Grid.SetColumn(detailBorder, 1); body.Children.Add(detailBorder);
        _details.Children.Add(new TextBlock { Text = "Select an application to review its map.", TextWrapping = TextWrapping.Wrap });
        var footer = new Grid { Margin = new Thickness(0, 20, 0, 0) }; footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); Grid.SetRow(footer, 3); root.Children.Add(footer); footer.Children.Add(_message);
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 100, Margin = new Thickness(12, 0, 12, 0) }; Grid.SetColumn(cancel, 1); footer.Children.Add(cancel); Grid.SetColumn(_confirm, 2); footer.Children.Add(_confirm);
        _list.SelectionChanged += async (_, _) => await SelectionAsync();
        _confirm.Click += async (_, _) =>
        {
            if (_linking || SelectedApplication is not { } selected) return;
            _linking = true; _confirm.IsEnabled = false; _list.IsEnabled = false; cancel.IsEnabled = false;
            try { await _link(selected.CurrentRecord, selected.DisplayName); _linking = false; DialogResult = true; }
            catch (Exception error) { _message.Foreground = Brushes.Firebrick; _message.Text = error.GetBaseException().Message; }
            finally { _linking = false; _list.IsEnabled = true; cancel.IsEnabled = true; _confirm.IsEnabled = SelectedApplication is not null; }
        };
        Closing += (_, e) => { if (_linking) e.Cancel = true; };
        Loaded += async (_, _) => { await _model.RefreshAsync(CancellationToken.None); _message.Text = _model.LastRefreshSucceeded ? _model.Groups.Count == 0 ? "No application maps are available to link." : "Select a map to link. Sharing stays unchanged." : _model.Status; };
        Closed += (_, _) => _selection++;
    }
    private async Task SelectionAsync()
    {
        var ticket = ++_selection; _details.Children.Clear(); _confirm.IsEnabled = SelectedApplication is not null && !_linking;
        if (SelectedApplication is not { } app) return;
        void Line(string text, double size = 13) => _details.Children.Add(new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        Line(app.DisplayName, 23); Line("Executable: " + app.ExecutableName); Line("App version: " + app.ProductVersion); Line(_scope(app.CurrentRecord));
        var preview = new Image { Height = 140, Visibility = Visibility.Collapsed, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 14) }; _details.Children.Add(preview);
        var previewNote = new TextBlock { Text = "Loading recorded preview�", Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap }; _details.Children.Add(previewNote);
        Line("Map status: " + app.LibraryStatus); Line("Last recorded: " + app.LastRecorded); Line("Map updated: " + app.MapUpdated); Line(app.RecordingSummary);
        Line("Map revision: " + (string.IsNullOrWhiteSpace(app.CurrentRecord.Entry.LatestMapRevisionRef) ? "Unpublished draft" : app.CurrentRecord.Entry.LatestMapRevisionRef));
        Line(app.Counts);
        var open = new Button { Content = "Open preview", Padding = new Thickness(12, 8, 12, 8), HorizontalAlignment = HorizontalAlignment.Left }; open.Click += (_, _) => { try { _open(app.CurrentRecord); } catch (Exception error) { _message.Foreground = Brushes.Firebrick; _message.Text = error.GetBaseException().Message; } }; _details.Children.Add(open);
        try
        {
            var bytes = await _source.LoadPreviewAsync(app.CurrentRecord, CancellationToken.None);
            if (ticket != _selection) return;
            if (bytes is null) { previewNote.Text = "Recorded screenshot unavailable. Open preview to inspect the map."; return; }
            using var stream = new MemoryStream(bytes); var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); preview.Source = bitmap; preview.Visibility = Visibility.Visible; previewNote.Text = "Screenshot from a source recording";
        }
        catch (Exception) { if (ticket == _selection) previewNote.Text = "Recorded screenshot unavailable. Open preview to inspect the map."; }
    }
}

