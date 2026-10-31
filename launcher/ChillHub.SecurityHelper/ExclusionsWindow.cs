namespace ChillHub.SecurityHelper {
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Windows;
    using System.Windows.Automation;
    using System.Windows.Controls;
    using System.Windows.Media;
    using ChillHub.Security;

    internal sealed class ExclusionsWindow : Window {
        private sealed record Folder(string Path, string Name, string Group) {
            public override string ToString() => this.Name;
        }

        private SecurityJournal journal;
        private readonly bool removing;
        private readonly List<Folder> folders = new();
        private readonly StackPanel configuration = new();
        private readonly Border scope;
        private readonly ComboBox product = new() { ItemsSource = AntivirusCatalog.All, SelectedIndex = 0, MinHeight = 36, Margin = new Thickness(0, 0, 0, 16) };
        private readonly TextBlock instructions = HelperAppearance.Text("");
        private readonly TextBlock feedbackTitle = HelperAppearance.Text("", "Brush.Title", 16);
        private readonly TextBlock feedback = HelperAppearance.Text("");
        private readonly TextBlock status = HelperAppearance.Text("");
        private readonly Expander details = new() { Header = "Что не получилось", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        private readonly Expander advanced = new() { Header = "Другие варианты", Margin = new Thickness(0, 12, 0, 0) };
        private readonly Button apply = new() { MinHeight = 38, MinWidth = 140, Padding = new Thickness(16, 8, 16, 8) };
        private readonly Button close = new() { Content = "Не сейчас", MinWidth = 100, MinHeight = 38, Margin = new Thickness(0, 0, 12, 0) };
        private readonly Button remove = new() { Content = "Удалить исключения", MinHeight = 34, Padding = new Thickness(12, 6, 12, 6) };
        private readonly Button help = new() { Content = "Настроить вручную", MinHeight = 34, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 0, 8) };
        private readonly StackPanel manual = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
        private readonly TextBlock copyHint = HelperAppearance.Text(SecuritySetupText.CopyFoldersHint);
        private readonly ComboBox copyFolder = new() { MinHeight = 34, MinWidth = 120 };
        private readonly Button copy = new() { Content = "Копировать", MinHeight = 34, MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
        private readonly TextBlock intro;
        private readonly bool invalidFolders;
        private bool busy;
        private bool productChosen;
        private bool complete;
        private bool retry;
        private bool nextRemoving;

        internal ExclusionsWindow(IEnumerable<string> paths, bool removing)
            : this(paths, removing, SecurityJournal.Read(SecurityJournal.DefaultPath)) { }

        internal ExclusionsWindow(IEnumerable<string> paths, bool removing, SecurityJournal journal) {
            this.removing = removing;
            this.nextRemoving = removing;
            this.journal = journal;
            var requested = paths.ToArray();
            var knownGames = requested.Skip(1).SkipLast(1).Concat(journal.OfferedSteamPaths.Where(System.IO.Directory.Exists)).ToArray();
            var source = removing ? journal.AddedPaths : requested.Concat(knownGames).Concat(journal.AddedPaths);
            foreach (var raw in source.Distinct(StringComparer.OrdinalIgnoreCase)) {
                try {
                    var path = ExclusionPolicy.Normalize(raw);
                    if (this.folders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) { continue; }
                    var group = SecuritySetupText.FolderName(path, requested.FirstOrDefault(), requested.LastOrDefault(), knownGames);
                    var name = group;
                    if (group == "Игры" && !string.Equals(path, ExclusionPolicy.Normalize(knownGames[0]), StringComparison.OrdinalIgnoreCase)) {
                        name = "Игра «" + System.IO.Path.GetFileName(path) + "»";
                    }
                    var number = this.folders.Count(f => f.Name == name || f.Name.StartsWith(name + " · ", StringComparison.Ordinal)) + 1;
                    this.folders.Add(new Folder(path, number == 1 ? name : name + " · " + number, group));
                }
                catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or UnauthorizedAccessException) {
                    this.invalidFolders = true;
                    HelperDiagnostics.Record(ex.ToString());
                }
            }

            HelperAppearance.Apply(this, "настройка защиты");
            this.Width = 520; this.Height = Math.Min(440, SystemParameters.WorkArea.Height - 48);
            this.MinWidth = 440; this.MinHeight = Math.Min(360, this.Height);
            var root = new Grid { Margin = new Thickness(24) };
            root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            this.Content = root;
            var content = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var heading = HelperAppearance.Text(removing ? SecuritySetupText.RemoveTitle : SecuritySetupText.Title);
            heading.ClearValue(TextBlock.FontSizeProperty); heading.ClearValue(TextBlock.ForegroundProperty);
            heading.SetResourceReference(FrameworkElement.StyleProperty, "Style.PageTitle"); content.Children.Add(heading);
            this.intro = HelperAppearance.Text(removing ? SecuritySetupText.RemoveIntro : SecuritySetupText.Intro);
            this.intro.Margin = new Thickness(0, 8, 0, 20); content.Children.Add(this.intro);
            this.feedbackTitle.FontWeight = FontWeights.SemiBold;
            this.feedbackTitle.Margin = new Thickness(0, 12, 0, 6);
            this.feedback.Margin = new Thickness(0, 0, 0, 12);
            AutomationProperties.SetLiveSetting(this.feedbackTitle, AutomationLiveSetting.Polite);
            content.Children.Add(this.feedbackTitle); content.Children.Add(this.feedback);
            this.details.SetResourceReference(FrameworkElement.StyleProperty, "Style.Expander.Compact");
            this.advanced.SetResourceReference(FrameworkElement.StyleProperty, "Style.Expander.Compact");
            this.status.Margin = new Thickness(0, 8, 0, 12); this.details.Content = this.status;
            content.Children.Add(this.details); content.Children.Add(this.configuration);

            AutomationProperties.SetName(this.product, "Ваш антивирус");
            this.product.Visibility = removing ? Visibility.Collapsed : Visibility.Visible;
            this.configuration.Children.Add(this.product);
            this.scope = HelperAppearance.Card(HelperAppearance.Text(string.Join(" · ", this.folders.Select(f => f.Group).Distinct()), "Brush.Text", 14));
            this.configuration.Children.Add(this.scope);
            this.configuration.Children.Add(this.instructions);

            var copyRow = new Grid();
            copyRow.ColumnDefinitions.Add(new ColumnDefinition()); copyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            this.copyFolder.ItemsSource = this.folders; this.copyFolder.SelectedIndex = 0;
            AutomationProperties.SetName(this.copyFolder, "Что добавить в антивирус");
            copyRow.Children.Add(this.copyFolder); Grid.SetColumn(this.copy, 1); copyRow.Children.Add(this.copy);
            this.copyHint.MinHeight = 36;
            AutomationProperties.SetLiveSetting(this.copyHint, AutomationLiveSetting.Polite);
            this.manual.Children.Add(this.copyHint); this.manual.Children.Add(copyRow);
            this.configuration.Children.Add(this.manual);
            var extra = new StackPanel { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            extra.Children.Add(this.help); extra.Children.Add(this.remove); this.advanced.Content = extra;
            this.configuration.Children.Add(this.advanced);

            var footer = new Border { Padding = new Thickness(0, 16, 0, 0), Margin = new Thickness(0, 16, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0) };
            footer.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            footer.Child = actions; Grid.SetRow(footer, 1); root.Children.Add(footer);
            this.apply.SetResourceReference(FrameworkElement.StyleProperty, "Style.Button.Primary");
            actions.Children.Add(this.close); actions.Children.Add(this.apply);
            this.close.Click += (_, _) => this.Close();
            this.apply.Click += async (_, _) => {
                if (this.complete) { this.Close(); }
                else if (this.IsDefender && !this.invalidFolders) { await this.ChangeAsync(this.nextRemoving); }
                else { this.OpenGuide(); }
            };
            this.remove.Click += async (_, _) => await this.ChangeAsync(true);
            this.help.Click += (_, _) => this.OpenGuide();
            this.copy.Click += (_, _) => {
                if (this.copyFolder.SelectedItem is not Folder folder) { return; }
                try { Clipboard.SetText(folder.Path); this.PresentCopyResult(true); }
                catch (Exception ex) { HelperDiagnostics.Record(ex.ToString()); this.PresentCopyResult(false); }
            };
            this.copyFolder.SelectionChanged += (_, _) => this.ResetCopyHint();
            this.product.SelectionChanged += (_, _) => { this.productChosen = true; this.ShowGuide(); };
            this.ShowGuide();
            this.Loaded += async (_, _) => {
                var found = await Task.Run(AntivirusCatalog.Discover);
                if (!this.productChosen && !this.busy && !this.complete && !this.removing && found.Count > 0) {
                    this.product.SelectedItem = AntivirusCatalog.Match(found.FirstOrDefault(n => AntivirusCatalog.Match(n).Id != "defender") ?? found[0]);
                }
                if (found.Count == 0 && !this.productChosen && !this.busy && !this.complete && !this.removing) {
                    this.ShowFeedback(SecuritySetupText.DetectionFailed, "");
                }
                this.RefreshActions();
            };
            this.Closing += (_, e) => { if (this.busy) { e.Cancel = true; } };
            this.ContentRendered += (_, _) => {
                if (this.removing) { return; }
                try { this.journal = SecurityJournal.Update(SecurityJournal.DefaultPath, state => state.IntroductionShown = true); }
                catch (Exception ex) { HelperDiagnostics.Record(ex.ToString()); this.ShowFeedback(SecuritySetupText.RememberFailed, ""); }
            };
        }

        private bool IsDefender => this.product.SelectedItem is AntivirusGuide { Id: "defender" };

        private void ShowGuide() {
            this.ResetCopyHint();
            this.retry = false; this.nextRemoving = this.removing;
            var guide = (AntivirusGuide)this.product.SelectedItem;
            this.intro.Visibility = this.IsDefender ? Visibility.Visible : Visibility.Collapsed;
            this.scope.Visibility = this.IsDefender ? Visibility.Visible : Visibility.Collapsed;
            this.configuration.Margin = new Thickness(0, this.IsDefender ? 0 : 16, 0, 0);
            this.instructions.Text = this.removing ? SecuritySetupText.RemoveWarning : this.IsDefender ? SecuritySetupText.Warning
                : guide.Id == "mcafee" ? SecuritySetupText.FilesOnlyHint : guide.Id == "other" ? SecuritySetupText.UnknownHint : SecuritySetupText.ManualHint;
            this.instructions.SetResourceReference(TextBlock.ForegroundProperty, this.IsDefender && !this.removing ? "Brush.Warning" : "Brush.TextSecondary");
            this.manual.Visibility = !this.IsDefender && guide.Id != "mcafee" ? Visibility.Visible : Visibility.Collapsed;
            this.advanced.Visibility = this.IsDefender && !this.removing ? Visibility.Visible : Visibility.Collapsed;
            this.remove.Visibility = this.journal.AddedPaths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            this.ShowFeedback("", ""); this.details.Visibility = Visibility.Collapsed;
            if (this.invalidFolders) { this.ShowFeedback(SecuritySetupText.IncompleteTitle, SecuritySetupText.PrepareFailed); }
            else if (this.removing && this.folders.Count == 0) {
                this.complete = true;
                this.ShowFeedback(SecuritySetupText.NothingToRemove, "");
                this.ShowCompletion();
            }
            else if (this.folders.Count == 0) { this.ShowFeedback(SecuritySetupText.IncompleteTitle, ""); }
            this.RefreshActions();
        }

        private void RefreshActions() {
            bool owned = this.folders.Any(f => this.journal.AddedPaths.Contains(f.Path, StringComparer.OrdinalIgnoreCase));
            var guide = (AntivirusGuide)this.product.SelectedItem;
            this.apply.Content = this.complete ? "Готово" : this.busy ? "Подождите…" : this.invalidFolders ? "Открыть инструкцию" : this.nextRemoving ? "Удалить" : this.IsDefender ? this.retry ? "Повторить" : "Настроить" : "Открыть инструкцию";
            this.apply.Visibility = !this.complete && !this.IsDefender && string.IsNullOrEmpty(guide.Url) ? Visibility.Collapsed : Visibility.Visible;
            this.apply.IsEnabled = !this.busy && (this.complete || (this.invalidFolders ? !string.IsNullOrEmpty(guide.Url) : this.IsDefender ? this.nextRemoving ? owned : this.folders.Count > 0 : true));
            this.close.Content = this.busy || !this.IsDefender ? "Закрыть" : "Не сейчас";
            this.close.Visibility = this.complete ? Visibility.Collapsed : Visibility.Visible; this.close.IsEnabled = !this.busy;
            this.remove.IsEnabled = !this.busy && owned && !this.invalidFolders; this.help.IsEnabled = !this.busy;
            this.product.IsEnabled = !this.busy; this.copyFolder.IsEnabled = !this.busy; this.copy.IsEnabled = !this.busy && this.copyFolder.SelectedItem != null;
        }

        private void ShowFeedback(string title, string message) {
            this.feedbackTitle.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Title");
            this.feedbackTitle.Text = title; this.feedback.Text = message;
            this.feedbackTitle.Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible;
            this.feedback.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ResetCopyHint() {
            this.copyHint.Text = SecuritySetupText.CopyFoldersHint;
            this.copyHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        }

        private void PresentCopyResult(bool copied) {
            this.copyHint.Text = copied ? SecuritySetupText.CopyHint : SecuritySetupText.CopyFailed + ". " + SecuritySetupText.TryAgain;
            this.copyHint.SetResourceReference(TextBlock.ForegroundProperty, copied ? "Brush.Success" : "Brush.Warning");
        }

        private void OpenGuide() {
            this.productChosen = true;
            var guide = (AntivirusGuide)this.product.SelectedItem;
            if (!Uri.TryCreate(guide.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https") { return; }
            try {
                Process.Start(new ProcessStartInfo(guide.Url) { UseShellExecute = true });
                this.manual.Visibility = guide.Id == "mcafee" ? Visibility.Collapsed : Visibility.Visible;
                this.intro.Visibility = Visibility.Collapsed;
                this.scope.Visibility = Visibility.Collapsed;
                this.configuration.Margin = new Thickness(0, 16, 0, 0);
                this.ResetCopyHint();
            }
            catch (Exception ex) { HelperDiagnostics.Record(ex.ToString()); this.ShowFeedback(SecuritySetupText.GuideFailed, SecuritySetupText.TryAgain); }
        }

        internal void PresentResults(ExclusionResult[] results, int expected, bool deleting) {
            var summary = SecuritySetupText.Summarize(results, expected, deleting);
            this.complete = summary.Complete; this.retry = !this.complete;
            this.intro.Visibility = Visibility.Collapsed;
            this.status.Text = string.Join("\n", results.Where(r => r.Status is not (ExclusionStatus.Applied or ExclusionStatus.Existing or ExclusionStatus.Removed or ExclusionStatus.Cancelled))
                .Select(r => (this.folders.FirstOrDefault(f => string.Equals(f.Path, r.Path, StringComparison.OrdinalIgnoreCase))?.Name ?? "Файлы Chill Hub") + ": " + SecuritySetupText.ResultHint(r.Status)));
            this.details.Visibility = string.IsNullOrEmpty(this.status.Text) ? Visibility.Collapsed : Visibility.Visible;
            this.details.IsExpanded = false;
            this.configuration.Visibility = this.complete ? Visibility.Collapsed : Visibility.Visible;
            this.ShowFeedback(summary.Title, summary.Message);
            this.feedbackTitle.SetResourceReference(TextBlock.ForegroundProperty, summary.Complete ? "Brush.Success" : "Brush.Warning");
            this.feedbackTitle.BringIntoView();
            if (this.complete) { this.ShowCompletion(); }
            this.RefreshActions();
        }

        private void ShowCompletion() {
            this.intro.Visibility = Visibility.Collapsed;
            this.configuration.Visibility = Visibility.Collapsed;
            this.MinHeight = Math.Min(240, SystemParameters.WorkArea.Height - 48);
            this.Height = Math.Min(280, SystemParameters.WorkArea.Height - 48);
        }

        private async Task ChangeAsync(bool deleting) {
            if (this.busy || this.invalidFolders) { return; }
            this.nextRemoving = deleting;
            var selected = this.folders.Select(f => f.Path).Where(p => !deleting || this.journal.AddedPaths.Contains(p, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (selected.Length == 0) { return; }
            this.productChosen = true; this.busy = true; this.RefreshActions();
            this.ShowFeedback(SecuritySetupText.Waiting, SecuritySetupText.Pending); this.feedbackTitle.BringIntoView();
            this.status.Text = ""; this.details.Visibility = Visibility.Collapsed;
            bool started = false;
            try {
                if (!deleting) { ExclusionPolicy.PrepareFolders(selected); }
                started = true;
                var results = await PrivilegedOperation.RunAsync(selected, deleting, result => {
                    HelperDiagnostics.Record(result.Path + ": " + result.Detail);
                    this.journal = SecurityJournal.Update(SecurityJournal.DefaultPath, state => state.Record(new[] { result }));
                });
                this.PresentResults(results, selected.Length, deleting);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { this.retry = true; this.ShowFeedback(SecuritySetupText.CancelledTitle, SecuritySetupText.Cancelled); }
            catch (Exception ex) {
                HelperDiagnostics.Record(ex.ToString()); this.retry = true;
                this.ShowFeedback(SecuritySetupText.IncompleteTitle, started ? SecuritySetupText.Interrupted : SecuritySetupText.PrepareFailed);
            }
            finally {
                this.busy = false;
                this.remove.Visibility = this.journal.AddedPaths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                this.RefreshActions(); this.feedbackTitle.BringIntoView();
                if (this.complete) { this.apply.Focus(); }
            }
        }
    }
}
