using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;

namespace FrostyEditor.Utils;

public static class TextPromptDialog
{
    public static async Task<string?> ShowAsync(string title, string prompt, string initialValue)
    {
        if ((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not { } owner)
        {
            return null;
        }

        Window dialog = new()
        {
            Title = title,
            Width = 420,
            Height = 170,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        TextBox input = new()
        {
            Text = initialValue,
            Margin = new Thickness(0, 8, 0, 0)
        };

        string? result = null;

        Button okButton = new()
        {
            Content = "OK",
            Width = 84,
            IsDefault = true
        };

        Button cancelButton = new()
        {
            Content = "Cancel",
            Width = 84,
            IsCancel = true
        };

        okButton.Click += (_, _) =>
        {
            result = input.Text?.Trim();
            dialog.Close();
        };

        cancelButton.Click += (_, _) => dialog.Close();

        dialog.Content = new Border
        {
            Padding = new Thickness(14),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,*"),
                Children =
                {
                    new TextBlock
                    {
                        Text = prompt
                    },
                    new TextBox
                    {
                        Text = initialValue,
                        Margin = new Thickness(0, 8, 0, 0),
                        [Grid.RowProperty] = 1,
                        Name = "PromptInput"
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 14, 0, 0),
                        [Grid.RowProperty] = 2,
                        Children =
                        {
                            cancelButton,
                            okButton
                        }
                    }
                }
            }
        };

        if (dialog.Content is Border border &&
            border.Child is Grid grid &&
            grid.Children[1] is TextBox createdInput)
        {
            input = createdInput;
        }

        await dialog.ShowDialog(owner);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
