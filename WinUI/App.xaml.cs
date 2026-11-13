using Microsoft.UI.Xaml;
namespace ThumbLabWinUI;
public partial class App : Application
{
    private Window mainWindow;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, error) =>
        {
            try
            {
                string folder = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "ThumbLab");
                System.IO.Directory.CreateDirectory(folder);
                System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "last-ui-error.txt"), error.Exception.ToString());
            }
            catch (System.Exception) { }
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        mainWindow = new MainWindow();
        mainWindow.Activate();
    }
}
