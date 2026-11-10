using System.Diagnostics;
using System.Windows;
namespace MineOrbit.UI;
public partial class LoginHelpWindow : Window
{
    public LoginHelpWindow() => InitializeComponent();
    private void OpenEntra(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://entra.microsoft.com/") { UseShellExecute = true });
    private void OpenApiHelp(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/xboxauthnet.game.msal/clientid/") { UseShellExecute = true });
}
