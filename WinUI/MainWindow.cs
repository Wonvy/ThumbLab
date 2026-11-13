#nullable enable
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.System;
using System;
using System.Linq;

namespace ThumbLabWinUI;

internal sealed class MainWindow : Window
{
    private readonly ThumbnailSettings settings = new();
    private readonly FileTypeIcons icons = new();
    private readonly Grid root = (Grid)Microsoft.UI.Xaml.Markup.XamlReader.Load(
        @"<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource ApplicationPageBackgroundThemeBrush}' />");
    private readonly ListView types = new() { SelectionMode = ListViewSelectionMode.Single, IsMultiSelectCheckBoxEnabled = false };
    private readonly TextBox search = new() { PlaceholderText = "搜索扩展名", BorderThickness = new Thickness(0) };
    private static readonly string[] FilterNames = { "有缩略图处理器", "无缩略图处理器", "全部文件类型" };
    private readonly Button typeFilter = new() { Content = new FontIcon { Glyph = "\uE71C", FontSize = 16 }, Width = 40 };
    private int filterIndex;
    private readonly HashSet<string> exportSelection = new(StringComparer.OrdinalIgnoreCase);
    private bool selectingForExport;

    private readonly ScrollViewer scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel content = new() { Spacing = 10, MaxWidth = 980, Padding = new Thickness(28, 22, 28, 28) };
    private readonly InfoBar notice = new() { IsClosable = true };

    private ThumbnailSettings.FileType[] fileTypes = Array.Empty<ThumbnailSettings.FileType>();
    private string extension = ".pdf";
    private Button? inspectSearch;
    private Button? multiExport;
    private TextBlock? exportHint;
    private Button? exportEntry;
    private Button? importEntry;
    private Button? themeEntry;
    private bool filtering;
    private bool loading = true;
    private string currentPage = "manage";

    public MainWindow()
    {
        Title = "ThumbLab";
        Content = root;

        var area = DisplayArea.Primary.WorkArea;
        int width = Math.Min(1120, area.Width);
        int windowHeight = Math.Min(760, area.Height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - windowHeight) / 2, width, windowHeight));
        SetWindowIcon();
        var leftColumn = new ColumnDefinition { Width = new GridLength(315) };
        root.ColumnDefinitions.Add(leftColumn);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sidebar = new Grid { Padding = new Thickness(12, 12, 12, 8), RowSpacing = 7 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            sidebar.RowDefinitions.Add(new RowDefinition { Height = height });
        Place(sidebar, Text("文件类型", 18), 0);
        var filters = new Grid { ColumnSpacing = 8 };
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        search.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        search.Resources["TextControlBorderBrushFocused"] = new SolidColorBrush(Colors.Transparent);
        filters.Children.Add(search);
        Grid.SetColumn(typeFilter, 1);
        filters.Children.Add(typeFilter);
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var view = IconButton(new Button(), "\uE80A", "切换到图标视图");
        Grid.SetColumn(view, 2);
        filters.Children.Add(view);
        bool iconView = false;
        view.Click += (_, _) => Guard(() =>
        {
            iconView = !iconView;
            SetTypeView(iconView);
            SetIcon(view, iconView ? "\uEA37" : "\uE80A", iconView ? "切换到列表视图" : "切换到图标视图");
        });
        Place(sidebar, filters, 1);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(typeFilter, "按缩略图处理器筛选");

        Place(sidebar, types, 3);
        inspectSearch = Button("检查此扩展名", InspectSearch);
        inspectSearch.VerticalAlignment = VerticalAlignment.Top;
        inspectSearch.HorizontalAlignment = HorizontalAlignment.Stretch;
        inspectSearch.Visibility = Visibility.Collapsed;
        Place(sidebar, inspectSearch, 3);
        var leftPane = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            @"<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource LayerFillColorDefaultBrush}' />");
        leftPane.Child = sidebar;
        root.Children.Add(leftPane);
        var splitter = new Border { Background = new SolidColorBrush(Colors.Transparent) };
        Grid.SetColumn(splitter, 1);
        Grid.SetRowSpan(splitter, 2);
        ToolTipService.SetToolTip(splitter, "拖动调整栏宽");
        bool resizing = false;
        void ResizeLeft(double x)
        {
            double max = Math.Max(205, root.ActualWidth - 360);
            leftColumn.Width = new GridLength(Math.Clamp(x, 205, max));
        }
        splitter.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(splitter).Properties.IsLeftButtonPressed) return;
            resizing = splitter.CapturePointer(e.Pointer);
            e.Handled = resizing;
        };
        splitter.PointerMoved += (_, e) =>
        {
            if (resizing) ResizeLeft(e.GetCurrentPoint(root).Position.X);
        };
        splitter.PointerReleased += (_, e) => splitter.ReleasePointerCapture(e.Pointer);
        splitter.PointerCaptureLost += (_, _) => resizing = false;
        root.SizeChanged += (_, _) => ResizeLeft(leftColumn.Width.Value);
        root.Children.Add(splitter);
        scroller.Content = content;
        Grid.SetColumn(scroller, 2);
        root.Children.Add(scroller);
        notice.Margin = new Thickness(18, 4, 18, 12);
        Grid.SetColumn(notice, 2);
        Grid.SetRow(notice, 1);
        root.Children.Add(notice);
        var import = importEntry = IconButton(new Button(), "\uE896", "导入");
        var imports = new MenuFlyout();
        AddMenuAction(imports, "导入配置", ImportAsync);
        AddMenuAction(imports, "导入组件包", ImportPackageAsync);
        import.Flyout = imports;
        var export = IconButton(AsyncButton("", async () =>
        {
            if (selectingForExport) await ExportAsync();
            else ToggleExportSelection();
        }), "\uE898", "导出配置");
        var theme = themeEntry = IconButton(new Button(), "\uE706", "切换主题");
        void UpdateThemeIcon()
        {
            bool dark = root.ActualTheme == ElementTheme.Dark;
            SetIcon(theme, dark ? "\uE706" : "\uE708", dark ? "切换为浅色" : "切换为深色");
        }
        theme.Click += (_, _) =>
        {
            root.RequestedTheme = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        };
        root.ActualThemeChanged += (_, _) => UpdateThemeIcon();
        root.Loaded += (_, _) => UpdateThemeIcon();
        multiExport = Button("取消", ToggleExportSelection);
        exportHint = Text("", 12);
        AddIconLabel(import, "导入");
        AddIconLabel(export, "导出");
        var toolbar = new Grid();
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.Children.Add(theme);
        var transfers = Actions(multiExport, import, export);
        Grid.SetColumn(transfers, 1);
        toolbar.Children.Add(transfers);
        Place(sidebar, toolbar, 5);
        var selectionBar = exportHint;
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(sidebar, selectionBar, 4);
        exportEntry = export;

        UpdateExportLabel();

        foreach (string state in new[] { "Selected", "SelectedPointerOver", "SelectedPressed" })
        {
            types.Resources["ListViewItemBackground" + state] = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 91, 166));
            types.Resources["ListViewItemForeground" + state] = new SolidColorBrush(Colors.White);
        }
        SetTypeView(false);
        types.ContainerContentChanging += ShowTypeIcon;
        SetupFileDrop();
        types.SelectionChanged += (_, e) =>
        {
            if (filtering) return;
            if (selectingForExport)
            {
                foreach (TypeRow removed in e.RemovedItems) exportSelection.Remove(removed.Name);
                foreach (TypeRow added in e.AddedItems) exportSelection.Add(added.Name);
                UpdateExportLabel();
                return;
            }
            if (types.SelectedItem is not TypeRow item) return;
            extension = item.Name;
            Guard(ShowDetails);
        };
        search.TextChanged += (_, _) => Filter();
        search.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter && inspectSearch?.Visibility == Visibility.Visible)
                Guard(InspectSearch);
        };
        var menu = new MenuFlyout();
        var choices = new ToggleMenuFlyoutItem[FilterNames.Length];
        for (int i = 0; i < choices.Length; i++)
        {
            int index = i;
            choices[i] = new ToggleMenuFlyoutItem { Text = FilterNames[i], IsChecked = i == 0 };
            choices[i].Click += (_, _) => SetFilter(index);
            menu.Items.Add(choices[i]);
        }
        typeFilter.Flyout = menu;
        typeFilter.Tag = choices;
        SetFilter(0);
        root.ActualThemeChanged += (_, _) => UpdateTitleBar();
        root.Loaded += (_, _) => UpdateTitleBar();

        Closed += (_, _) => icons.Dispose();
        ShowLoading();
        root.Loaded += AfterLoaded;
    }

    private void AfterLoaded(object sender, RoutedEventArgs args)
    {
        root.Loaded -= AfterLoaded;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, LoadTypes);
    }

    private void ShowLoading()
    {
        Begin("manage", "缩略图管理");
        content.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 180, HorizontalAlignment = HorizontalAlignment.Left });
        content.Children.Add(Text("正在读取文件类型和缩略图设置…", 14));
    }

    private async void LoadTypes()
    {
        string initialType = extension;
        ThumbnailSettings.Details? initial = null;
        try
        {
            initial = await Task.Run(() => settings.Inspect(initialType));
            fileTypes = await Task.Run(ThumbnailSettings.LoadFileTypes);
        }
        catch (Exception ex)
        {
            Tell("加载缩略图设置时遇到问题：" + ex.Message, InfoBarSeverity.Warning);
        }
        finally { loading = false; }

        Filter();
        if (currentPage == "manage")
        {
            if (initial != null) Guard(() => RenderDetails(initial));
            else
            {
                Begin("manage", "缩略图管理");
                content.Children.Add(Actions(Button("重试", () => { loading = true; ShowLoading(); LoadTypes(); })));
            }
        }
    }

    private System.Drawing.Icon? windowIcon;

    private void SetWindowIcon()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "ThumbLab.ico");
        if (System.IO.File.Exists(path))
        {
            AppWindow.SetIcon(path);
            windowIcon = new System.Drawing.Icon(path, 16, 16);
        }
        else windowIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        if (windowIcon == null) return;
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (AppWindowTitleBar.IsCustomizationSupported())
            AppWindow.TitleBar.IconShowOptions = IconShowOptions.ShowIconAndSystemMenu;
        SetNativeIcon(handle, 0x0080, IntPtr.Zero, windowIcon.Handle);
        root.Loaded += (_, _) => SetNativeIcon(handle, 0x0080, IntPtr.Zero, windowIcon.Handle);
        Closed += (_, _) => windowIcon.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SetNativeIcon(IntPtr window, uint message, IntPtr kind, IntPtr icon);

    private MenuFlyout FileTypeMenu(string name)
    {
        var menu = new MenuFlyout();
        AddMenuAction(menu, "查看详情", () =>
        {
            extension = ThumbnailSettings.Normalize(name);
            Filter();
            ShowDetails();
            return Task.CompletedTask;
        });
        AddMenuAction(menu, "复制扩展名", () =>
        {
            DesktopIntegration.CopyText(name);
            Tell("已复制 " + name, InfoBarSeverity.Success);
            return Task.CompletedTask;
        });
        AddMenuAction(menu, selectingForExport && exportSelection.Contains(name) ? "取消勾选" : "加入导出选择", () =>
        {
            if (!selectingForExport)
            {
                ToggleExportSelection();
                exportSelection.Add(name);
            }
            else if (!exportSelection.Remove(name)) exportSelection.Add(name);
            Filter();
            UpdateExportLabel();
            return Task.CompletedTask;
        });
        return menu;
    }

    private void SetTypeView(bool iconView)
    {
        filtering = true;
        try
        {
            types.ItemContainerStyle = iconView
                ? (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    @"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListViewItem'><Setter Property='Padding' Value='4'/><Setter Property='MinWidth' Value='0'/><Setter Property='HorizontalContentAlignment' Value='Stretch'/><Setter Property='VerticalContentAlignment' Value='Center'/></Style>")
                : null;
            types.ItemsPanel = (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(iconView
                ? @"<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsWrapGrid Orientation='Horizontal' ItemWidth='90' ItemHeight='76'/></ItemsPanelTemplate>"
                : @"<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsStackPanel Orientation='Vertical'/></ItemsPanelTemplate>");
            if (iconView)
                types.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    @"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Height='58' RowSpacing='4'><Grid.RowDefinitions><RowDefinition Height='32'/><RowDefinition Height='Auto'/></Grid.RowDefinitions><Image Width='28' Height='28' HorizontalAlignment='Center'/><TextBlock Grid.Row='1' TextAlignment='Center' TextTrimming='CharacterEllipsis'/></Grid></DataTemplate>");
            else
        types.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            @"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid ColumnSpacing='10' MinHeight='27'><Grid.ColumnDefinitions><ColumnDefinition Width='18'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions><Image Width='16' Height='16' VerticalAlignment='Center'/><TextBlock Grid.Column='1' VerticalAlignment='Center' TextTrimming='CharacterEllipsis'/></Grid></DataTemplate>");
        }
        finally { filtering = false; }
        Filter();
        var selected = types.SelectedItems.OfType<TypeRow>().FirstOrDefault(row => row.Name.Equals(extension, StringComparison.OrdinalIgnoreCase));
        if (selected != null) DispatcherQueue.TryEnqueue(() => types.ScrollIntoView(selected));
    }

    private void ShowTypeIcon(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not TypeRow item) return;
        args.ItemContainer.ContextFlyout = FileTypeMenu(item.Name);
        args.RegisterUpdateCallback(async (list, update) =>
        {
            if (update.ItemContainer.ContentTemplateRoot is not Grid grid || grid.Children[0] is not Image image) return;
            if (grid.Children[1] is TextBlock label) label.Text = item.Name;
            image.Source = item.Icon;
            if (item.Icon == null && icons.Get(item.Name, large: image.Width > 16) is { } bitmap)
                try { item.Icon = await DesktopIntegration.ToImageSourceAsync(bitmap); } catch { return; }
            if (ReferenceEquals(update.ItemContainer.Content, item)) image.Source = item.Icon;
        });
    }

    private void SetFilter(int index)
    {
        filterIndex = index;
        if (typeFilter.Tag is ToggleMenuFlyoutItem[] choices)
            for (int i = 0; i < choices.Length; i++) choices[i].IsChecked = i == index;
        string label = "筛选：" + FilterNames[index];
        ToolTipService.SetToolTip(typeFilter, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(typeFilter, label);
        Filter();
    }

    private void Filter()
    {
        filtering = true;
        try
        {
            var rows = fileTypes.Where(item => (filterIndex == 2 || item.HasThumbnail == (filterIndex == 0)) &&
                item.Extension.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(item => new TypeRow(item.Extension)).ToArray();
            if (inspectSearch != null)
            {
                bool canInspect = false;
                if (!loading && rows.Length == 0 && !string.IsNullOrWhiteSpace(search.Text))
                {
                    try { ThumbnailSettings.Normalize(search.Text); canInspect = true; }
                    catch (InvalidOperationException) { }
                }
                inspectSearch.Visibility = canInspect ? Visibility.Visible : Visibility.Collapsed;
            }
            types.ItemsSource = rows;
            if (selectingForExport)
            {
                foreach (var row in rows.Where(row => exportSelection.Contains(row.Name))) types.SelectedItems.Add(row);
            }
            else types.SelectedItem = rows.FirstOrDefault(row => row.Name.Equals(extension, StringComparison.OrdinalIgnoreCase));
        }
        finally { filtering = false; }
    }

    public sealed class TypeRow
    {
        public string Name { get; }
        public ImageSource? Icon { get; set; }
        public TypeRow(string name) { Name = name; }
    }

    private void InspectSearch()
    {
        extension = ThumbnailSettings.Normalize(search.Text);
        var match = fileTypes.FirstOrDefault(item => item.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase));
        if (match != null) SetFilter(match.HasThumbnail ? 0 : 1);
        Filter();
        ShowDetails();
    }

    private void ShowDetails()
    {
        if (loading) { ShowLoading(); return; }
        RenderDetails(settings.Inspect(extension));
    }

    private Func<string, Task>? testCurrentFile;

    private void SetupFileDrop()
    {
        try
        {
            var nativeDrop = new ExplorerFileDrop(WinRT.Interop.WindowNative.GetWindowHandle(this),
                path => DispatcherQueue.TryEnqueue(async () =>
                {
                    if (path == null || !System.IO.File.Exists(path))
                    {
                        Tell("请一次拖入一个本地文件。", InfoBarSeverity.Informational);
                        return;
                    }
                    await TestDroppedFile(path);
                }));
            Closed += (_, _) => nativeDrop.Dispose();
            return;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Use WinUI's drop target if native registration is unavailable.
        }

        root.AllowDrop = true;
        root.AddHandler(UIElement.DragOverEvent, new DragEventHandler(OnFileDragOver), true);
        root.AddHandler(UIElement.DropEvent, new DragEventHandler(OnFileDrop), true);
    }

    private void OnFileDragOver(object sender, DragEventArgs args)
    {
        if (!args.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        args.DragUIOverride.Caption = "测试缩略图";
        args.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs args)
    {
        if (!args.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        args.Handled = true;
        var deferral = args.GetDeferral();
        string? path = null;
        try
        {
            var items = await args.DataView.GetStorageItemsAsync();
            if (items.Count == 1 && items[0] is Windows.Storage.StorageFile file
                && !string.IsNullOrEmpty(file.Path))
            {
                path = file.Path;
                args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            }
            else Tell("请一次拖入一个文件。", InfoBarSeverity.Informational);
        }
        catch (Exception ex) { Tell("无法读取拖入的文件：" + ex.Message, InfoBarSeverity.Warning); }
        finally { deferral.Complete(); }

        if (path != null) await TestDroppedFile(path);
    }

    private async Task TestDroppedFile(string path)
    {
        if (loading || testCurrentFile == null)
        {
            Tell(loading ? "请等待文件类型加载完成，再拖入文件。" : "请先选中文件类型，再拖入文件。", InfoBarSeverity.Informational);
            return;
        }
        await testCurrentFile(path);
    }

    private void RenderDetails(ThumbnailSettings.Details details, string? sample = null)
    {
        Begin("manage", details.Extension, showFileIcon: true);
        var advanced = new StackPanel { Spacing = 8, Padding = new Thickness(0, 8, 0, 0) };
        var picker = new ComboBox { MinWidth = 220, MaxWidth = 470, ItemsSource = details.Providers };
        picker.SelectedItem = details.Providers.FirstOrDefault(item => item.Exists && item.Id != details.Effective?.Id)
            ?? details.Providers.FirstOrDefault();
        var apply = Button("应用处理器", () =>
        {
            if (picker.SelectedItem is not ThumbnailSettings.Provider chosen) return;
            Changed(settings.Apply(extension, chosen), "已应用处理器。");
        }, true);
        apply.IsEnabled = picker.SelectedItem is ThumbnailSettings.Provider { Exists: true };
        var reset = Button("移除用户覆盖", () => Changed(settings.Reset(extension), "已移除用户覆盖。"));
        reset.IsEnabled = details.HasOverride;
        var undo = Button("撤销", () => Changed(settings.Undo(), "已撤销上一次修改。"));
        undo.IsEnabled = settings.CanUndo;


        var locations = new ComboBox { MinWidth = 220, MaxWidth = 460 };
        var open = Button("打开注册表", () =>
        {
            if (locations.SelectedItem is ComboBoxItem { Tag: ThumbnailSettings.Location location }) DesktopIntegration.OpenRegistry(location.Path);
        });
        var copy = Button("复制路径", () =>
        {
            if (locations.SelectedItem is ComboBoxItem { Tag: ThumbnailSettings.Location location }) DesktopIntegration.CopyText(location.Path);
        });
        var locate = Button("定位所选 DLL", () =>
        {
            if (picker.SelectedItem is ThumbnailSettings.Provider item) DesktopIntegration.SelectProviderFile(item.Server);
        });

        void UpdateLocations()
        {
            var options = details.Locations.ToList();
            var provider = picker.SelectedItem as ThumbnailSettings.Provider;
            if (ThumbnailSettings.ProviderLocation(provider) is { } location) options.Add(location);
            locations.ItemsSource = options.Select(location =>
            {
                var item = new ComboBoxItem { Content = location.Label, Tag = location };
                string explanation = location.Label switch
                {
                    "当前用户覆盖" => "查看你自己的 Windows 账户为这种文件选了哪个缩略图组件。",
                    "系统扩展名配置" => "查看这台电脑为这种文件登记的系统级缩略图设置。",
                    "扩展名备用配置" => "查看 Windows 为这种文件额外登记的缩略图设置。",
                    "当前文件类型的配置" => "查看这种文件对应的类型设置里，有没有指定缩略图组件。",
                    "通用文件类型配置" => "查看同类文件共用的缩略图设置，例如一组图片或视频格式。",
                    _ => "查看所选组件的登记信息，包括它从哪个 DLL 文件加载。"
                };
                ToolTipService.SetToolTip(item, new ToolTip { Content = Text(explanation, 13), MaxWidth = 300 });
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(item, explanation);
                return item;
            }).ToArray();
            locations.SelectedIndex = options.Count > 0 ? 0 : -1;
            open.IsEnabled = copy.IsEnabled = options.Count > 0;
            apply.IsEnabled = locate.IsEnabled = provider?.Exists == true;
            locate.Visibility = provider?.Exists == true && provider.Id != details.Effective?.Id
                ? Visibility.Visible : Visibility.Collapsed;

        }
        picker.SelectionChanged += (_, _) => UpdateLocations();
        UpdateLocations();
        picker.HorizontalAlignment = HorizontalAlignment.Stretch;
        picker.MaxWidth = double.PositiveInfinity;
        locations.HorizontalAlignment = HorizontalAlignment.Stretch;
        locations.MaxWidth = double.PositiveInfinity;
        var processor = Card("处理器", null);
        processor.Children.Add(picker);
        processor.Children.Add(Actions(apply, reset, locate));
        advanced.Children.Add(processor);

        var registry = Card("注册表", null);
        registry.Children.Add(locations);
        registry.Children.Add(Actions(open, copy));
        advanced.Children.Add(registry);

        advanced.Children.Add(Card("诊断", null,
            Button("复制诊断信息", () => DesktopIntegration.CopyText(details.DiagnosticText))));

        var testStatus = Text("尚未测试", 13);
        content.Children.Add(testStatus);
        string? lastSample = sample;
        var image = new Image { Height = 160, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        var result = Text("", 12);
        bool testing = false;
        StackPanel? previewCard = null;
        async Task TestPath(string path)
        {
            if (testing || testCurrentFile != TestPath) return;
            testing = true;
            try
            {
                previewCard?.StartBringIntoView();
                if (!System.IO.File.Exists(path))
                {
                    result.Text = "找不到该文件，或当前无法访问。";
                    return;
                }
                var suffix = System.IO.Path.GetExtension(path);
                if (string.IsNullOrEmpty(suffix))
                {
                    result.Text = "此文件没有扩展名。请选择带扩展名的文件。";
                    return;
                }
                if (!suffix.Equals(details.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    var target = ThumbnailSettings.Normalize(suffix);
                    var prompt = new ContentDialog
                    {
                        XamlRoot = root.XamlRoot,
                        Title = "切换文件类型？",
                        Content = Text($"这个文件是 {target}，当前查看的是 {details.Extension}。", 14),
                        PrimaryButtonText = "切换并测试",
                        CloseButtonText = "取消"
                    };
                    if (await prompt.ShowAsync() != ContentDialogResult.Primary || testCurrentFile != TestPath) return;
                    var targetDetails = settings.Inspect(target);
                    extension = target;
                    search.Text = "";
                    SetFilter(2);
                    RenderDetails(targetDetails, path);
                    return;
                }

                lastSample = path;
                testStatus.Text = "正在测试…";
                result.Text = "正在生成缩略图…";
                image.Visibility = Visibility.Collapsed;
                using var bitmap = await Task.Run(() => ThumbnailShell.Extract(path, true));
                image.Source = await DesktopIntegration.ToImageSourceAsync(bitmap);
                image.Visibility = Visibility.Visible;
                testStatus.Text = "已生成缩略图";
                result.Text = $"{System.IO.Path.GetFileName(path)}  ·  {bitmap.Width} × {bitmap.Height} 像素";
            }
            catch (Exception ex)
            {
                testStatus.Text = "这份文件生成失败";
                result.Text = $"无法生成缩略图（0x{ex.HResult:X8}）。";
            }
            finally { testing = false; }
        }

        var test = AsyncButton("选择文件测试", async () =>
        {
            var file = await DesktopIntegration.PickTestFileAsync(this);
            if (file != null) await TestPath(file);
        });
        previewCard = Card("缩略图测试", "把一个文件拖进窗口，或选择文件测试。");
        var repair = AsyncButton("尝试修复", () => GuardRepair(details.Extension, details.IconsOnly, details.Effective, details.Providers, () => lastSample, TestPath), true);
        undo.Content = "撤销上次修改";
        previewCard.Children.Add(result);
        previewCard.Children.Add(image);
        previewCard.Children.Add(Actions(test, repair, undo));
        content.Children.Add(previewCard);
        testCurrentFile = TestPath;
        if (sample != null) _ = TestPath(sample);

        var current = details.Effective;
        var file = current?.Server;
        var info = Card("当前配置", null);
        var facts = new Grid { ColumnSpacing = 14, RowSpacing = 8 };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddFact(facts, "缩略图处理器", current?.Name ?? "未找到");
        Action? openDll = current?.Exists == true && !string.IsNullOrEmpty(file)
            ? () => DesktopIntegration.SelectProviderFile(file) : null;
        AddFact(facts, "DLL 位置", string.IsNullOrWhiteSpace(file) ? "未找到" : file, openDll);
        AddFact(facts, "组件状态", current == null ? "无法确认" : current.Exists ? "文件已找到" : "找不到组件文件");
        AddFact(facts, "显示设置", details.IconsOnly ? "仅显示文件图标" : "允许显示缩略图");
        AddFact(facts, "用户覆盖", details.HasOverride ? "已设置" : "未设置");
        info.Children.Add(facts);
        content.Children.Add(info);

        content.Children.Add(Text("高级设置", 18));
        content.Children.Add(advanced);
    }

    private void AddFact(Grid grid, string label, string value, Action? open = null)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(grid, Text(label, 13), row);
        var text = Text(value, 13);
        FrameworkElement field = text;
        if (open != null)
        {
            var link = new Microsoft.UI.Xaml.Documents.Hyperlink();
            link.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = value });
            link.Click += (_, _) => Guard(open);
            text.Text = "";
            text.Inlines.Add(link);
            ToolTipService.SetToolTip(text, "打开文件夹并选中 DLL");
        }
        else text.IsTextSelectionEnabled = true;
        Grid.SetColumn(field, 1);
        Place(grid, field, row);
    }

    private async Task GuardRepair(string type, bool iconsOnly, ThumbnailSettings.Provider? current, ThumbnailSettings.Provider[] candidates, Func<string?> sample, Func<string, Task> owner)
    {
        try
        {
            var alternatives = candidates.Where(item => item.Exists && item.Id != current?.Id).ToArray();
            if (!iconsOnly && alternatives.Length != 1)
            {
                Tell(alternatives.Length == 0
                    ? "本机没有找到可替换的缩略图组件。可以先选一个文件测试。"
                    : "本机有多个可用组件。请在高级设置里选择要使用的组件。", InfoBarSeverity.Informational);
                return;
            }
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot,
                Title = "修复缩略图",
                Content = Text(iconsOnly ? "允许资源管理器显示缩略图。这项设置对所有文件类型生效。"
                    : "尝试使用本机另一个可用组件来显示 " + type + " 文件的缩略图。", 14),
                PrimaryButtonText = "修复",
                CloseButtonText = "取消"
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || testCurrentFile != owner) return;
            bool changed = iconsOnly ? settings.EnableThumbnails() : settings.Apply(type, alternatives[0]);
            if (!changed)
            {
                Tell("配置无需更新。", InfoBarSeverity.Informational);
                return;
            }
            var previous = sample();
            RenderDetails(settings.Inspect(type), previous);
            Tell(previous == null ? "已更新设置。请选择文件验证。" : "已更新设置，正在用同一文件复测。", InfoBarSeverity.Informational);
        }
        catch (Exception ex) { Tell(ex.Message, InfoBarSeverity.Warning); }
    }

    private async Task ImportPackageAsync()
    {
        var path = await DesktopIntegration.PickPackageAsync(this);
        if (path == null) return;
        using var package = await Task.Run(() => ProviderPackage.Prepare(path));
        var dialog = new ContentDialog
        {
            Title = "导入缩略图组件包",
            XamlRoot = root.XamlRoot,
            Content = new ScrollViewer { MaxHeight = 360, Content = Text(package.Summary, 14) },
            PrimaryButtonText = "安装并应用",
            CloseButtonText = "取消"
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        bool changed = package.Install(settings.ApplyPackage);
        ShowDetails();
        Tell(changed ? "已应用组件关联。请选一个样本文件测试。" : "组件关联无需更新。", InfoBarSeverity.Success);
        fileTypes = await Task.Run(ThumbnailSettings.LoadFileTypes);
        Filter();
    }

    private void ToggleExportSelection()
    {
        selectingForExport = !selectingForExport;
        exportSelection.Clear();
        if (selectingForExport) exportSelection.Add(extension);
        filtering = true;
        try { types.SelectionMode = selectingForExport ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single; }
        finally { filtering = false; }
        Filter();
        if (selectingForExport)
        {
            if (types.SelectedItems.Count == 0 && fileTypes.Any(item => item.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase)))
            {
                search.Text = "";
                SetFilter(2);
            }
            if (types.SelectedItems.Count > 0)
            {
                var selected = types.SelectedItems[0];
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (selectingForExport && types.SelectedItems.Contains(selected))
                        types.ScrollIntoView(selected, ScrollIntoViewAlignment.Leading);
                });
            }
        }
        UpdateExportLabel();
    }

    private void UpdateExportLabel()
    {
        if (importEntry != null)
            importEntry.Visibility = selectingForExport ? Visibility.Collapsed : Visibility.Visible;
        if (themeEntry != null)
            themeEntry.Visibility = selectingForExport ? Visibility.Collapsed : Visibility.Visible;
        if (exportEntry != null)
        {
            string label = selectingForExport ? "保存配置" : "导出";
            SetIcon(exportEntry, "\uE898", label);
            AddIconLabel(exportEntry, label);
        }
        if (exportHint != null)
        {
            exportHint.Text = $"已选 {exportSelection.Count} 项。勾选文件类型后，点击「保存配置」。";
            exportHint.Visibility = selectingForExport ? Visibility.Visible : Visibility.Collapsed;
        }
        if (multiExport != null)
        {
            multiExport.Content = "取消";
            multiExport.Visibility = selectingForExport ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static Button IconButton(Button button, string glyph, string label)
    {
        button.Width = 40;
        SetIcon(button, glyph, label);
        return button;
    }

    private static void AddIconLabel(Button button, string label)
    {
        var icon = (FontIcon)button.Content;
        button.Content = null;
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        caption.Children.Add(icon);
        caption.Children.Add(new TextBlock { Text = label });
        button.ClearValue(FrameworkElement.WidthProperty);
        button.Content = caption;
    }

    private static void SetIcon(Button button, string glyph, string label)
    {
        button.Content = new FontIcon { Glyph = glyph, FontSize = 16 };
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
    }

    private void AddMenuAction(MenuFlyout menu, string text, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += async (_, _) =>
        {
            item.IsEnabled = false;
            try { await action(); }
            catch (Exception ex) { Tell(ex.Message, InfoBarSeverity.Warning); }
            finally { item.IsEnabled = true; }
        };
        menu.Items.Add(item);
    }

    private async Task ExportAsync()
    {
        var selection = selectingForExport ? exportSelection.ToArray() : null;
        if (selection is { Length: 0 })
        {
            Tell("请先在左栏勾选要导出的文件类型。", InfoBarSeverity.Informational);
            return;
        }
        var path = await DesktopIntegration.PickExportFileAsync(this);
        if (path == null) return;
        int count = settings.Export(path, selection);
        int skipped = (selection?.Length ?? count) - count;
        if (selectingForExport) ToggleExportSelection();
        Tell(skipped == 0 ? $"已导出 {count} 项配置。" : $"已导出 {count} 项，跳过 {skipped} 个没有可用处理器的文件类型。", InfoBarSeverity.Success);
    }

    private async Task ImportAsync()
    {
        var path = await DesktopIntegration.PickImportFileAsync(this);
        if (path == null) return;
        var plan = settings.PrepareImport(path);
        var dialog = new ContentDialog { Title = "导入配置", XamlRoot = root.XamlRoot,
            Content = new ScrollViewer { MaxHeight = 330, Content = Text(plan.Summary, 14) },
            CloseButtonText = plan.ChangeCount > 0 ? "取消" : "关闭" };
        if (plan.ChangeCount > 0) dialog.PrimaryButtonText = "应用";
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && settings.CommitImport(plan))
        {
            ShowDetails();
            Tell($"已导入 {plan.ChangeCount} 项，跳过 {plan.SkippedCount} 项。可在右栏撤销。", InfoBarSeverity.Success);
            fileTypes = await Task.Run(ThumbnailSettings.LoadFileTypes);
            Filter();
        }
    }

    private void UpdateTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        var title = AppWindow.TitleBar;
        bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        bool dark = root.ActualTheme == ElementTheme.Dark;
        Windows.UI.Color? background = highContrast ? null : dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        Windows.UI.Color? foreground = highContrast ? null : dark ? Colors.White : Colors.Black;
        Windows.UI.Color? hover = highContrast ? null : dark ? Windows.UI.Color.FromArgb(255, 55, 55, 55) : Windows.UI.Color.FromArgb(255, 221, 221, 221);

        title.BackgroundColor = title.InactiveBackgroundColor = title.ButtonBackgroundColor = title.ButtonInactiveBackgroundColor = background;
        title.ForegroundColor = title.InactiveForegroundColor = title.ButtonForegroundColor = title.ButtonInactiveForegroundColor = foreground;
        title.ButtonHoverBackgroundColor = title.ButtonPressedBackgroundColor = hover;
        title.ButtonHoverForegroundColor = title.ButtonPressedForegroundColor = foreground;
    }

    private void Begin(string page, string title, bool showFileIcon = false)
    {
        bool reset = currentPage != page;
        currentPage = page;
        content.Children.Clear();
        var heading = Text(title, 29);
        if (showFileIcon)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var icon = new Image { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            header.Children.Add(icon);
            header.Children.Add(heading);
            content.Children.Add(header);
            LoadHeadingIcon(title, icon);
        }
        else content.Children.Add(heading);
        if (reset) scroller.ChangeView(null, 0, null, true);
    }

    private async void LoadHeadingIcon(string extension, Image icon)
    {
        try
        {
            if (icons.Get(extension, large: true) is not { } bitmap) return;
            icon.Source = await DesktopIntegration.ToImageSourceAsync(bitmap);
            icon.Visibility = Visibility.Visible;
        }
        catch (Exception) { /* The extension remains visible when Windows cannot supply its icon. */ }
    }

    private void Changed(bool changed, string message)
    {
        ShowDetails();
        Tell(changed ? message : "配置无需更新。", changed ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
    }

    private void Tell(string message, InfoBarSeverity severity)
    {
        notice.Severity = severity; notice.Message = message; notice.IsOpen = true;
    }

    private void Guard(Action action)
    {
        try { action(); } catch (Exception ex) { Tell(ex.Message, InfoBarSeverity.Warning); }
    }

    private Button Button(string title, Action action, bool primary = false)
    {
        var button = new Button { Content = title };
        if (primary && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style)) button.Style = (Style)style;
        button.Click += (_, _) => Guard(action);
        return button;
    }

    private Button AsyncButton(string title, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = title };
        if (primary && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style)) button.Style = (Style)style;
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            catch (Exception ex) { Tell(ex.Message, InfoBarSeverity.Warning); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private Button Nav(string title, string glyph, Action action)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        stack.Children.Add(new FontIcon { Glyph = glyph, FontSize = 15 });
        stack.Children.Add(Text(title, 14));
        var button = Button(title, action);
        button.Content = stack;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title);
        return button;
    }

    private static StackPanel Card(string title, string? description, params UIElement[] controls)
    {
        var card = (StackPanel)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            @"<StackPanel xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Padding='16,12' Spacing='8' CornerRadius='8' BorderThickness='1' Background='{ThemeResource CardBackgroundFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' />");
        card.Children.Add(Text(title, 16));
        if (!string.IsNullOrWhiteSpace(description)) card.Children.Add(Text(description, 13));
        if (controls.Length > 0) card.Children.Add(Actions(controls));
        return card;
    }


    private static ActionRow Actions(params UIElement[] controls)
    {
        var row = new ActionRow();
        foreach (var control in controls)
        {
            if (control is Control field) field.MinHeight = 34;
            row.Children.Add(control);
        }
        return row;
    }

    private sealed class ActionRow : Panel
    {
        private const double Gap = 8;

        protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available) =>
            Layout(available.Width, true);

        protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
        {
            Layout(final.Width, false);
            return final;
        }

        private Windows.Foundation.Size Layout(double width, bool measure)
        {
            double y = 0, widest = 0;
            var row = new List<UIElement>();
            double used = 0, height = 0;
            void FinishRow()
            {
                if (row.Count == 0) return;
                if (!measure)
                {
                    double buttonWidth = row.Where(item => item is Button).Sum(item => Math.Min(width, item.DesiredSize.Width));
                    int buttonCount = row.Count(item => item is Button);
                    double x = 0;
                    double right = width - buttonWidth - Math.Max(0, buttonCount - 1) * Gap;
                    foreach (var item in row)
                    {
                        double itemWidth = Math.Min(width, item.DesiredSize.Width);
                        bool button = item is Button;
                        item.Arrange(new Windows.Foundation.Rect(button ? right : x, y + (height - item.DesiredSize.Height) / 2, itemWidth, item.DesiredSize.Height));
                        if (button) right += itemWidth + Gap;
                        else x += itemWidth + Gap;
                    }
                }
                widest = Math.Max(widest, used);
                y += height + Gap;
                row.Clear();
                used = height = 0;
            }

            foreach (var child in Children)
            {
                if (child.Visibility == Visibility.Collapsed) continue;
                if (measure) child.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
                double itemWidth = Math.Min(width, child.DesiredSize.Width);
                if (row.Count > 0 && used + Gap + itemWidth > width) FinishRow();
                if (row.Count > 0) used += Gap;
                row.Add(child);
                used += itemWidth;
                height = Math.Max(height, child.DesiredSize.Height);
            }
            FinishRow();
            return new Windows.Foundation.Size(widest, Math.Max(0, y - Gap));
        }
    }

    private static TextBlock Text(string text, double size) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
    private static void Place(Grid grid, FrameworkElement child, int row) { Grid.SetRow(child, row); grid.Children.Add(child); }
}
