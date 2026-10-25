using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Macro;

public partial class MainWindow : Window
{
    private readonly Views.FarmingPage _farmingPage;
    private readonly Views.DailyPage _dailyPage;
    private readonly Views.TimerPage _timerPage;
    private readonly Views.HomePage _homePage;
    private readonly Views.PresetsPage _presetsPage;
    private readonly Views.CaptureTestPage _captureTestPage;
    private readonly Views.InitialSettingsPage _initialSettingsPage;
    private readonly System.Windows.Threading.DispatcherTimer _sidebarRefresh = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
            new Uri("pack://application:,,,/Assets/app-v2.ico", UriKind.Absolute));
        _farmingPage = new Views.FarmingPage();
        _dailyPage = new Views.DailyPage();
        _timerPage = new Views.TimerPage();
        _homePage = new Views.HomePage(_farmingPage);
        _presetsPage = new Views.PresetsPage();
        _captureTestPage = new Views.CaptureTestPage();
        _initialSettingsPage = new Views.InitialSettingsPage();
        MainFrame.Navigate(_homePage);
        _sidebarRefresh.Tick += (_, _) => RefreshSidebarActivity();
        Loaded += (_, _) => { RefreshSidebarActivity(); _sidebarRefresh.Start(); };
        Closed += (_, _) => _sidebarRefresh.Stop();
    }

    private void RefreshSidebarActivity()
    {
        SidebarRunStatus.Text = _farmingPage.RunStatus;
        var lines = _farmingPage.RunLog.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var activity = lines.Length == 0 ? "Inicie um preset para acompanhar a execução aqui."
            : string.Join(Environment.NewLine + Environment.NewLine, lines.TakeLast(5));
        if (SidebarActivity.Text != activity)
        {
            SidebarActivity.Text = activity;
            SidebarActivity.ScrollToEnd();
        }
    }

    private void BtnHome_Click(object sender, RoutedEventArgs e) => MainFrame.Navigate(_homePage);
    private void BtnPresets_Click(object sender, RoutedEventArgs e) => MainFrame.Navigate(_presetsPage);

    private void BtnFarming_Click(object sender, RoutedEventArgs e) => MainFrame.Navigate(_farmingPage);
    private void BtnInitialSettings_Click(object sender, RoutedEventArgs e) => MainFrame.Navigate(_initialSettingsPage);
    private void BtnTimer_Click(object sender, RoutedEventArgs e) => MainFrame.Navigate(_timerPage);
    private void BtnDaily_Click(object sender, RoutedEventArgs e) => MainFrame.Navigate(_dailyPage);
    private void BtnCapture_Click(object sender, RoutedEventArgs e) => MainFrame.Navigate(_captureTestPage);

    private void MainFrame_Navigated(object sender, NavigationEventArgs e)
    {
        DeactivateButton(BtnFarming);
        DeactivateButton(BtnTimer);
        DeactivateButton(BtnDaily);
        DeactivateButton(BtnHome);
        DeactivateButton(BtnPresets);
        DeactivateButton(BtnCapture);
        DeactivateButton(BtnInitialSettings);
        if (e.Content is Views.FarmingPage) ActivateButton(BtnFarming);
        else if (e.Content is Views.TimerPage) ActivateButton(BtnTimer);
        else if (e.Content is Views.DailyPage) ActivateButton(BtnDaily);
        else if (e.Content is Views.HomePage) ActivateButton(BtnHome);
        else if (e.Content is Views.PresetsPage) ActivateButton(BtnPresets);
        else if (e.Content is Views.CaptureTestPage) ActivateButton(BtnCapture);
        else if (e.Content is Views.InitialSettingsPage) ActivateButton(BtnInitialSettings);
    }

    private static void ActivateButton(Button button)
    {
        button.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x2D, 0x22));
        button.BorderThickness = new Thickness(1);
        button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x70, 0x5B, 0x38));
        button.Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xCF, 0x98));
    }

    private static void DeactivateButton(Button button)
    {
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(1);
        button.BorderBrush = Brushes.Transparent;
        button.Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xC9, 0xC3));
    }
}
