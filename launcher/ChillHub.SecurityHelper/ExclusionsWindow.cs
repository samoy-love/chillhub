namespace ChillHub.SecurityHelper {
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Windows;
    using System.Windows.Automation;
    using System.Windows.Controls;
    using System.Windows.Media;
    using ChillHub.Security;

    internal sealed class ExclusionsWindow : Window {
        private SecurityJournal journal;
        private readonly bool removing;
        private readonly List<CheckBox> folders = new();
        private readonly List<Button> copyButtons = new();
        private readonly StackPanel configuration = new();
        private readonly ComboBox product = new() { ItemsSource = AntivirusCatalog.All, SelectedIndex = 0, MinHeight = 36, Margin = new Thickness(0, 8, 0, 6) };
        private readonly TextBlock detected = HelperAppearance.Text(SecuritySetupText.Detecting, "Brush.TextMuted", 12);
        private readonly TextBlock instructions = HelperAppearance.Text("");
        private readonly TextBlock folderHint = HelperAppearance.Text(SecuritySetupText.FolderHint, "Brush.TextMuted", 12);
        private readonly TextBlock guideText = HelperAppearance.Text("");
        private readonly Expander guideDetails = new() { Header = "Краткая инструкция", Margin = new Thickness(0, 8, 0, 0) };
        private readonly TextBlock feedbackTitle = HelperAppearance.Text("", "Brush.Title", 16);
        private readonly TextBlock feedback = HelperAppearance.Text("");
        private readonly TextBox status = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly Expander details = new() { Header = "Результат по папкам", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
        private readonly Expander advanced = new() { Header = "Дополнительно", Margin = new Thickness(0, 0, 0, 12) };
        private readonly Button apply = new() { MinHeight = 38, MinWidth = 180, Padding = new Thickness(16, 8, 16, 8) };
        private readonly Button close = new() { Content = "Не сейчас", MinWidth = 110, MinHeight = 38, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 12, 0) };
        private readonly Button remove = new() { Content = "Удалить добавленные исключения", MinWidth = 270, MinHeight = 36, Padding = new Thickness(12, 8, 12, 8) };
        private readonly Button help = new() { Content = "Настроить вручную", MinWidth = 180, MinHeight = 36, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8) };
        private readonly TextBlock intro;
        private bool busy;
        private bool productChosen;
        private bool complete;
        private bool retry;
        private bool nextRemoving;

        internal ExclusionsWindow(IEnumerable<string> paths, bool removing) {
            this.removing = removing;
            this.nextRemoving = removing;
            this.journal = SecurityJournal.Read(SecurityJournal.DefaultPath);
            var requested = paths.Concat(this.journal.OfferedSteamPaths.Where(System.IO.Directory.Exists)).ToArray();
            HelperAppearance.Apply(this, "настройка защиты");
            this.Width = 720; this.Height = Math.Min(740, SystemParameters.WorkArea.Height - 48);
            this.MinWidth = 560; this.MinHeight = Math.Min(480, this.Height);
            var root = new Grid { Margin = new Thickness(24) };
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            this.Content = root;
            var content = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var heading = HelperAppearance.Text(removing ? SecuritySetupText.RemoveTitle : SecuritySetupText.Title);
            heading.ClearValue(TextBlock.FontSizeProperty);
            heading.ClearValue(TextBlock.ForegroundProperty);
            heading.SetResourceReference(FrameworkElement.StyleProperty, "Style.PageTitle");
            content.Children.Add(heading);
            this.intro = HelperAppearance.Text(removing ? SecuritySetupText.RemoveIntro : SecuritySetupText.Intro);
            this.intro.Margin = new Thickness(0, 8, 0, 20); content.Children.Add(this.intro);
            this.feedbackTitle.FontWeight = FontWeights.SemiBold;
            this.feedbackTitle.Margin = new Thickness(0, 16, 0, 6);
            this.feedback.Margin = new Thickness(0, 0, 0, 12);
            AutomationProperties.SetLiveSetting(this.feedbackTitle, AutomationLiveSetting.Polite);
            content.Children.Add(this.feedbackTitle); content.Children.Add(this.feedback);
            this.details.SetResourceReference(FrameworkElement.StyleProperty, "Style.Expander.Compact");
            this.advanced.SetResourceReference(FrameworkElement.StyleProperty, "Style.Expander.Compact");
            this.guideDetails.SetResourceReference(FrameworkElement.StyleProperty, "Style.Expander.Compact");
            this.status.SetResourceReference(FrameworkElement.StyleProperty, "Style.TextBox.Field.Multiline");
            this.status.Margin = new Thickness(0, 8, 0, 12);
            this.details.Content = this.status; content.Children.Add(this.details);
            content.Children.Add(this.configuration);

            var protection = new StackPanel();
            protection.Children.Add(Section("Антивирус"));
            AutomationProperties.SetName(this.product, "Антивирус");
            protection.Children.Add(this.product); protection.Children.Add(this.detected);
            this.instructions.Margin = new Thickness(0, 12, 0, 0);
            protection.Children.Add(this.instructions);
            this.guideText.Margin = new Thickness(0, 8, 0, 0);
            this.guideDetails.Content = this.guideText; protection.Children.Add(this.guideDetails);
            this.configuration.Children.Add(HelperAppearance.Card(protection));
            this.product.Visibility = removing ? Visibility.Collapsed : Visibility.Visible;
            this.detected.Visibility = removing ? Visibility.Collapsed : Visibility.Visible;

            var folderContent = new StackPanel();
            folderContent.Children.Add(Section("Папки для исключений"));
            this.folderHint.Margin = new Thickness(0, 4, 0, 0); folderContent.Children.Add(this.folderHint);
            this.configuration.Children.Add(HelperAppearance.Card(folderContent));
            foreach (var raw in (removing ? this.journal.AddedPaths : requested.Concat(this.journal.AddedPaths)).Distinct(StringComparer.OrdinalIgnoreCase)) {
                try {
                    var path = ExclusionPolicy.Normalize(raw);
                    if (this.folders.Any(c => string.Equals(c.Tag as string, path, StringComparison.OrdinalIgnoreCase))) { continue; }
                    var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var pathLabel = HelperAppearance.Text(path, "Brush.Text", 13);
                    pathLabel.FontFamily = new FontFamily("Consolas");
                    var check = new CheckBox { ToolTip = path, Tag = path, IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
                    AutomationProperties.SetName(check, path);
                    this.folders.Add(check); row.Children.Add(check);
                    Grid.SetColumn(pathLabel, 1); pathLabel.VerticalAlignment = VerticalAlignment.Center;
                    pathLabel.Margin = new Thickness(0, 0, 12, 0); row.Children.Add(pathLabel);
                    pathLabel.MouseLeftButtonUp += (_, _) => { if (this.IsDefender && !this.busy) { check.IsChecked = check.IsChecked != true; } };
                    check.Checked += (_, _) => this.RefreshActions();
                    check.Unchecked += (_, _) => this.RefreshActions();
                    var copy = new Button { Content = "Копировать", MinWidth = 100, MinHeight = 32, Padding = new Thickness(10, 6, 10, 6), VerticalAlignment = VerticalAlignment.Center };
                    AutomationProperties.SetName(copy, "Копировать путь " + path);
                    Grid.SetColumn(copy, 2);
                    copy.Click += (_, _) => {
                        try { Clipboard.SetText(path); this.ShowFeedback(SecuritySetupText.CopyTitle, SecuritySetupText.CopyHint); }
                        catch (Exception ex) { this.ShowFeedback(SecuritySetupText.CopyFailed, ex.Message); }
                    };
                    this.copyButtons.Add(copy); row.Children.Add(copy); folderContent.Children.Add(row);
                }
                catch (ArgumentException ex) { folderContent.Children.Add(HelperAppearance.Text(raw + "\n" + ex.Message, "Brush.Danger")); }
            }
            var warning = HelperAppearance.Text(removing ? SecuritySetupText.RemoveWarning : ExclusionPolicy.Warning, "Brush.Warning", 12);
            warning.Margin = new Thickness(0, 0, 0, 16);
            this.configuration.Children.Add(warning);
            var extra = new StackPanel { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            extra.Children.Add(this.help); extra.Children.Add(this.remove);
            this.advanced.Content = extra; this.configuration.Children.Add(this.advanced);

            var footer = new Border { Padding = new Thickness(0, 16, 0, 0), Margin = new Thickness(0, 16, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0) };
            footer.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            footer.Child = actions; Grid.SetRow(footer, 1); root.Children.Add(footer);
            this.apply.SetResourceReference(FrameworkElement.StyleProperty, "Style.Button.Primary");
            actions.Children.Add(this.close); actions.Children.Add(this.apply);
            this.close.Click += (_, _) => this.Close();
            this.apply.Click += async (_, _) => {
                if (this.complete) { this.Close(); }
                else if (this.IsDefender) { await this.ChangeAsync(this.nextRemoving); }
                else { this.OpenGuide(); }
            };
            this.remove.Click += async (_, _) => await this.ChangeAsync(true);
            this.help.Click += (_, _) => this.OpenGuide();
            this.product.SelectionChanged += (_, _) => { this.productChosen = true; this.ShowGuide(); };
            this.ShowGuide();
            this.Loaded += async (_, _) => {
                var found = await Task.Run(AntivirusCatalog.Discover);
                this.detected.Text = found.Count == 0 ? SecuritySetupText.DetectionFailed : "Найдены: " + string.Join(", ", found) + ". Проверьте выбор: активность не определена.";
                // Discovery must not overwrite a choice the player already made.
                if (!this.productChosen && !this.busy && !this.complete && !this.removing && found.Count > 0) { this.product.SelectedItem = AntivirusCatalog.Match(found.FirstOrDefault(n => AntivirusCatalog.Match(n).Id != "defender") ?? found[0]); }
                this.RefreshActions();
            };
            this.Closing += (_, e) => { if (this.busy) { e.Cancel = true; this.ShowFeedback(SecuritySetupText.BusyTitle, SecuritySetupText.BusyClose); } };
            this.ContentRendered += (_, _) => {
                if (this.removing) { return; }
                try { this.journal = SecurityJournal.Update(SecurityJournal.DefaultPath, journal => journal.IntroductionShown = true); }
                catch (Exception ex) { this.ShowFeedback(SecuritySetupText.RememberFailed, ex.Message); }
            };
        }

        private bool IsDefender => this.product.SelectedItem is AntivirusGuide { Id: "defender" };

        private static TextBlock Section(string text) {
            var label = HelperAppearance.Text(text, "Brush.Title", 15);
            label.FontWeight = FontWeights.SemiBold;
            return label;
        }

        private void ShowGuide() {
            this.retry = false;
            this.nextRemoving = this.removing;
            var guide = (AntivirusGuide)this.product.SelectedItem;
            this.instructions.Text = this.removing ? SecuritySetupText.RemoveHint : this.IsDefender ? SecuritySetupText.DefenderHint : guide.Id == "mcafee" ? guide.Instructions : SecuritySetupText.ManualHint;
            this.guideText.Text = guide.Instructions;
            this.guideDetails.Visibility = this.IsDefender || guide.Id == "mcafee" ? Visibility.Collapsed : Visibility.Visible;
            this.guideDetails.IsExpanded = guide.Id == "other";
            this.folderHint.Text = this.IsDefender ? SecuritySetupText.FolderHint : SecuritySetupText.CopyFoldersHint;
            foreach (var folder in this.folders) { folder.Visibility = this.IsDefender ? Visibility.Visible : Visibility.Collapsed; }
            foreach (var copy in this.copyButtons) { copy.Visibility = this.IsDefender ? Visibility.Collapsed : Visibility.Visible; }
            this.advanced.Visibility = this.IsDefender && !this.removing ? Visibility.Visible : Visibility.Collapsed;
            this.remove.Visibility = this.journal.AddedPaths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            this.ShowFeedback("", ""); this.details.Visibility = Visibility.Collapsed;
            if (this.removing && this.folders.Count == 0) { this.ShowFeedback(SecuritySetupText.NothingToRemove, SecuritySetupText.NothingToRemoveHint); }
            this.RefreshActions();
        }

        private void RefreshActions() {
            bool selected = this.folders.Any(c => c.IsChecked == true);
            bool owned = this.folders.Any(c => c.IsChecked == true && this.journal.AddedPaths.Contains((string)c.Tag, StringComparer.OrdinalIgnoreCase));
            var guide = (AntivirusGuide)this.product.SelectedItem;
            this.apply.Content = this.complete ? "Закрыть" : this.busy ? "Настраиваем…" : this.nextRemoving ? "Удалить исключения" : this.IsDefender ? this.retry ? "Повторить настройку" : "Добавить исключения" : "Открыть инструкцию";
            this.apply.Visibility = !this.complete && !this.IsDefender && string.IsNullOrEmpty(guide.Url) ? Visibility.Collapsed : Visibility.Visible;
            this.apply.IsEnabled = !this.busy && (this.complete || (this.IsDefender ? this.nextRemoving ? owned : selected : true));
            this.close.Content = this.busy || !this.IsDefender ? "Закрыть" : "Не сейчас";
            this.close.Visibility = this.complete ? Visibility.Collapsed : Visibility.Visible;
            this.close.IsEnabled = !this.busy;
            this.remove.IsEnabled = !this.busy && owned;
            this.help.IsEnabled = !this.busy;
            this.product.IsEnabled = !this.busy;
            foreach (var folder in this.folders) { folder.IsEnabled = !this.busy; }
            foreach (var copy in this.copyButtons) { copy.IsEnabled = !this.busy; }
        }

        private void ShowFeedback(string title, string message) {
            this.feedbackTitle.Text = title; this.feedback.Text = message;
            this.feedbackTitle.Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible;
            this.feedback.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OpenGuide() {
            this.productChosen = true;
            var guide = (AntivirusGuide)this.product.SelectedItem;
            if (!Uri.TryCreate(guide.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https") { return; }
            try {
                Process.Start(new ProcessStartInfo(guide.Url) { UseShellExecute = true });
                foreach (var copy in this.copyButtons) { copy.Visibility = Visibility.Visible; }
                this.ShowFeedback(SecuritySetupText.GuideOpened, SecuritySetupText.GuideOpenedHint + SecuritySetupText.Retry);
            }
            catch (Exception ex) { this.ShowFeedback(SecuritySetupText.GuideFailed, ex.Message); }
        }

        private void PresentResults(ExclusionResult[] results, int expected, bool deleting) {
            var summary = SecuritySetupText.Summarize(results, expected, deleting);
            this.complete = summary.Complete;
            this.retry = !this.complete;
            this.intro.Visibility = this.complete ? Visibility.Collapsed : Visibility.Visible;
            this.status.Text = string.Join("\n\n", results.Select(r => r.Path + "\n" + r.Detail));
            this.details.Visibility = Visibility.Visible; this.details.IsExpanded = !summary.Complete && results.Any(r => r.Status != ExclusionStatus.Cancelled);
            this.configuration.Visibility = this.complete ? Visibility.Collapsed : Visibility.Visible;
            this.ShowFeedback(summary.Title, summary.Message);
            this.feedbackTitle.SetResourceReference(TextBlock.ForegroundProperty, summary.Complete ? "Brush.Success" : "Brush.Warning");
            this.feedbackTitle.BringIntoView();
            if (this.complete) { this.MinHeight = 320; this.Height = Math.Min(400, SystemParameters.WorkArea.Height - 48); }
        }

        private async Task ChangeAsync(bool deleting) {
            if (this.busy) { return; }
            this.nextRemoving = deleting;
            var selected = this.folders.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray();
            if (deleting) { selected = selected.Where(p => this.journal.AddedPaths.Contains(p, StringComparer.OrdinalIgnoreCase)).ToArray(); }
            if (selected.Length == 0) { this.ShowFeedback(SecuritySetupText.SelectFolders, SecuritySetupText.SelectOwned); return; }
            this.productChosen = true; this.busy = true; this.RefreshActions();
            this.ShowFeedback(SecuritySetupText.Waiting, SecuritySetupText.Pending);
            this.feedbackTitle.BringIntoView();
            this.status.Clear(); this.details.Visibility = Visibility.Collapsed;
            try {
                var results = await PrivilegedOperation.RunAsync(selected, deleting, result => {
                    try { this.journal = SecurityJournal.Update(SecurityJournal.DefaultPath, journal => journal.Record(new[] { result })); }
                    finally { this.status.Text += result.Path + "\n" + result.Detail + "\n\n"; }
                });
                this.PresentResults(results, selected.Length, deleting);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { this.retry = true; this.ShowFeedback(SecuritySetupText.CancelledTitle, SecuritySetupText.Cancelled); this.feedbackTitle.BringIntoView(); }
            catch (Exception ex) {
                this.retry = true;
                this.ShowFeedback(SecuritySetupText.IncompleteTitle, SecuritySetupText.Interrupted);
                this.status.Text += ex.Message; this.details.Visibility = Visibility.Visible; this.details.IsExpanded = true;
                this.feedbackTitle.BringIntoView();
            }
            finally {
                this.busy = false;
                this.remove.Visibility = this.journal.AddedPaths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                this.RefreshActions();
                if (this.complete) { this.apply.Focus(); }
            }
        }
    }
}
