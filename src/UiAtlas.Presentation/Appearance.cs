using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace UiAtlas.Presentation;

/// <summary>WPF-only appearance owner used by the public Atlas and private MapLock hosts.</summary>
public static class Appearance
{
    private static readonly object Gate = new();
    private static AppearancePreference _current = new AppearancePreferenceStore().Load();
    private static readonly ConditionalWeakTable<Window, WindowAppearance> Windows = new();
    private static readonly ConditionalWeakTable<Dispatcher, DispatcherAppearance> Dispatchers = new();
    private static readonly ConcurrentDictionary<(Type Type, string Name), DependencyProperty> Properties = new();
    internal static event Action<AppearancePreference>? Changed;
    private static bool _registered;
    public static AppearancePreference Current { get { lock (Gate) return _current; } }

    public static void Initialize(Application? application = null)
    {
        var dispatcher = application?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        dispatcher.VerifyAccess();
        lock (Gate)
        {
            if (!_registered)
            {
                EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                    new RoutedEventHandler((sender, _) => Apply((Window)sender)));
                _registered = true;
            }
        }
        Dispatchers.GetValue(dispatcher, d => new DispatcherAppearance(d, application));
    }

    public static void SetPreference(AppearancePreference preference)
    {
        var validated = preference.Validated();
        new AppearancePreferenceStore().Save(validated);
        Publish(validated);
    }

    public static void Refresh() => Publish(new AppearancePreferenceStore().Load());

    private static void Publish(AppearancePreference preference)
    {
        Action<AppearancePreference>? handlers;
        lock (Gate)
        {
            if (_current == preference) return;
            _current = preference;
            handlers = Changed;
        }
        handlers?.Invoke(preference);
    }

    public static void Apply(Window window)
    {
        window.Dispatcher.VerifyAccess();
        Initialize();
        Windows.GetValue(window, w => new WindowAppearance(w));
    }

    /// <summary>Use for code-built controls; resource expressions keep their existing instance and state alive.</summary>
    public static T Bind<T>(T element, params (string Property, string Resource)[] resources) where T : DependencyObject
    {
        foreach (var (property, resource) in resources)
        {
            var dependencyProperty = Properties.GetOrAdd((element.GetType(), property), key =>
                DependencyPropertyDescriptor.FromName(key.Name, key.Type, key.Type)?.DependencyProperty
                ?? throw new ArgumentException($"{key.Type.Name}.{key.Name} is not a dependency property."));
            if (element is FrameworkElement visual) visual.SetResourceReference(dependencyProperty, resource);
            else if (element is FrameworkContentElement content) content.SetResourceReference(dependencyProperty, resource);
            else throw new ArgumentException($"{element.GetType().Name} does not support resource references.");
        }
        return element;
    }

    public static ResourceDictionary CreateResources(AppearancePreference preference, bool? highContrast = null)
    {
        var value = preference.Validated();
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.{(value.ColorMode == "dark" ? "Dark" : "Light")}.xaml") });
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/UiAtlas.Presentation;component/Themes/{value.ThemeId}.{value.ColorMode}.xaml") });
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/UiAtlas.Presentation;component/Controls.xaml") });
        if (highContrast ?? SystemParameters.HighContrast)
        {
            foreach (var key in new[] { "CanvasBrush", "PanelBrush", "SidebarBrush", "InputBrush", "SoftBrush", "ButtonBrush", "ChromeBrush", "DisabledFillBrush", "SuccessSurfaceBrush", "WarningSurfaceBrush", "ErrorSurfaceBrush", "InfoSurfaceBrush" }) resources[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "InkBrush", "ChromeInkBrush", "LineBrush", "SuccessBrush", "WarningBrush", "ErrorBrush", "InfoBrush", "RawEvidenceBrush", "SemanticEvidenceBrush", "AtlasEvidenceBrush" }) resources[key] = SystemColors.WindowTextBrush;
            foreach (var key in new[] { "MutedInkBrush", "DisabledInkBrush" }) resources[key] = SystemColors.GrayTextBrush;
            foreach (var key in new[] { "AccentBrush", "AccentSelectionBrush", "SelectionBorderBrush", "FocusBrush", "HoverBrush", "PressedBrush", "AccentHoverBrush", "AccentPressedBrush" }) resources[key] = SystemColors.HighlightBrush;
            resources["AccentTextBrush"] = SystemColors.HighlightTextBrush;
            resources["SelectionInkBrush"] = SystemColors.HighlightTextBrush;
            resources["WindowCloseHoverBrush"] = SystemColors.HighlightBrush;
            resources["WindowClosePressedBrush"] = SystemColors.HighlightBrush;
            resources["WindowCloseInkBrush"] = SystemColors.HighlightTextBrush;
            resources["WindowShadow"] = new System.Windows.Media.Effects.DropShadowEffect { Opacity = 0 };
        }
        // Native templates that use system resource keys must follow the same palette.
        resources[SystemColors.WindowBrushKey] = resources["InputBrush"];
        resources[SystemColors.WindowTextBrushKey] = resources["InkBrush"];
        resources[SystemColors.ControlBrushKey] = resources["PanelBrush"];
        resources[SystemColors.ControlTextBrushKey] = resources["InkBrush"];
        resources[SystemColors.GrayTextBrushKey] = resources["DisabledInkBrush"];
        resources[SystemColors.HighlightBrushKey] = resources["AccentSelectionBrush"];
        resources[SystemColors.HighlightTextBrushKey] = resources["SelectionInkBrush"];
        return resources;
    }

    private static void Replace(ResourceDictionary target, ref ResourceDictionary? previous, AppearancePreference value)
    {
        var next = CreateResources(value);
        if (previous is null) target.MergedDictionaries.Add(next);
        else
        {
            // Keep style/template identity: rebuilding an editable ComboBox's template
            // would replace its focused text editor even though its value survives.
            // This also includes Fluent's container templates: replacing a UserControl
            // template raises Unloaded/Loaded and can reload a business workspace.
            PreserveTemplates(previous.MergedDictionaries[0], next.MergedDictionaries[0]);
            next.MergedDictionaries[2] = previous.MergedDictionaries[2];
            target.MergedDictionaries[target.MergedDictionaries.IndexOf(previous)] = next;
        }
        previous = next;
    }

    private static void PreserveTemplates(ResourceDictionary previous, ResourceDictionary next)
    {
        // Merged dictionaries resolve in order, with local keys taking precedence.
        // Keep only styles/templates; the new mode's color resources still replace
        // the old values and are resolved by the retained dynamic expressions.
        foreach (var merged in previous.MergedDictionaries) PreserveTemplates(merged, next);
        foreach (var key in previous.Keys)
            if (previous[key] is Style or FrameworkTemplate) next[key] = previous[key];
    }

    private sealed class WindowAppearance
    {
        private readonly Window _window;
        private ResourceDictionary? _resources;
        private bool _closed;
        public WindowAppearance(Window window)
        {
            _window = window;
            Replace(window.Resources, ref _resources, Current);
            Bind(window, ("FontFamily", "BodyFontFamily"), ("Foreground", "InkBrush"));
            if (window.ReadLocalValue(Window.BackgroundProperty) == DependencyProperty.UnsetValue)
                window.SetResourceReference(Window.BackgroundProperty, "CanvasBrush");
            Changed += Update;
            SystemParameters.StaticPropertyChanged += SystemChanged;
            window.Closed += Closed;
            window.Activated += Activated;
            window.SourceInitialized += SourceInitialized;
            UpdateCaption();
        }
        private void SourceInitialized(object? sender, EventArgs e) => UpdateCaption();
        private void UpdateCaption()
        {
            var handle = new WindowInteropHelper(_window).Handle;
            if (handle == IntPtr.Zero) return;
            // DWM owns the standard caption. Custom WPF title bars use theme resources.
            // Older Windows versions may reject this attribute and keep their native fallback.
            var dark = !SystemParameters.HighContrast && Current.ColorMode == "dark" ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        private void Activated(object? sender, EventArgs e) => Refresh();
        private void SystemChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast)) Update(Current);
        }
        private void Update(AppearancePreference value)
        {
            if (_closed || _window.Dispatcher.HasShutdownStarted) return;
            if (!_window.Dispatcher.CheckAccess()) { _window.Dispatcher.BeginInvoke(() => Update(value)); return; }
            // Read the latest value in case queued broadcasts arrived out of order.
            Replace(_window.Resources, ref _resources, Current);
            UpdateCaption();
        }
        private void Closed(object? sender, EventArgs e)
        {
            _closed = true;
            Changed -= Update;
            SystemParameters.StaticPropertyChanged -= SystemChanged;
            _window.Closed -= Closed;
            _window.Activated -= Activated;
            _window.SourceInitialized -= SourceInitialized;
            Windows.Remove(_window);
        }
    }

    private sealed class DispatcherAppearance
    {
        private readonly Dispatcher _dispatcher;
        private readonly Application? _application;
        private readonly DispatcherTimer _refresh;
        private ResourceDictionary? _resources;
        public DispatcherAppearance(Dispatcher dispatcher, Application? application)
        {
            _dispatcher = dispatcher;
            _application = application;
            if (application is not null) Replace(application.Resources, ref _resources, Current);
            Changed += Update;
            SystemParameters.StaticPropertyChanged += SystemChanged;
            // Poll atomic preferences rather than watching transient rename events. No workspace reload.
            _refresh = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Refresh(), dispatcher);
            dispatcher.ShutdownStarted += Shutdown;
        }
        private void SystemChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast)) Update(Current);
        }
        private void Update(AppearancePreference value)
        {
            if (_application is null || _dispatcher.HasShutdownStarted) return;
            if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => Update(value)); return; }
            Replace(_application.Resources, ref _resources, Current);
        }
        private void Shutdown(object? sender, EventArgs e)
        {
            _refresh.Stop();
            Changed -= Update;
            SystemParameters.StaticPropertyChanged -= SystemChanged;
            _dispatcher.ShutdownStarted -= Shutdown;
        }
    }
}
