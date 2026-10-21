using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout;

public enum TimerCloseChoice
{
    KeepWorking,
    SaveAndClose,
    CloseWithoutSaving,
    DoneLocalOnly
}

/// <summary>Another laptop took the checkout lock while this machine still has Active.</summary>
public enum TimerLockTakenOverChoice
{
    /// <summary>Escape / PIN cancel — leave Active blocked; heartbeat can re-prompt.</summary>
    Dismissed,
    /// <summary>Clear local Active; leave the other laptop's lock alone.</summary>
    CloseLocalCopy,
    /// <summary>Admin PIN verified — reclaim this race on this laptop.</summary>
    ForceTakeOver
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

    public static bool Confirm(
        Window? owner,
        string title,
        string message,
        string yesLabel = "Yes",
        string noLabel = "No",
        bool yesIsDanger = false)
    {
        var result = false;
        var window = CreateChromeLess(owner);
        var card = BuildCard(
            title,
            message,
            buttons: new[]
            {
                MakeButton(yesLabel, yesIsDanger ? "DangerButton" : "PrimaryButton", () => { result = true; window.DialogResult = true; }),
                MakeButton(noLabel, "SecondaryButton", () => { result = false; window.DialogResult = false; })
            });
        window.Content = DimmedHost(card);
        window.ShowDialog();
        return result;
    }

    /// <summary>
    /// Soft/force takeover happened elsewhere while this laptop still has Active.
    /// Force Take Over stays in this overlay and collects the Admin PIN here (no bounce to main).
    /// </summary>
    public static TimerLockTakenOverChoice AskLockTakenOver(
        Window? owner,
        string holderName,
        string machineName)
    {
        var choice = TimerLockTakenOverChoice.Dismissed;
        var window = CreateChromeLess(owner);
        var who = string.IsNullOrWhiteSpace(holderName) ? "Another timer" : holderName.Trim();
        var where = string.IsNullOrWhiteSpace(machineName) ? "another laptop" : machineName.Trim();
        var cardHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        void ShowChoices()
        {
            var closeLocal = MakeButton("Close Local Copy", "PrimaryButton", () =>
            {
                choice = TimerLockTakenOverChoice.CloseLocalCopy;
                window.DialogResult = true;
            });
            var force = MakeButton("Force Take Over…", "DangerButton", ShowPinStep);
            cardHost.Content = BuildCard(
                "Another Laptop Took Over",
                $"{who} on {where} now holds this event.\n\n"
                + "This laptop must not upload or Finish — that would overwrite their work.\n\n"
                + "Close Local Copy clears Active on this machine only (close RunScore / Timing & Scoring first).\n\n"
                + "Force Take Over requires the Admin PIN and reclaims the race here. Close RunScore so live entries can be applied.",
                buttons: new[] { closeLocal, force });
        }

        void ShowPinStep()
        {
            cardHost.Content = BuildAdminPinCard(
                title: "Force Take Over",
                message: "Enter the Admin PIN to reclaim this race on this laptop.\n\nClose RunScore if it is open — live Entries cannot overwrite a locked file.",
                confirmLabel: "Force Take Over",
                onConfirmValidPin: _ =>
                {
                    choice = TimerLockTakenOverChoice.ForceTakeOver;
                    window.DialogResult = true;
                },
                onBack: ShowChoices,
                verifyCompanyPin: true);
        }

        ShowChoices();
        window.Content = DimmedHost(cardHost);
        window.ShowDialog();
        return choice;
    }

    /// <summary>
    /// Select-session force take-over: warning + Admin PIN in one branded overlay.
    /// </summary>
    public static bool ConfirmForceTakeoverWithAdminPin(
        Window? owner,
        string holderName,
        string machineName,
        string lastSeenLocal)
    {
        var confirmed = false;
        var window = CreateChromeLess(owner);
        var cardHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        void ShowWarning()
        {
            var force = MakeButton("Force Take Over…", "DangerButton", ShowPinStep);
            var cancel = MakeButton("Cancel", "SecondaryButton", () =>
            {
                confirmed = false;
                window.DialogResult = false;
            });
            cardHost.Content = BuildCard(
                "Already in Use",
                $"Someone else is already using this event.\n\n"
                + $"{holderName} on {machineName}\nLast seen {lastSeenLocal}\n\n"
                + "Only use Force Take Over if that laptop truly cannot finish (crash, power loss, or you're the only timer left).\n\n"
                + "Force Take Over requires the Admin PIN. Close RunScore on this laptop so live entries from the other machine can be applied.",
                buttons: new[] { force, cancel });
        }

        void ShowPinStep()
        {
            cardHost.Content = BuildAdminPinCard(
                title: "Force Take Over",
                message: "Enter the Admin PIN to take over this event.\n\nClose RunScore on this laptop if it is open — live Entries cannot overwrite a locked file.",
                confirmLabel: "Force Take Over",
                onConfirmValidPin: _ =>
                {
                    confirmed = true;
                    window.DialogResult = true;
                },
                onBack: ShowWarning,
                verifyCompanyPin: true);
        }

        ShowWarning();
        window.Content = DimmedHost(cardHost);
        window.ShowDialog();
        return confirmed;
    }

    /// <summary>
    /// Branded Admin PIN overlay (chrome-less). Returns null if cancelled.
    /// When <paramref name="verifyCompanyPin"/> is true, wrong PIN stays on the card.
    /// </summary>
    public static string? PromptAdminPin(
        Window? owner,
        string title,
        string message,
        bool verifyCompanyPin = false)
    {
        string? pin = null;
        var window = CreateChromeLess(owner);
        var card = BuildAdminPinCard(
            title,
            message,
            confirmLabel: "Continue",
            onConfirmValidPin: accepted =>
            {
                pin = accepted;
                window.DialogResult = true;
            },
            onBack: () => { window.DialogResult = false; },
            verifyCompanyPin: verifyCompanyPin,
            backLabel: "Cancel");
        window.Content = DimmedHost(card);
        window.ShowDialog();
        return pin;
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

    /// <summary>
    /// Timing checkout is still open. Finish uploads and releases the lock.
    /// Close App leaves the checkout on this laptop so a reopen can resume.
    /// </summary>
    public static TimerCloseChoice AskCloseWhileActive(
        Window? owner,
        bool sessionEnding = false,
        IReadOnlyList<string>? vendorsStillOpen = null)
    {
        var result = TimerCloseChoice.KeepWorking;
        var window = CreateChromeLess(owner);
        var staleMinutes = AppSettings.DefaultStaleLockMinutes;

        var save = MakeButton("Finish & Close", "SaveButton", () =>
        {
            result = TimerCloseChoice.SaveAndClose;
            window.DialogResult = true;
        });
        var stay = MakeButton("Keep Working", "SecondaryButton", () =>
        {
            result = TimerCloseChoice.KeepWorking;
            window.DialogResult = false;
        });
        var leave = MakeButton("Close App", "DangerButton", () =>
        {
            if (!Confirm(
                    window,
                    "Close Without Finishing?",
                    FormatLeaveCheckoutWarning(staleMinutes, vendorsStillOpen),
                    yesLabel: "Close App",
                    noLabel: "Go Back",
                    yesIsDanger: true))
                return;
            result = TimerCloseChoice.CloseWithoutSaving;
            window.DialogResult = true;
        });

        var intro = sessionEnding
            ? "Windows tried to shut down or sign out. That is stopped until you choose below.\n\n"
            : string.Empty;

        var card = BuildCard(
            "Race Still Checked Out",
            intro
            + "This laptop is still checked out for a race.\n\n"
            + "Keep this app open while racing. It mirrors live files to OneDrive and holds the checkout.\n\n"
            + "Finish & Close saves the race and lets another laptop check it out.\n\n"
            + "Close App leaves the checkout on this laptop and stops the live mirror until you open this app again. "
            + $"After about {staleMinutes} minutes another laptop can take this race.",
            buttons: new[] { save, stay, leave });

        window.Content = DimmedHost(card);
        window.ShowDialog();
        return result;
    }

    private static string FormatLeaveCheckoutWarning(int staleMinutes, IReadOnlyList<string>? vendorsStillOpen)
    {
        var message =
            "The race stays checked out on this laptop. Nothing new is uploaded.\n\n"
            + "The live mirror stops until you open this app again. "
            + $"After about {staleMinutes} minutes another laptop can take this race.";

        if (vendorsStillOpen is { Count: > 0 })
        {
            message +=
                "\n\nStill open: " + string.Join(", ", vendorsStillOpen) + ".\n\n"
                + "Those programs keep running after this app closes. "
                + "Another laptop can take the race while you are still scoring.";
        }
        else
        {
            message += "\n\nRunScore and Timing & Scoring are closed.";
        }

        message += "\n\nOnly close if you will reopen this app, or setup staff told you to leave.";
        return message;
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
    public static TimerCloseChoice AskCloseWhileAnnouncerActive(Window? owner, bool sessionEnding = false)
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

        var intro = sessionEnding
            ? "Windows tried to shut down or sign out. That is stopped until you choose below.\n\n"
            : string.Empty;

        var card = BuildCard(
            "Announcer Files Still Open",
            intro
            + "This announcer copy stays on this laptop only. Closing the app does not upload anything and does not affect the timing laptop.\n\n"
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
        return StyleCard(stack);
    }

    /// <summary>
    /// Admin PIN step inside a branded card. When <paramref name="verifyCompanyPin"/> is true,
    /// wrong PIN stays on this card with an inline error (no bounce to main).
    /// </summary>
    private static Border BuildAdminPinCard(
        string title,
        string message,
        string confirmLabel,
        Action<string> onConfirmValidPin,
        Action onBack,
        bool verifyCompanyPin,
        string backLabel = "Back")
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
            Margin = new Thickness(0, 0, 0, 16)
        });

        var box = new PasswordBox
        {
            FontSize = 22,
            Padding = new Thickness(12, 10, 12, 10),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MinHeight = 48
        };
        stack.Children.Add(box);

        var error = new TextBlock
        {
            Text = string.Empty,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("DangerBrush"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 10, 0, 0)
        };
        stack.Children.Add(error);

        void Submit()
        {
            var entered = box.Password ?? string.Empty;
            if (verifyCompanyPin
                && !string.Equals(entered, AppSettings.DefaultAdminPin, StringComparison.Ordinal))
            {
                error.Text = "Admin PIN did not match. Try again.";
                error.Visibility = Visibility.Visible;
                box.Clear();
                box.Focus();
                return;
            }

            onConfirmValidPin(entered);
        }

        var confirm = MakeButton(confirmLabel, "DangerButton", Submit);
        confirm.Margin = new Thickness(0, 20, 0, 0);
        confirm.IsDefault = true;
        stack.Children.Add(confirm);

        var back = MakeButton(backLabel, "SecondaryButton", onBack);
        back.Margin = new Thickness(0, 10, 0, 0);
        back.IsCancel = true;
        stack.Children.Add(back);

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Submit();
                e.Handled = true;
            }
        };

        var card = StyleCard(stack);
        card.Loaded += (_, _) => box.Focus();
        return card;
    }

    private static Border StyleCard(UIElement child) =>
        new()
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
            Child = child
        };

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
