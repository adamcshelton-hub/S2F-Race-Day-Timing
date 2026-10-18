using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace S2F.RaceDayCheckout;

public enum TimerCloseChoice
{
    KeepWorking,
    SaveAndClose,
    CloseWithoutSaving,
    DoneLocalOnly
}

/// <summary>
/// In-app branded dialogs matching the busy overlay (no Windows title-bar chrome).
/// </summary>
public static class TimerDialog
{
    public static void Alert(Window? owner, string title, string message)
    {
        var window = CreateChromeLess(owner);
        var card = BuildCard(
            title,
            message,
            buttons: new[]
            {
                MakeButton("OK", "PrimaryButton", () => { window.DialogResult = true; })
            });
        window.Content = DimmedHost(card);
        window.ShowDialog();
    }

    /// <summary>
    /// Checkout confirm with monospace Save As folder map (shown before Active exists).
    /// </summary>
    public static bool ConfirmSaveAsGuide(
        Window? owner,
        string title,
        string message,
        string yesLabel = "Checkout",
        string noLabel = "Cancel")
    {
        var result = false;
        var window = CreateChromeLess(owner);
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });
        stack.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 14,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
            Foreground = Brush("MutedBrush"),
            TextAlignment = TextAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var yes = MakeButton(yesLabel, "PrimaryButton", () => { result = true; window.DialogResult = true; });
        yes.Margin = new Thickness(0, 20, 0, 0);
        stack.Children.Add(yes);
        var no = MakeButton(noLabel, "SecondaryButton", () => { result = false; window.DialogResult = false; });
        no.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(no);

        var card = new Border
        {
            Background = Brush("CardBrush"),
            CornerRadius = new CornerRadius(12),
            BorderBrush = Brush("AccentBrush"),
            BorderThickness = new Thickness(3),
            Padding = new Thickness(36, 32, 36, 32),
            MinWidth = 480,
            MaxWidth = 640,
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                Opacity = 0.35,
                ShadowDepth = 0,
                Color = Colors.Black
            },
            Child = stack
        };
        window.Content = DimmedHost(card);
        window.ShowDialog();
        return result;
    }

    public static bool Confirm(Window? owner, string title, string message, string yesLabel = "Yes", string noLabel = "No")
    {
        var result = false;
        var window = CreateChromeLess(owner);
        var card = BuildCard(
            title,
            message,
            buttons: new[]
            {
                MakeButton(yesLabel, "PrimaryButton", () => { result = true; window.DialogResult = true; }),
                MakeButton(noLabel, "SecondaryButton", () => { result = false; window.DialogResult = false; })
            });
        window.Content = DimmedHost(card);
        window.ShowDialog();
        return result;
    }

    /// <summary>
    /// Non-modal busy overlay. Close via Dispose before showing Alert/Confirm.
    /// </summary>
    public static ProgressSession ShowProgress(Window? owner, string title, string message)
    {
        var window = CreateChromeLess(owner, allowEscapeClose: false);
        var messageBlock = new TextBlock
        {
            Text = message,
            FontSize = 16,
            Foreground = Brush("MutedBrush"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 24
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });
        stack.Children.Add(messageBlock);
        stack.Children.Add(new TextBlock
        {
            Text = "Keep this window open.",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("AccentBrush"),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 22, 0, 0)
        });

        var card = new Border
        {
            Background = Brush("CardBrush"),
            CornerRadius = new CornerRadius(12),
            BorderBrush = Brush("AccentBrush"),
            BorderThickness = new Thickness(3),
            Padding = new Thickness(40, 36, 40, 36),
            MinWidth = 420,
            MaxWidth = 520,
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                Opacity = 0.35,
                ShadowDepth = 0,
                Color = Colors.Black
            },
            Child = stack
        };

        window.Content = DimmedHost(card);
        if (owner is not null)
            owner.IsEnabled = false;
        window.Show();
        return new ProgressSession(window, messageBlock, owner);
    }

    public sealed class ProgressSession : IDisposable
    {
        private readonly Window _window;
        private readonly TextBlock _messageBlock;
        private readonly Window? _owner;
        private bool _disposed;

        internal ProgressSession(Window window, TextBlock messageBlock, Window? owner)
        {
            _window = window;
            _messageBlock = messageBlock;
            _owner = owner;
        }

        public void SetMessage(string message)
        {
            if (_disposed)
                return;

            void Apply() => _messageBlock.Text = message;
            if (_messageBlock.Dispatcher.CheckAccess())
                Apply();
            else
                _messageBlock.Dispatcher.Invoke(Apply);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            void Close()
            {
                try
                {
                    _window.Close();
                }
                catch
                {
                    // Window may already be closing with the owner.
                }

                if (_owner is not null)
                    _owner.IsEnabled = true;
            }

            if (_window.Dispatcher.CheckAccess())
                Close();
            else
                _window.Dispatcher.Invoke(Close);
        }
    }

    public static TimerCloseChoice AskCloseWhileActive(Window? owner)
    {
        var result = TimerCloseChoice.KeepWorking;
        var window = CreateChromeLess(owner);

        Button? save = null;
        Button? stay = null;
        Button? discard = null;

        save = MakeButton("Save & Close", "SaveButton", () =>
        {
            result = TimerCloseChoice.SaveAndClose;
            window.DialogResult = true;
        });
        stay = MakeButton("Keep Working", "SecondaryButton", () =>
        {
            result = TimerCloseChoice.KeepWorking;
            window.DialogResult = false;
        });
        discard = MakeButton("Close Without Saving", "DangerButton", () =>
        {
            if (!Confirm(window, "Are you sure?",
                    "Close without saving?\n\nOnly do this if setup staff told you to.",
                    yesLabel: "Close Without Saving",
                    noLabel: "Go back"))
                return;
            result = TimerCloseChoice.CloseWithoutSaving;
            window.DialogResult = true;
        });

        var card = BuildCard(
            "Race still open",
            "You're still checked out for a race.\n\n"
            + "Keep this app open while racing — it mirrors live files to OneDrive for failover.\n\n"
            + "Save your work before closing. If you close without saving, another timer may not be able to use this event.",
            buttons: new[] { save, stay, discard });

        window.Content = DimmedHost(card);
        window.ShowDialog();
        return result;
    }

    /// <summary>
    /// Soft warning when Active has no .event yet. Returns true to Finish anyway,
    /// false to stay on Active (Continue Edit).
    /// </summary>
    public static bool ConfirmFinishWithoutTimingFile(Window? owner, string activePath)
    {
        var finishAnyway = false;
        var window = CreateChromeLess(owner);
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = "No Timing File Yet",
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        stack.Children.Add(new TextBlock
        {
            Text =
                "No Timing & Scoring .event is in the Active folder yet (see T&S Save As Location under Finish). "
                + "You can Continue Edit and Save As now, or Finish without it and add the file later.",
            FontSize = 15,
            Foreground = Brush("MutedBrush"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });

        stack.Children.Add(new TextBlock
        {
            Text = activePath,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
            FontSize = 12,
            Foreground = Brush("MutedBrush"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        stack.Children.Add(new TextBlock
        {
            Text =
                "Announcer / video files: Save As additional .event files into this same Active folder. "
                + "On Finish you will mark the main timing file; other .event files are offered on Announcer T&S Laptop. "
                + "You can also add them later under Admin → Edit Current Event.",
            FontSize = 13,
            Foreground = Brush("MutedBrush"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var finish = MakeButton("Finish", "PrimaryButton", () =>
        {
            finishAnyway = true;
            window.DialogResult = true;
        });
        finish.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(finish);

        var stay = MakeButton("Continue Edit", "SecondaryButton", () =>
        {
            finishAnyway = false;
            window.DialogResult = false;
        });
        stay.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(stay);

        var card = new Border
        {
            Background = Brush("CardBrush"),
            CornerRadius = new CornerRadius(12),
            BorderBrush = Brush("AccentBrush"),
            BorderThickness = new Thickness(3),
            Padding = new Thickness(36, 32, 36, 32),
            MinWidth = 480,
            MaxWidth = 640,
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                Opacity = 0.35,
                ShadowDepth = 0,
                Color = Colors.Black
            },
            Child = stack
        };
        window.Content = DimmedHost(card);
        window.ShowDialog();
        return finishAnyway;
    }

    /// <summary>
    /// Finish step when .event files exist but primary is unset: pick the main timing file.
    /// Returns null if cancelled (Finish still proceeds without assignment).
    /// </summary>
    public static string? PickPrimaryEventFile(
        Window? owner,
        string activePath,
        IReadOnlyList<string> eventFiles,
        string? suggestedFileName)
    {
        if (eventFiles.Count == 0)
            return null;

        string? picked = null;
        var window = CreateChromeLess(owner);
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = "Confirm Main Timing .event",
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        stack.Children.Add(new TextBlock
        {
            Text = "These Timing & Scoring files are in the Active folder. Choose which one is the main timing .event for this race.",
            FontSize = 15,
            Foreground = Brush("MutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });

        stack.Children.Add(new TextBlock
        {
            Text = activePath,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
            FontSize = 12,
            Foreground = Brush("MutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var combo = new ComboBox
        {
            ItemsSource = eventFiles,
            SelectedItem = suggestedFileName is not null
                           && eventFiles.Any(f => string.Equals(f, suggestedFileName, StringComparison.OrdinalIgnoreCase))
                ? eventFiles.First(f => string.Equals(f, suggestedFileName, StringComparison.OrdinalIgnoreCase))
                : eventFiles[0],
            Padding = new Thickness(8, 6, 8, 6),
            MinHeight = 36,
            Margin = new Thickness(0, 0, 0, 14),
            FontSize = 15
        };
        stack.Children.Add(combo);

        stack.Children.Add(new TextBlock
        {
            Text =
                "Announcer / video .event files: Save As extra .event files into this same Active folder in Timing & Scoring (before Finish). "
                + "Anything that is not the main timing file is offered on Announcer T&S Laptop. "
                + "You can also add them later, then mark main timing under Admin → Edit Current Event.",
            FontSize = 13,
            Foreground = Brush("MutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var confirm = MakeButton("Confirm & Finish", "SaveButton", () =>
        {
            picked = combo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(picked))
                return;
            window.DialogResult = true;
        });
        confirm.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(confirm);

        var cancel = MakeButton("Cancel", "SecondaryButton", () =>
        {
            picked = null;
            window.DialogResult = false;
        });
        cancel.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(cancel);

        var card = new Border
        {
            Background = Brush("CardBrush"),
            CornerRadius = new CornerRadius(12),
            BorderBrush = Brush("AccentBrush"),
            BorderThickness = new Thickness(3),
            Padding = new Thickness(36, 32, 36, 32),
            MinWidth = 480,
            MaxWidth = 640,
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                Opacity = 0.35,
                ShadowDepth = 0,
                Color = Colors.Black
            },
            Child = stack
        };
        window.Content = DimmedHost(card);
        window.ShowDialog();
        return string.IsNullOrWhiteSpace(picked) ? null : picked;
    }

    /// <summary>
    /// Announcer close: no Finish/upload. Leave Active so Timing &amp; Scoring keeps its files,
    /// or Done to clear this laptop only.
    /// </summary>
    public static TimerCloseChoice AskCloseWhileAnnouncerActive(Window? owner)
    {
        var result = TimerCloseChoice.KeepWorking;
        var window = CreateChromeLess(owner);

        var done = MakeButton("Done", "SaveButton", () =>
        {
            result = TimerCloseChoice.DoneLocalOnly;
            window.DialogResult = true;
        });
        var stay = MakeButton("Keep Working", "SecondaryButton", () =>
        {
            result = TimerCloseChoice.KeepWorking;
            window.DialogResult = false;
        });
        var leave = MakeButton("Close App", "SecondaryButton", () =>
        {
            result = TimerCloseChoice.CloseWithoutSaving;
            window.DialogResult = true;
        });

        var card = BuildCard(
            "Announcer files still open",
            "This announcer copy stays on this laptop only. Closing the app does not upload anything and does not affect the timing laptop.\n\n"
            + "Timing & Scoring can stay open if you close the app.\n\n"
            + "Tap Done only when you are finished — that removes the announcer files from this laptop.",
            buttons: new[] { done, stay, leave });

        window.Content = DimmedHost(card);
        window.ShowDialog();
        return result;
    }

    private static Window CreateChromeLess(Window? owner, bool allowEscapeClose = true)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };

        if (owner is not null)
        {
            window.Width = owner.ActualWidth;
            window.Height = owner.ActualHeight;
            window.Left = owner.Left;
            window.Top = owner.Top;
            window.WindowState = owner.WindowState;
        }
        else
        {
            window.Width = 520;
            window.Height = 420;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (allowEscapeClose)
        {
            window.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    window.DialogResult = false;
                    e.Handled = true;
                }
            };
        }

        return window;
    }

    private static Grid DimmedHost(UIElement card)
    {
        var host = new Grid();
        host.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0xE6, 0, 0, 0)) });
        var wrapper = new Border
        {
            Child = card,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24)
        };
        host.Children.Add(wrapper);
        return host;
    }

    private static Border BuildCard(string title, string message, IEnumerable<Button> buttons)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });
        stack.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 16,
            Foreground = Brush("MutedBrush"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 24,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var buttonPanel = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        var first = true;
        foreach (var button in buttons)
        {
            if (!first)
                button.Margin = new Thickness(0, 10, 0, 0);
            first = false;
            buttonPanel.Children.Add(button);
        }

        stack.Children.Add(buttonPanel);

        return new Border
        {
            Background = Brush("CardBrush"),
            CornerRadius = new CornerRadius(12),
            BorderBrush = Brush("AccentBrush"),
            BorderThickness = new Thickness(3),
            Padding = new Thickness(40, 36, 40, 36),
            MinWidth = 420,
            MaxWidth = 520,
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                Opacity = 0.35,
                ShadowDepth = 0,
                Color = Colors.Black
            },
            Child = stack
        };
    }

    private static Button MakeButton(string text, string styleKey, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)Application.Current.FindResource(styleKey),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 52
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static Brush Brush(string key) =>
        (Brush)Application.Current.FindResource(key);
}
