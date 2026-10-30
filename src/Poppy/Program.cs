using Velopack;

namespace Poppy;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args is ["--diag-thumb", var file, var png])
        {
            ShellThumbnail.Save(file, png);
            return;
        }
        // Must run first: handles install/uninstall/update hooks of the GitHub (Setup.exe) edition.
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
