using System.Windows;
using System.Windows.Interop;
using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Services;

namespace S2F.RaceDayCheckout;

public partial class ManualBackupFloatWindow : Window
{
    public ManualBackupFloatWindow()
    {
        InitializeComponent();
        // Unowned Topmost tool — do not Owner-link Main Timing and do not
        // rewrite Z-order of Main / RSServer on activate (that stole focus).
        Owner = null;
        SourceInitialized += (_, _) =>
        {
            var helper = new WindowInteropHelper(this);
            helper.Owner = IntPtr.Zero;
        };
        Loaded += (_, _) =>
        {
            PlaceBottomRight();
            Activate();
            EntryPanel.FocusBibSoon();
        };
        Activated += (_, _) => EntryPanel.FocusBibSoon();
    }

    /// <summary>Default open: bottom-right of the primary work area (above the taskbar).</summary>
    private void PlaceBottomRight()
    {
        const double margin = 12;
        if (ActualWidth <= 0 || ActualHeight <= 0)
            UpdateLayout();

        var work = SystemParameters.WorkArea;
        var left = work.Right - ActualWidth - margin;
        var top = work.Bottom - ActualHeight - margin;
        if (left < work.Left) left = work.Left;
        if (top < work.Top) top = work.Top;
        Left = left;
        Top = top;
    }

    public void Bind(
        ManualBackupCoordinator coordinator,
        Func<string, string, ManualBackupTap?>? submit = null)
    {
        EntryPanel.ShowOpenStatus = true;
        EntryPanel.Bind(coordinator, submit);
    }
}
