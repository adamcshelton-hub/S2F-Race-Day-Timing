using System.Windows;
using Velopack;

namespace S2F.RaceDayCheckout;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before WPF starts — handles install/update hooks.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
