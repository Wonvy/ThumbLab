#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ThumbLabWinUI;

internal sealed partial class MainWindow
{
    private async Task<ThumbnailSettings.ConfigImportResult?> PreviewConfigImportAsync(string path)
    {
        var plan = await Task.Run(() => settings.PrepareImport(path));
        var items = new StackPanel { Spacing = 10, MinWidth = 270 };
        var selections = new Dictionary<string, ThumbnailSettings.ImportOption>(StringComparer.OrdinalIgnoreCase);
        var dialog = new ContentDialog
        {
            Title = "导入配置",
            XamlRoot = root.XamlRoot,
            Content = new ScrollViewer { MaxHeight = 400, Content = items },
            PrimaryButtonText = "应用配置",
            CloseButtonText = "取消"
        };
        void UpdateButtons()
        {
            bool canApply = plan.Applicable.Length > 0 || selections.Count > 0;
            dialog.IsPrimaryButtonEnabled = canApply;
            dialog.CloseButtonText = canApply ? "取消" : "关闭";
        }

        AddGroup($"可应用（{plan.Applicable.Length}）", plan.Applicable.Select(item => item.Description));
        AddGroup($"已是相同配置（{plan.Unchanged.Length}）", plan.Unchanged);
        if (plan.Missing.Length > 0)
        {
            items.Children.Add(Text($"缺少组件（{plan.Missing.Length}）", 16));
            items.Children.Add(Text("可选择本机组件，未选择的项目会跳过。", 13));
            foreach (var item in plan.Missing)
            {
                var row = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 0, 5) };
                var label = Text($"{item.Extension}  ·  {item.ProviderName}", 13);
                ToolTipService.SetToolTip(label, item.ProviderId);
                row.Children.Add(label);
                if (item.Replacements.Length > 0)
                {
                    var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                    picker.Items.Add("跳过");
                    foreach (var option in item.Replacements) picker.Items.Add(option.Provider.Name);
                    picker.SelectedIndex = 0;
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, $"{item.Extension} 的替代组件");
                    picker.SelectionChanged += (_, _) =>
                    {
                        if (picker.SelectedIndex > 0) selections[item.Extension] = item.Replacements[picker.SelectedIndex - 1];
                        else selections.Remove(item.Extension);
                        UpdateButtons();
                    };
                    row.Children.Add(picker);
                }
                else row.Children.Add(Text("本机没有可用的替代组件。", 12));
                items.Children.Add(row);
            }
        }
        if (items.Children.Count == 0) items.Children.Add(Text("此配置文件没有可导入的项目。", 13));
        UpdateButtons();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return settings.CommitImport(plan, selections);

        void AddGroup(string title, IEnumerable<string> entries)
        {
            var lines = entries.ToArray();
            if (lines.Length == 0) return;
            items.Children.Add(Text(title, 16));
            items.Children.Add(Text(string.Join("\n", lines), 13));
        }
    }
}
