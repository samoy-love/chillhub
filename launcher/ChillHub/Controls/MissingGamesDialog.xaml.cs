namespace ChillHub.Controls {
    using System;
    using System.ComponentModel;
    using System.Linq;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Input;

    using ChillHub.Core.Home;

    public partial class MissingGamesDialog : UserControl {
        private readonly MissingGameRow[] rows;
        private readonly Func<MissingGameFolder, Task<string>> delete;
        private bool finished;

        internal MissingGamesDialog(MissingGamesSnapshot snapshot, Func<MissingGameFolder, Task<string>> delete) {
            this.InitializeComponent();
            this.delete = delete;
            this.TitleText.Text = MissingGamesText.Title;
            this.DescriptionText.Text = MissingGamesText.Description;
            this.WarningText.Text = MissingGamesText.Warning;
            this.LaterButton.Content = MissingGamesText.Later;
            this.DeleteButton.Content = MissingGamesText.Delete;
            this.CloseButton.ToolTip = MissingGamesText.Close;
            this.rows = snapshot.Folders.Select(f => new MissingGameRow(f)).ToArray();
            foreach (var row in this.rows) {
                row.PropertyChanged += (_, __) => this.DeleteButton.IsEnabled = !this.IsBusy && this.rows.Any(r => r.IsSelected);
            }

            this.FolderList.ItemsSource = this.rows;
            this.DeleteButton.IsEnabled = this.rows.Length > 0;
            this.Loaded += (_, __) => this.LaterButton.Focus();
        }

        internal event Action? Closed;

        internal bool IsBusy { get; private set; }

        internal void Close() {
            if (!this.IsBusy) {
                this.Closed?.Invoke();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => this.Close();

        private void Dialog_PreviewKeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                e.Handled = true;
                this.Close();
            }
        }

        private async void Delete_Click(object sender, RoutedEventArgs e) => await this.DeleteSelectedAsync();

        internal async Task DeleteSelectedAsync() {
            var selected = this.rows.Where(r => r.IsSelected).ToArray();
            if (this.IsBusy || this.finished || selected.Length == 0) {
                return;
            }

            this.IsBusy = true;
            this.DeleteButton.IsEnabled = false;
            this.LaterButton.IsEnabled = false;
            this.CloseButton.IsEnabled = false;
            foreach (var row in this.rows) {
                row.CanSelect = false;
            }

            this.ProgressText.Visibility = Visibility.Visible;
            this.DeletionProgress.Visibility = Visibility.Visible;
            this.DeletionProgress.Maximum = selected.Length;
            try {
                for (var i = 0; i < selected.Length; i++) {
                    this.ProgressText.Text = MissingGamesText.Progress(i + 1, selected.Length);
                    selected[i].Result = MissingGamesText.Progress(i + 1, selected.Length);
                    try {
                        selected[i].Result = await this.delete(selected[i].Folder);
                    }
                    catch (Exception ex) {
                        Core.Logging.Logger.Warn($"MissingGamesDialog: {ex.Message}");
                        selected[i].Result = MissingGamesText.Failed;
                    }

                    this.DeletionProgress.Value = i + 1;
                }
            }
            finally {
                this.finished = true;
                this.IsBusy = false;
                this.DeleteButton.Visibility = Visibility.Collapsed;
                this.LaterButton.Content = MissingGamesText.Close;
                this.LaterButton.IsEnabled = true;
                this.CloseButton.IsEnabled = true;
                this.ProgressText.Text = MissingGamesText.Finished;
                this.LaterButton.Focus();
            }
        }

        internal sealed class MissingGameRow : INotifyPropertyChanged {
            private bool selected = true;
            private bool canSelect = true;
            private string result = string.Empty;

            internal MissingGameRow(MissingGameFolder folder) => this.Folder = folder;

            public event PropertyChangedEventHandler? PropertyChanged;

            public MissingGameFolder Folder { get; }

            public bool IsSelected {
                get => this.selected;
                set { this.selected = value; this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsSelected))); }
            }

            public bool CanSelect {
                get => this.canSelect;
                set { this.canSelect = value; this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.CanSelect))); }
            }

            public string Result {
                get => this.result;
                set { this.result = value; this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.Result))); }
            }
        }
    }
}
