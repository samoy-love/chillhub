namespace ChillHub.SecurityHelper {
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using ChillHub.Security;

    internal sealed class ExclusionsWindow : Window {
        private SecurityJournal journal;
        private readonly List<CheckBox> folders = new();
        private readonly ComboBox product = new() { ItemsSource = AntivirusCatalog.All, SelectedIndex = 0, Margin = new Thickness(0, 12, 0, 8) };
        private readonly TextBlock detected = new() { Text = "Определяем установленные антивирусы…", TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock instructions = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) };
        private readonly TextBox status = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 0) };
        private readonly Button apply = new() { Content = "Добавить исключения Defender", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 8, 8, 0) };
        private readonly Button remove = new() { Content = "Удалить добавленные исключения", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 8, 8, 0) };
        private bool busy;
        private readonly Button manual = new() { Content = "Я настроил вручную", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 8, 8, 0) };

        internal ExclusionsWindow(IEnumerable<string> paths, bool removing) {
            this.journal = SecurityJournal.Read(SecurityJournal.DefaultPath);
            HelperAppearance.Apply(this, "исключения антивируса");
            this.Width = 840; this.Height = 860; this.MinWidth = 640; this.MinHeight = 560;
            var content = new StackPanel { Margin = new Thickness(24) };
            this.Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var heading = HelperAppearance.Text("Исключения антивируса", "Brush.Title", 24);
            heading.FontWeight = FontWeights.SemiBold;
            content.Children.Add(heading);
            var intro = HelperAppearance.Text("Выберите папки и антивирус. Настройку можно пропустить.");
            intro.Margin = new Thickness(0, 6, 0, 20); content.Children.Add(intro);
            var warning = HelperAppearance.Card(HelperAppearance.Text(ExclusionPolicy.Warning));
            warning.SetResourceReference(Border.BorderBrushProperty, "Brush.Warning");
            content.Children.Add(warning);
            var folderContent = new StackPanel();
            folderContent.Children.Add(HelperAppearance.Text("Папки", "Brush.Text", 16));
            var folderHint = HelperAppearance.Text("Снимите галочки с папок, которые не хотите добавлять.");
            folderHint.Margin = new Thickness(0, 4, 0, 6); folderContent.Children.Add(folderHint);
            content.Children.Add(HelperAppearance.Card(folderContent));
            foreach (var raw in paths.Concat(this.journal.AddedPaths).Distinct(StringComparer.OrdinalIgnoreCase)) {
                try {
                    var path = ExclusionPolicy.Normalize(raw);
                    if (this.folders.Any(c => string.Equals(c.Tag as string, path, StringComparison.OrdinalIgnoreCase))) { continue; }
                    var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var pathLabel = HelperAppearance.Text(path, "Brush.Text", 13);
                    var check = new CheckBox { Content = pathLabel, ToolTip = path, Tag = path, IsChecked = removing ? this.journal.AddedPaths.Contains(path, StringComparer.OrdinalIgnoreCase) : paths.Any(p => string.Equals(p.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase)), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                    this.folders.Add(check); row.Children.Add(check);
                    var copy = new Button { Content = "Копировать", MinWidth = 104, MinHeight = 32, Padding = new Thickness(10, 6, 10, 6), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(copy, 1);
                    row.SizeChanged += (_, _) => pathLabel.MaxWidth = Math.Max(100, row.ActualWidth - copy.ActualWidth - 44);
                    copy.Click += (_, _) => { try { Clipboard.SetText(path); } catch (Exception ex) { this.status.Text = "Не удалось скопировать: " + ex.Message; } };
                    row.Children.Add(copy); folderContent.Children.Add(row);
                }
                catch (ArgumentException ex) { folderContent.Children.Add(HelperAppearance.Text(raw + "\n" + ex.Message, "Brush.Danger")); }
            }
            var protection = new StackPanel();
            protection.Children.Add(HelperAppearance.Text("Антивирус", "Brush.Text", 16));
            this.product.MinHeight = 36;
            this.detected.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
            this.detected.FontSize = 12;
            this.instructions.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            this.instructions.Margin = new Thickness(0, 12, 0, 4);
            protection.Children.Add(this.product); protection.Children.Add(this.detected); protection.Children.Add(this.instructions);
            content.Children.Add(HelperAppearance.Card(protection));
            var actions = new WrapPanel(); protection.Children.Add(actions);
            this.apply.SetResourceReference(FrameworkElement.StyleProperty, "Style.Button.Primary");
            this.apply.MinHeight = 38;
            this.remove.MinHeight = 38; this.remove.MinWidth = 274;
            actions.Children.Add(this.apply); actions.Children.Add(this.remove);
            var help = new Button { Content = "Официальная инструкция", MinHeight = 36, MinWidth = 204, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 8, 8, 0) };
            this.manual.MinHeight = 36; this.manual.MinWidth = 180;
            var manualActions = new WrapPanel(); protection.Children.Add(manualActions);
            manualActions.Children.Add(help); manualActions.Children.Add(this.manual);
            this.status.SetResourceReference(FrameworkElement.StyleProperty, "Style.TextBox.Field.Multiline");
            this.status.SetResourceReference(Control.BackgroundProperty, "Brush.Surface");
            this.status.MinHeight = 90; this.status.MaxHeight = 190;
            this.status.Margin = new Thickness(0);
            content.Children.Add(this.status);
            this.apply.Click += async (_, _) => await this.ChangeAsync(false);
            this.remove.Click += async (_, _) => await this.ChangeAsync(true);
            help.Click += (_, _) => {
                var guide = (AntivirusGuide)this.product.SelectedItem;
                if (Uri.TryCreate(guide.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https") {
                    try { Process.Start(new ProcessStartInfo(guide.Url) { UseShellExecute = true }); } catch (Exception ex) { this.status.Text = "Не удалось открыть справку: " + ex.Message; }
                }
            };
            this.manual.Click += (_, _) => {
                try {
                    var guide = (AntivirusGuide)this.product.SelectedItem;
                    var selected = this.folders.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray();
                    if (selected.Length == 0) { this.status.Text = "Выберите настроенные папки."; return; }
                    this.journal = SecurityJournal.Update(SecurityJournal.DefaultPath, journal => {
                        foreach (var path in selected) {
                            if (!journal.ManualPaths.Any(p => p.ProductId == guide.Id && string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))) {
                                journal.ManualPaths.Add(new ManualExclusion(guide.Id, path));
                            }
                        }
                    });
                    this.status.Text = string.Join("\n", selected.Select(path => path + ": настроено пользователем — не проверено.")) + "\nВернитесь в лаунчер и повторите загрузку.";
                }
                catch (Exception ex) { this.status.Text = "Не удалось сохранить отметку: " + ex.Message; }
            };
            this.product.SelectionChanged += (_, _) => this.ShowGuide();
            this.ShowGuide();
            this.Loaded += async (_, _) => {
                var found = await Task.Run(AntivirusCatalog.Discover);
                this.detected.Text = found.Count == 0 ? "Не удалось определить антивирус. Выберите его выше." : "Найдены: " + string.Join(", ", found) + ". Наличие продукта не означает, что он сейчас активен.";
                if (!this.busy && !removing && found.Count > 0) { this.product.SelectedItem = AntivirusCatalog.Match(found.FirstOrDefault(n => AntivirusCatalog.Match(n).Id != "defender") ?? found[0]); }
            };
            this.Closing += (_, e) => { if (this.busy) { e.Cancel = true; this.status.Text += "\nДождитесь результата изменения."; } };
            if (removing) { this.status.Text = "Выбраны записи, добавленные помощником. Удалите их перед удалением лаунчера. Если пропустить этот шаг, они останутся в Defender."; }
        }

        private void ShowGuide() {
            var guide = (AntivirusGuide)this.product.SelectedItem;
            this.instructions.Text = guide.Instructions;
            this.apply.IsEnabled = !this.busy && guide.Id == "defender";
            this.remove.IsEnabled = !this.busy && guide.Id == "defender" && this.journal.AddedPaths.Count > 0;
            var manualPaths = this.journal.ManualPaths.Where(p => p.ProductId == guide.Id && this.folders.Any(c => string.Equals(c.Tag as string, p.Path, StringComparison.OrdinalIgnoreCase))).Select(p => p.Path).ToArray();
            this.status.Text = guide.Id == "defender" ? "Добавление и удаление потребуют подтверждения Windows. Они изменяют только Microsoft Defender." : manualPaths.Length > 0 ? "Настроено пользователем — не проверено:\n" + string.Join("\n", manualPaths) : "Нужна ручная настройка. Автоматическое добавление для этого продукта недоступно.";
        }

        private async Task ChangeAsync(bool removing) {
            if (this.busy) { return; }
            var selected = this.folders.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray();
            if (removing) { selected = selected.Where(p => this.journal.AddedPaths.Contains(p, StringComparer.OrdinalIgnoreCase)).ToArray(); }
            if (selected.Length == 0) { this.status.Text = "Выберите папки. Удалить можно только записи, добавленные помощником."; return; }
            this.busy = true; this.apply.IsEnabled = false; this.remove.IsEnabled = false; this.product.IsEnabled = false;
            this.manual.IsEnabled = false;
            foreach (var folder in this.folders) { folder.IsEnabled = false; }
            this.status.Text = "Подтвердите запрос Windows и список папок в окне помощника.";
            try {
                var results = await PrivilegedOperation.RunAsync(selected, removing, result => {
                    this.journal = SecurityJournal.Update(SecurityJournal.DefaultPath, journal => journal.Record(new[] { result }));
                    this.status.Text += "\n" + result.Path + ": " + result.Detail;
                });
                this.status.Text = string.Join("\n", results.Select(r => r.Path + ": " + r.Detail)) + "\nВернитесь в лаунчер. Повторная загрузка запускается отдельно.";
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { this.status.Text = "Запрос прав администратора отменён. Настройки не изменены."; }
            catch (Exception ex) { this.status.Text += "\nОперация не завершена: " + ex.Message + "\nЧасть изменений могла примениться. Проверьте список исключений Defender."; }
            finally {
                this.busy = false; this.product.IsEnabled = true; this.apply.IsEnabled = true; this.remove.IsEnabled = this.journal.AddedPaths.Count > 0;
                this.manual.IsEnabled = true;
                foreach (var folder in this.folders) { folder.IsEnabled = true; }
            }
        }
    }
}
