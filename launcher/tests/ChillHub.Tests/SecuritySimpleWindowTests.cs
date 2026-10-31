extern alias SecurityHelper;

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Windows;
    using System.Windows.Controls;
    using ChillHub.Security;
    using Xunit;
    using ExclusionsWindow = SecurityHelper::ChillHub.SecurityHelper.ExclusionsWindow;

    public sealed class SecuritySimpleWindowTests {
        private static readonly string[] Paths = { @"C:\ChillHubTest\launcher", @"C:\ChillHubTest\games", @"C:\ChillHubTest\updates" };

        [Fact]
        public void ProviderFailureDoesNotExposePathsOrPretendTheWholeBatchSucceeded() => UiThread.Run(() => {
            var window = new ExclusionsWindow(Paths, false, new SecurityJournal());
            window.PresentResults(new[] {
                new ExclusionResult(Paths[0], ExclusionStatus.Applied, "ok"),
                new ExclusionResult(Paths[1], ExclusionStatus.Failed, @"Access denied: C:\Users\PrivatePlayer\file.dll"),
                new ExclusionResult(Paths[2], ExclusionStatus.Unknown, Paths[2]),
            }, 3, false);
            var text = string.Join("\n", Descendants(window).OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains(SecuritySetupText.IncompleteTitle, text);
            Assert.DoesNotContain(@"C:\", text);
            Assert.DoesNotContain("PrivatePlayer", text);
            Assert.Contains(Descendants(window).OfType<Button>(), b => Equals(b.Content, "Повторить") && b.IsEnabled);
            Assert.Empty(Descendants(window).OfType<CheckBox>());
        });

        [Fact]
        public void InvalidFolderCannotBeSilentlyDroppedFromAutomaticSetup() => UiThread.Run(() => {
            var window = new ExclusionsWindow(new[] { Paths[0], @"C:\", Paths[2] }, false, new SecurityJournal());
            Assert.Contains(Descendants(window).OfType<Button>(), b => Equals(b.Content, "Открыть инструкцию") && b.IsEnabled);
            Assert.DoesNotContain(Descendants(window).OfType<Button>(), b => Equals(b.Content, "Настроить"));
            Assert.Contains(Descendants(window).OfType<TextBlock>(), t => t.Text == SecuritySetupText.PrepareFailed);
        });

        [Fact]
        public void VerifiedBatchLeavesOnlyTheCompletionAction() => UiThread.Run(() => {
            var window = new ExclusionsWindow(Paths, false, new SecurityJournal());
            window.PresentResults(Paths.Select(p => new ExclusionResult(p, ExclusionStatus.Applied, p)).ToArray(), 3, false);
            Assert.Contains(Descendants(window).OfType<Button>(), b => Equals(b.Content, "Готово") && b.IsEnabled);
            Assert.Contains(Descendants(window).OfType<Button>(), b => Equals(b.Content, "Не сейчас") && b.Visibility == Visibility.Collapsed);
            Assert.All(Descendants(window).OfType<Expander>(), expander => Assert.False(expander.IsExpanded));
        });

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent) {
            yield return parent;
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) {
                foreach (var descendant in Descendants(child)) { yield return descendant; }
            }
        }
    }
}
