using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Models;
using TheRadioVault.Core.Services;

namespace TheRadioVault.Desktop.Avalonia.Platform;

public sealed class AvaloniaLibraryFolderShowSelectionService : ILibraryFolderShowSelectionService
{
    private readonly AvaloniaWindowProvider _windows;

    public AvaloniaLibraryFolderShowSelectionService(AvaloniaWindowProvider windows)
        => _windows = windows ?? throw new ArgumentNullException(nameof(windows));

    public async Task<LibraryFolderShowChoice?> ChooseAsync(
        string folderPath,
        IReadOnlyList<LibraryFolderShowChoice> choices,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("A folder path is required.", nameof(folderPath));
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count == 0)
            throw new InvalidOperationException("No show choices are available for this archive folder.");

        var owner = _windows.MainWindow
            ?? throw new InvalidOperationException("The main window is not available yet.");
        var completion = new TaskCompletionSource<LibraryFolderShowChoice?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var combo = new ComboBox
        {
            ItemsSource = choices,
            SelectedIndex = FindSuggestedIndex(folderPath, choices),
            MinWidth = 390,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var choiceDescription = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
            FontSize = 11
        };
        void UpdateChoiceDescription()
            => choiceDescription.Text = (combo.SelectedItem as LibraryFolderShowChoice)?.Description ?? string.Empty;
        combo.SelectionChanged += (_, _) => UpdateChoiceDescription();
        UpdateChoiceDescription();

        var addButton = new Button { Content = "Add folder", MinWidth = 104 };
        addButton.Classes.Add("primary");
        var cancelButton = new Button { Content = "Cancel", MinWidth = 92 };
        cancelButton.Classes.Add("ghost");

        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folderPath));
        if (string.IsNullOrWhiteSpace(folderName))
            folderName = folderPath;

        var dialog = new Window
        {
            Title = "Assign archive folder",
            Width = 540,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new global::Avalonia.Thickness(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Which show is this folder for?",
                        FontSize = 22,
                        FontWeight = FontWeight.SemiBold
                    },
                    new TextBlock
                    {
                        Text = $"Folder: {folderName}",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.72
                    },
                    new TextBlock
                    {
                        Text = "Choose a fixed show for the most reliable classification. Use Auto-detect / mixed-show folder when one folder contains multiple shows or its filenames already identify each show.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.72
                    },
                    combo,
                    choiceDescription,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 9,
                        Children = { cancelButton, addButton }
                    }
                }
            }
        };

        addButton.Click += (_, _) =>
        {
            completion.TrySetResult(combo.SelectedItem as LibraryFolderShowChoice);
            dialog.Close();
        };
        cancelButton.Click += (_, _) =>
        {
            completion.TrySetResult(null);
            dialog.Close();
        };
        dialog.Closed += (_, _) => completion.TrySetResult(null);

        _ = dialog.ShowDialog(owner);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int FindSuggestedIndex(
        string folderPath,
        IReadOnlyList<LibraryFolderShowChoice> choices)
    {
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folderPath));
        var normalized = KnownShowCatalog.Normalize(folderName);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            for (var index = 0; index < choices.Count; index++)
            {
                if (choices[index].CollectionId.HasValue
                    && choices[index].Name.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                    return index;
            }
        }

        var compactFolder = Compact(folderName);
        if (compactFolder.Length > 0)
        {
            for (var index = 0; index < choices.Count; index++)
            {
                if (!choices[index].CollectionId.HasValue)
                    continue;
                var compactShow = Compact(choices[index].Name);
                if (compactShow.Length > 0
                    && (compactFolder.Contains(compactShow, StringComparison.Ordinal)
                        || compactShow.Contains(compactFolder, StringComparison.Ordinal)))
                    return index;
            }
        }

        return 0;
    }

    private static string Compact(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
