using Microsoft.Win32;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed partial class ThumbLab
{
    private const string AppSettings = @"Software\ThumbLab";
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private readonly ComboBox appearance = Picker();
    private readonly ComboBox registryLocations = Picker();
    private readonly Label previewEmpty = new Label { Text = "选择一个文件以查看缩略图", TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill, Tag = "muted" };
    private readonly Panel pageHost = new Panel { Dock = DockStyle.Fill };
    private readonly Panel[] pages = new Panel[3];
    private readonly Button[] navigation = new Button[3];
    private readonly ThumbTypeList fileTypes = new ThumbTypeList { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, AccessibleName = "文件类型列表", Tag = "rail" };
    private readonly TextBox typeSearch = new TextBox { Dock = DockStyle.Top, AccessibleName = "搜索文件扩展名" };
    private readonly CheckBox allTypes = new CheckBox { Text = "显示全部已注册类型", Dock = DockStyle.Top, Height = 28, Tag = "rail" };
    private readonly Label currentType = new Label { AutoSize = true, Tag = "muted" };
    private readonly FileTypeIcons typeIcons = new FileTypeIcons();
    private string[] registeredTypes;
    private HashSet<string> thumbnailTypes;
    private bool updatingTypes;
    private int currentPage;
    private Color ink, muted, surface, backdrop, rail, border, hover, accent, selection;

    private void BuildInterface(out Button undoButton, out Button enableButton)
    {
        Text = "ThumbLab 设置";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 800);
        MinimumSize = new Size(800, 550);

        var frame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 232));
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(frame);
        var sidebar = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 12, 10, 12), Margin = Padding.Empty, Tag = "rail" };
        frame.Controls.Add(sidebar, 0, 0);
        var nav = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, Tag = "rail", Margin = Padding.Empty };
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var types = new Panel { Dock = DockStyle.Fill, Tag = "rail", Padding = new Padding(6, 6, 4, 0) };
        sidebar.Controls.Add(types);
        sidebar.Controls.Add(nav);

        string[] names = { "缩略图管理", "导入与导出", "外观设置" };
        string[] icons = { "\uE91B", "\uE8B7", "\uE713" };
        for (int index = 0; index < names.Length; index++)
        {
            int page = index;
            var button = ActionButton(names[index], () => SwitchPage(page), "nav");
            button.Dock = DockStyle.Top;
            button.AutoSize = false;
            button.Height = 38;
            RoundCorners(button, 5);
            button.Margin = new Padding(0, 0, 0, 4);
            button.Paint += (sender, args) =>
            {
                using (var brush = new SolidBrush(currentPage == page ? accent : ink))
                using (var glyph = new Font("Segoe MDL2 Assets", 12))
                {
                    if (currentPage == page) args.Graphics.FillRectangle(brush, 2, 10, 3, button.Height - 20);
                    args.Graphics.DrawString(icons[page], glyph, brush, 13, (button.Height - 21) / 2);
                }
            };
            navigation[index] = button;
            if (index == 2)
            {
                var separator = new Panel { Dock = DockStyle.Top, Height = 1, Tag = "divider", Margin = new Padding(0, 8, 0, 8) };
                nav.Controls.Add(separator);
            }
            nav.Controls.Add(button);
        }

        var listArea = new Panel { Dock = DockStyle.Fill, Tag = "rail" };
        listArea.Controls.Add(fileTypes);
        listArea.Controls.Add(new SlimScroll(() => fileTypes.Items.Count,
            () => Math.Max(1, fileTypes.ClientSize.Height / fileTypes.ItemHeight),
            () => fileTypes.Items.Count == 0 ? 0 : fileTypes.TopIndex,
            index => { if (fileTypes.Items.Count > 0) fileTypes.TopIndex = index; }) { Tag = "rail" });
        types.Controls.Add(listArea);
        FormClosed += (sender, args) => typeIcons.Dispose();
        types.Controls.Add(allTypes);
        types.Controls.Add(typeSearch);
        types.Controls.Add(new Label { Text = "文件类型", Dock = DockStyle.Top, Height = 27, Tag = "rail" });
        fileTypes.DrawMode = DrawMode.OwnerDrawFixed;
        fileTypes.ItemHeight = 29;
        fileTypes.DrawItem += (sender, args) =>
        {
            bool selected = (args.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(selected ? selection : rail))
                args.Graphics.FillRectangle(brush, args.Bounds);
            if (args.Index >= 0)
            {
                string item = (string)fileTypes.Items[args.Index];
                int size = Math.Max(16, args.Bounds.Height * 3 / 5);
                var icon = typeIcons.Get(item);
                if (icon != null) args.Graphics.DrawImage(icon, new Rectangle(args.Bounds.X + 10, args.Bounds.Y + (args.Bounds.Height - size) / 2, size, size));
                if (selected)
                    using (var mark = new SolidBrush(accent)) args.Graphics.FillRectangle(mark, args.Bounds.X + 2, args.Bounds.Y + 6, 3, args.Bounds.Height - 12);
                var textBounds = new Rectangle(args.Bounds.X + size + 17, args.Bounds.Y, Math.Max(0, args.Bounds.Width - size - 18), args.Bounds.Height);
                TextRenderer.DrawText(args.Graphics, item, Font, textBounds, ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            args.DrawFocusRectangle();
        };
        fileTypes.SelectedIndexChanged += (sender, args) =>
        {
            if (updatingTypes || fileTypes.SelectedItem == null) return;
            Run(() =>
            {
                extension.Text = (string)fileTypes.SelectedItem;
                SwitchPage(0);
                RefreshInfo();
            });
        };
        typeSearch.TextChanged += (sender, args) => UpdateTypeList();
        allTypes.CheckedChanged += (sender, args) => UpdateTypeList();

        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        frame.Controls.Add(main, 1, 0);
        main.Controls.Add(pageHost, 0, 0);
        status.Dock = DockStyle.Fill;
        status.Tag = "muted";
        status.Padding = new Padding(6, 10, 6, 12);
        status.Margin = new Padding(24, 0, 20, 0);
        status.MinimumSize = new Size(0, 40);
        main.Controls.Add(status, 0, 1);

        var manager = Page(0, names[0]);
        var settings = Card(manager, "当前文件类型");
        settings.Controls.Add(currentType);
        extension.Font = new Font(Font.FontFamily, 11);
        extension.Width = 110;
        extension.Margin = new Padding(4, 7, 6, 4);
        extension.AccessibleName = "文件扩展名";
        var query = Row();
        query.Controls.Add(extension);
        query.Controls.Add(ActionButton("检查", RefreshInfo, "primary"));

        enableButton = ActionButton("启用缩略图显示", EnableThumbnails);
        query.Controls.Add(enableButton);
        settings.Controls.Add(query);

        var handler = Card(manager, "缩略图处理器");
        providers.AccessibleName = "选择缩略图处理器";
        SetupPicker(providers);
        providers.SelectionChangeCommitted += (sender, args) => Run(() => UpdateRegistryLocations(Normalize(extension.Text)));
        handler.Controls.Add(providers);
        var actions = Row();
        actions.Controls.Add(ActionButton("应用所选处理器", ApplySelected, "primary"));
        actions.Controls.Add(ActionButton("移除用户覆盖", ResetOverride));
        undoButton = ActionButton("撤销", Undo);
        undoButton.Enabled = false;
        actions.Controls.Add(undoButton);

        handler.Controls.Add(actions);
        var registryCard = Card(manager, "注册表位置");
        registryLocations.AccessibleName = "注册表位置";
        registryCard.Controls.Add(registryLocations);
        var locations = Row();
        locations.Controls.Add(ActionButton("打开注册表位置", OpenSelectedRegistry));
        locations.Controls.Add(ActionButton("复制注册表路径", CopyRegistryPath));
        registryCard.Controls.Add(locations);
        var fileCard = Card(manager, "处理器 DLL");
        AddAction(fileCard, ActionButton("在资源管理器中选中", SelectProviderFile));

        var test = Card(manager, "缩略图预览");
        AddAction(test, ActionButton("选择文件测试", TestFile));
        var imageCard = ResultCard("文件测试结果");
        var imageArea = new Panel { Dock = DockStyle.Fill, Tag = "preview" };
        imageArea.Controls.Add(preview);
        imageArea.Controls.Add(previewEmpty);
        preview.AccessibleName = "缩略图预览";
        preview.Tag = "preview";
        imageCard.Controls.Add(imageArea, 0, 1);
        imageCard.Dock = DockStyle.Top;
        imageCard.Height = 238;
        test.Controls.Add(imageCard);
        var details = Card(manager, "技术详情");
        diagnostics.WordWrap = true;
        diagnostics.ScrollBars = ScrollBars.None;
        diagnostics.BorderStyle = BorderStyle.None;
        diagnostics.Dock = DockStyle.Top;
        diagnostics.Visible = false;
        diagnostics.AccessibleName = "缩略图诊断信息";
        var disclosure = ActionButton("展开技术详情", () =>
        {
            diagnostics.Visible = !diagnostics.Visible;
            FitDiagnostics();
        });
        disclosure.Click += (sender, args) => disclosure.Text = diagnostics.Visible ? "收起技术详情" : "展开技术详情";
        AddAction(details, disclosure);
        details.Controls.Add(diagnostics);
        diagnostics.TextChanged += (sender, args) => FitDiagnostics();
        diagnostics.SizeChanged += (sender, args) => FitDiagnostics();

        var migration = Page(1, names[1]);
        var export = Card(migration, "导出缩略图配置");
        AddDescription(export, "保存当前用户的缩略图配置，供另一台电脑导入。");
        AddAction(export, ActionButton("导出配置", Export, "primary"));
        var import = Card(migration, "导入缩略图配置");
        AddDescription(import, "选择 .thumblab 文件。应用前会检查本机是否已安装所需处理器。");
        AddAction(import, ActionButton("导入配置", Import, "primary"));

        var preferences = Page(2, names[2]);
        var theme = Card(preferences, "应用主题");
        appearance.AccessibleName = "应用主题";
        appearance.Items.AddRange(new object[] { "跟随系统", "浅色", "深色" });
        using (var key = Registry.CurrentUser.OpenSubKey(AppSettings))
        {
            var saved = key == null ? null : key.GetValue("Theme");
            appearance.SelectedIndex = saved is int && (int)saved >= 0 && (int)saved <= 2 ? (int)saved : 0;
        }
        theme.Controls.Add(appearance);
        appearance.SelectedIndexChanged += (sender, args) =>
        {
            ApplyTheme();
            Run(() =>
            {
                using (var key = Registry.CurrentUser.CreateSubKey(AppSettings))
                    key.SetValue("Theme", appearance.SelectedIndex, RegistryValueKind.DWord);
            });
        };
        SetupPicker(appearance);
        SetupPicker(registryLocations);
        LoadFileTypes();
        SwitchPage(0);
    }

    private void FitDiagnostics()
    {
        if (!diagnostics.Visible || diagnostics.Width < 40) return;
        int height = TextRenderer.MeasureText(diagnostics.Text + " ", diagnostics.Font,
            new Size(diagnostics.Width - 8, Int32.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height + 12;
        if (diagnostics.Height != height) diagnostics.Height = height;
    }

    private void LoadFileTypes()
    {
        using (var classes = Registry.ClassesRoot)
            registeredTypes = classes.GetSubKeyNames().Where(name => name.StartsWith("."))
                .Where(name => System.Text.RegularExpressions.Regex.IsMatch(name, @"^\.[a-zA-Z0-9][a-zA-Z0-9_+.-]{0,31}\z"))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();

        string generic = ThumbnailShell.Effective(".thumblab-unregistered-" + Guid.NewGuid().ToString("N"));
        thumbnailTypes = new HashSet<string>(registeredTypes.Where(name =>
            (ThumbnailShell.Effective(name) ?? generic) != generic
            || Read(Registry.ClassesRoot, name + Slot) != null
            || Read(Registry.ClassesRoot, @"SystemFileAssociations\" + name + Slot) != null), StringComparer.OrdinalIgnoreCase);
        UpdateTypeList();
    }

    private void UpdateTypeList()
    {
        if (registeredTypes == null) return;
        string filter = typeSearch.Text.Trim();
        string active = extension.Text.Trim();
        if (!active.StartsWith(".")) active = "." + active;
        updatingTypes = true;
        fileTypes.BeginUpdate();
        try
        {
            fileTypes.Items.Clear();
            fileTypes.Items.AddRange(registeredTypes.Where(name =>
                (allTypes.Checked || thumbnailTypes.Contains(name)) &&
                name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToArray());
            fileTypes.SelectedIndex = fileTypes.FindStringExact(active);
        }
        finally
        {
            fileTypes.EndUpdate();
            updatingTypes = false;
        }
    }

    private TableLayoutPanel Page(int index, string title)
    {
        var scroll = new ThumbScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(26, 18, 26, 8), Visible = false };

        var page = new Panel { Dock = DockStyle.Fill, Visible = false };
        pages[index] = page;
        pageHost.Controls.Add(page);
        page.Controls.Add(scroll);
        scroll.Visible = true;
        page.Controls.Add(new SlimScroll(() => scroll.DisplayRectangle.Height, () => scroll.ClientSize.Height,
            () => -scroll.AutoScrollPosition.Y, offset => scroll.AutoScrollPosition = new Point(0, offset)));
        var body = Stack();
        scroll.Controls.Add(body);
        scroll.Resize += (sender, args) => CenterPage(scroll);
        CenterPage(scroll);
        body.Controls.Add(new Label { Text = title, Font = new Font(Font.FontFamily, 23, FontStyle.Regular), Height = 57, Dock = DockStyle.Top, Margin = Padding.Empty });
        return body;
    }

    private static void CenterPage(Panel scroll)
    {
        // Use the full width of the work area until the readable content width is reached.
        int maxWidth = (int)Math.Round(1000 * scroll.Font.Size / 10);
        int gutter = Math.Max(26, (scroll.ClientSize.Width - maxWidth) / 2);
        var padding = new Padding(gutter, 18, gutter, 8);
        if (scroll.Padding != padding) scroll.Padding = padding;
    }

    private static void RoundCorners(Control control, int radius)
    {
        control.SizeChanged += (sender, args) =>
        {
            Region previous = control.Region;
            int diameter = Math.Min(radius * 2, Math.Min(control.Width, control.Height));
            if (diameter < 2) return;
            var bounds = new Rectangle(0, 0, control.Width, control.Height);
            using (var path = new GraphicsPath())
            {
                path.AddArc(0, 0, diameter, diameter, 180, 90);
                path.AddArc(bounds.Right - diameter, 0, diameter, diameter, 270, 90);
                path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(0, bounds.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                control.Region = new Region(path);
            }
            if (previous != null) previous.Dispose();
        };
    }

    private static TableLayoutPanel Stack()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return layout;
    }

    private TableLayoutPanel Card(TableLayoutPanel body, string title)
    {
        var card = Stack();
        card.Tag = "surface";
        RoundCorners(card, 7);
        card.Padding = new Padding(15, 10, 15, 10);
        card.Margin = new Padding(0, 0, 0, 10);
        card.Controls.Add(Heading(title));
        body.Controls.Add(card);
        return card;
    }

    private TableLayoutPanel ResultCard(string title)
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(15, 10, 15, 12), Tag = "surface", Margin = new Padding(0, 0, 8, 0) };
        RoundCorners(card, 7);
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(Heading(title), 0, 0);
        return card;
    }

    private Label Heading(string text)
    {
        return new Label { Text = text, Font = new Font(Font.FontFamily, 12, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 7) };
    }

    private static void AddDescription(TableLayoutPanel card, string text)
    {
        card.Controls.Add(new Label { Text = text, AutoSize = true, Tag = "muted", Margin = new Padding(0, 0, 0, 6) });
    }

    private static void AddAction(TableLayoutPanel card, Button button)
    {
        var row = Row();
        row.Controls.Add(button);
        card.Controls.Add(row);
    }

    private static FlowLayoutPanel Row()
    {
        return new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
    }

    private static ComboBox Picker()
    {
        return new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    }

    private void SetupPicker(ComboBox box)
    {
        box.FlatStyle = FlatStyle.Flat;
        box.DrawMode = DrawMode.OwnerDrawFixed;
        box.ItemHeight = 25;
        box.DrawItem += (sender, args) =>
        {
            bool selected = (args.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(selected ? selection : surface))
                args.Graphics.FillRectangle(brush, args.Bounds);
            if (args.Index >= 0 && args.Index < box.Items.Count)
                TextRenderer.DrawText(args.Graphics, box.GetItemText(box.Items[args.Index]), Font, args.Bounds, ink,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            args.DrawFocusRectangle();
        };
    }

    private Button ActionButton(string text, Action action, string role = "button")
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, 33), Padding = new Padding(9, 0, 9, 0),
            Margin = new Padding(0, 3, 7, 3), FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, Tag = role };
        if (role == "nav")
        {
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Padding = new Padding(37, 0, 0, 0);
        }
        button.Click += (sender, args) => Run(action);
        return button;
    }

    private void SwitchPage(int index)
    {
        currentPage = index;
        for (int item = 0; item < pages.Length; item++) pages[item].Visible = item == index;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        bool highContrast = SystemInformation.HighContrast;
        bool systemDark;
        using (var key = Registry.CurrentUser.OpenSubKey(Personalize))
            systemDark = key != null && Equals(key.GetValue("AppsUseLightTheme"), 0);
        bool dark = appearance.SelectedIndex == 2 || (appearance.SelectedIndex == 0 && systemDark);
        ink = highContrast ? SystemColors.WindowText : dark ? Color.WhiteSmoke : Color.FromArgb(27, 27, 27);
        muted = highContrast ? ink : dark ? Color.FromArgb(192, 192, 192) : Color.FromArgb(85, 85, 85);
        surface = highContrast ? SystemColors.Window : dark ? Color.FromArgb(41, 41, 41) : Color.White;
        backdrop = highContrast ? SystemColors.Window : dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
        rail = backdrop;
        border = highContrast ? ink : dark ? Color.FromArgb(83, 83, 83) : Color.FromArgb(205, 211, 218);
        hover = highContrast ? backdrop : dark ? Color.FromArgb(57, 57, 57) : Color.FromArgb(232, 232, 232);
        accent = highContrast ? SystemColors.Highlight : dark ? Color.FromArgb(118, 185, 237) : Color.FromArgb(0, 95, 184);
        selection = highContrast ? backdrop : dark ? Color.FromArgb(46, 46, 46) : Color.FromArgb(229, 229, 229);
        PaintControls(this, backdrop, highContrast ? SystemColors.HighlightText : dark ? Color.Black : Color.White);
        if (IsHandleCreated)
        {
            int value = dark && !highContrast ? 1 : 0;
            if (DwmSetWindowAttribute(Handle, 20, ref value, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref value, 4);
            int caption = highContrast ? -1 : ColorTranslator.ToWin32(backdrop);
            int text = highContrast ? -1 : ColorTranslator.ToWin32(ink);
            DwmSetWindowAttribute(Handle, 35, ref caption, 4);
            DwmSetWindowAttribute(Handle, 36, ref text, 4);
        }
    }

    private void PaintControls(Control control, Color parent, Color accentText)
    {
        string role = control.Tag as string;
        Color background = role == "divider" ? border : role == "surface" ? surface : role == "rail" ? rail : role == "preview" ? backdrop : parent;
        if (control is TextBox || control is ComboBox) background = surface;
        control.BackColor = background;
        control.ForeColor = role == "muted" ? muted : ink;
        var button = control as Button;
        if (button != null)
        {
            bool primary = role == "primary";
            bool nav = role == "nav";
            bool selected = nav && button == navigation[currentPage];
            button.BackColor = primary ? accent : selected ? selection : nav ? rail : surface;
            button.ForeColor = primary ? accentText : ink;
            button.FlatAppearance.BorderSize = primary || nav ? 0 : 1;
            button.FlatAppearance.BorderColor = border;
            button.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(accent, 0.08f) : hover;
            button.FlatAppearance.MouseDownBackColor = primary ? ControlPaint.Dark(accent, 0.08f) : selection;
        }
        foreach (Control child in control.Controls) PaintControls(child, background, accentText);
        control.Invalidate();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyTheme();
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if ((message.Msg == 0x001A || message.Msg == 0x031A) && pages != null && pages[0] != null && !IsDisposed)
            ApplyTheme();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
