using System.Windows;
using S2F.RaceDayCheckout.Services;
using Velopack;

namespace S2F.RaceDayCheckout;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before WPF starts — handles install/update hooks.
        VelopackApp.Build()
            .SetAppUserModelId(TaskbarPinPrompt.AppUserModelId)
            .OnFirstRun(_ => TaskbarPinPrompt.MarkOfferPending())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
