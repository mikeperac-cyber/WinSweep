using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSweep.Core.Models;
using WinSweep.Core.Sweep;

namespace WinSweep.App.Views;

/// <summary>The dry-run preview dialog, shared by the Clean and Projects pages.</summary>
public static class SweepPreview
{
    // A XamlRoot can host only one ContentDialog at a time.
    private static bool _isOpen;

    /// <summary>Shows what a sweep would do. Nothing is removed; the primary button opens the saved manifest.</summary>
    public static async Task ShowAsync(XamlRoot xamlRoot, SweepManifest manifest)
    {
        if (_isOpen)
        {
            return;
        }

        _isOpen = true;
        try
        {
            var path = SweepPlanner.DryRunManifestPath;
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Dry run: nothing was removed",
                Content = BuildPreviewContent(manifest),
                PrimaryButtonText = "Open manifest",
                IsPrimaryButtonEnabled = File.Exists(path),
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
                {
                    // No app is registered for .json; the path is shown in the dialog anyway.
                }
            }
        }
        finally
        {
            _isOpen = false;
        }
    }

    private static UIElement BuildPreviewContent(SweepManifest manifest)
    {
        var panel = new StackPanel { Spacing = 6, MinWidth = 420 };

        panel.Children.Add(new TextBlock
        {
            Text = $"{ByteSizeFormatter.Format(manifest.TotalBytes)} in {manifest.Entries.Count} locations",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });

        foreach (var (category, bytes) in manifest.BytesByCategory.OrderByDescending(c => c.Value))
        {
            panel.Children.Add(new TextBlock { Text = $"{category}: {ByteSizeFormatter.Format(bytes)}", Opacity = 0.8 });
        }

        int Count(SweepRisk risk) => manifest.Entries.Count(e => e.Risk == risk);
        panel.Children.Add(new TextBlock
        {
            Text = $"Safe: {Count(SweepRisk.Safe)}   Caution: {Count(SweepRisk.Caution)}   Locked: {Count(SweepRisk.Locked)}",
            Margin = new Thickness(0, 8, 0, 0)
        });

        var flagged = manifest.Entries.Where(e => e.Risk != SweepRisk.Safe).OrderByDescending(e => e.Risk).ToList();
        if (flagged.Count > 0)
        {
            var list = new StackPanel { Spacing = 4 };
            foreach (var entry in flagged)
            {
                list.Children.Add(new TextBlock
                {
                    Text = $"{entry.Name} — {entry.RiskReason}",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.85
                });
            }

            panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260 });
        }

        panel.Children.Add(new TextBlock
        {
            Text = SweepPlanner.DryRunManifestPath,
            FontSize = 11,
            Opacity = 0.55,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(0, 8, 0, 0)
        });

        return panel;
    }
}
