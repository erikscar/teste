using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Macro.Services;
using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;

namespace Macro.Views;

public partial class CaptureTestPage : Page
{
    private readonly WindowPreviewService _service = new();
    private readonly TemplateSearchService _vision = new();
    private readonly WindowClickService _input = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _selectingRegion;
    private Point _selectionStartPixel;
    private Rect? _selectedPixelRegion;

    public CaptureTestPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshWindows();
        Preview.SizeChanged += (_, _) => RedrawSelection();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private void ChooseTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Escolha a imagem que deseja localizar",
            Filter = "Imagens|*.png;*.bmp;*.jpg;*.jpeg|Todos os arquivos|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) TemplatePath.Text = dialog.FileName;
    }

    private void RefreshWindows()
    {
        try
        {
            WindowList.ItemsSource = _service.ListWindows();
            WindowList.SelectedIndex = 0;
            Preview.Source = null;
            ClearSelection();
            WriteLog(WindowList.Items.Count == 0
                ? "Nenhuma janela de processo MIR4 encontrada. Abra o jogo e atualize a lista."
                : $"{WindowList.Items.Count} janela(s) de processo MIR4 disponível(is).");
        }
        catch (Exception ex) { WriteLog(ex.Message); }
    }

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (WindowList.SelectedItem is not PreviewWindow target)
        {
            WriteLog("Selecione uma janela primeiro.");
            return;
        }
        CaptureButton.IsEnabled = RefreshButton.IsEnabled = WindowList.IsEnabled = false;
        Preview.Source = null;
        try
        {
            await _input.Ensure720pAsync(target, _lifetime.Token);
            var image = await _service.CaptureAsync(target, _lifetime.Token);
            Preview.Source = image;
            ClearSelection();
            WriteLog($"Captura: {image.PixelWidth} × {image.PixelHeight} pixels. {target.Label}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { WriteLog(ex.Message); }
        finally
        {
            CaptureButton.IsEnabled = RefreshButton.IsEnabled = WindowList.IsEnabled = true;
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync(clickWhenFound: false);
    private async void SearchAndClick_Click(object sender, RoutedEventArgs e) => await SearchAsync(clickWhenFound: true);

    private async Task SearchAsync(bool clickWhenFound)
    {
        if (WindowList.SelectedItem is not PreviewWindow target)
        {
            WriteLog("Selecione a janela alvo primeiro.");
            return;
        }
        if (string.IsNullOrWhiteSpace(TemplatePath.Text) || !File.Exists(TemplatePath.Text))
        {
            WriteLog("Escolha um arquivo de imagem válido para procurar.");
            return;
        }
        if (_selectedPixelRegion is null)
        {
            WriteLog("Primeiro arraste sobre a prévia para escolher a região de busca.");
            return;
        }
        if (!TryPercent(RegionX.Text, out var x) || !TryPercent(RegionY.Text, out var y) ||
            !TryPercent(RegionWidth.Text, out var width) || !TryPercent(RegionHeight.Text, out var height) ||
            !double.TryParse(Confidence.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var confidence))
        {
            WriteLog("Confira os percentuais da região e a confiança mínima.");
            return;
        }
        RelativeSearchRegion region;
        try
        {
            region = new(x / 100, y / 100, width / 100, height / 100);
            if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
                region.X + region.Width > 1 || region.Y + region.Height > 1 || confidence is <= 0 or > 1)
                throw new ArgumentOutOfRangeException();
        }
        catch
        {
            WriteLog("A região precisa caber na janela (valores entre 0% e 100%); confiança vai de 0,01 a 1,00.");
            return;
        }

        SetBusy(true);
        Preview.Source = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var consecutiveMatches = 0;
        TemplateSearchResult? lastResult = null;
        try
        {
            WriteLog($"Procurando {Path.GetFileName(TemplatePath.Text)} na região X={x:0.#}%, Y={y:0.#}%, {width:0.#}% × {height:0.#}%.");
            await _input.Ensure720pAsync(target, _lifetime.Token);
            deadline = DateTime.UtcNow.AddSeconds(5);
            if (clickWhenFound) await _input.ActivateAsync(target, _lifetime.Token);
            while (DateTime.UtcNow < deadline)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                var frame = await _service.CaptureAsync(target, _lifetime.Token);
                var result = await _vision.FindAsync(frame, TemplatePath.Text, region, confidence, _lifetime.Token);
                lastResult = result;
                if (result.Found) consecutiveMatches++;
                else consecutiveMatches = 0;
                if (consecutiveMatches >= 3)
                {
                    Preview.Source = result.AnnotatedFrame;
                    var centerX = result.Bounds.X + result.Bounds.Width / 2d;
                    var centerY = result.Bounds.Y + result.Bounds.Height / 2d;
                    WriteLog($"Imagem confirmada em 3 capturas. Confiança {result.Confidence:0.00}; centro ({centerX:0}, {centerY:0}) de {frame.PixelWidth}×{frame.PixelHeight}.");
                    if (clickWhenFound)
                    {
                        var click = await _input.ClickRelativeAsync(target, centerX / frame.PixelWidth, centerY / frame.PixelHeight, _lifetime.Token, WriteLog);
                        WriteLog($"Windows aceitou o clique em X={click.X}, Y={click.Y} da tela; resposta do jogo não verificada.");
                    }
                    return;
                }
                await Task.Delay(100, _lifetime.Token);
            }
            if (lastResult is not null)
            {
                Preview.Source = lastResult.AnnotatedFrame;
                WriteLog($"Não confirmou a imagem em 5 segundos. Melhor confiança: {lastResult.Confidence:0.00} (mínimo {confidence:0.00}). Região de busca marcada em azul.");
            }
            else WriteLog("Não foi possível capturar a janela dentro do tempo limite.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { WriteLog(ex.Message); }
        finally { SetBusy(false); }
    }

    private static bool TryPercent(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Preview.Source is not BitmapSource frame || !TryGetImageLayout(frame, out var layout)) return;
        var point = e.GetPosition(PreviewSurface);
        if (!layout.Display.Contains(point)) return;
        _selectionStartPixel = ToImagePixel(point, layout);
        _selectingRegion = true;
        _selectedPixelRegion = new Rect(_selectionStartPixel, new Size(1, 1));
        PreviewSurface.CaptureMouse();
        e.Handled = true;
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_selectingRegion || Preview.Source is not BitmapSource frame || !TryGetImageLayout(frame, out var layout)) return;
        var current = ToImagePixel(e.GetPosition(PreviewSurface), layout);
        _selectedPixelRegion = MakeRect(_selectionStartPixel, current);
        RedrawSelection();
    }

    private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_selectingRegion || Preview.Source is not BitmapSource frame || !TryGetImageLayout(frame, out var layout)) return;
        _selectingRegion = false;
        PreviewSurface.ReleaseMouseCapture();
        var current = ToImagePixel(e.GetPosition(PreviewSurface), layout);
        var selection = MakeRect(_selectionStartPixel, current);
        if (selection.Width < 4 || selection.Height < 4)
        {
            ClearSelection();
            WriteLog("A região é muito pequena. Arraste para marcar uma área maior.");
            return;
        }
        _selectedPixelRegion = selection;
        RegionX.Text = Percent(selection.X / frame.PixelWidth);
        RegionY.Text = Percent(selection.Y / frame.PixelHeight);
        RegionWidth.Text = Percent(selection.Width / frame.PixelWidth);
        RegionHeight.Text = Percent(selection.Height / frame.PixelHeight);
        RedrawSelection();
        WriteLog($"Região selecionada: X={RegionX.Text}%, Y={RegionY.Text}%, {RegionWidth.Text}% × {RegionHeight.Text}%.");
        e.Handled = true;
    }

    private void ClearSelection()
    {
        _selectedPixelRegion = null;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        RegionX.Text = RegionY.Text = "0";
        RegionWidth.Text = RegionHeight.Text = "100";
    }

    private void RedrawSelection()
    {
        if (_selectedPixelRegion is not Rect pixels || Preview.Source is not BitmapSource frame ||
            !TryGetImageLayout(frame, out var layout))
        {
            SelectionRectangle.Visibility = Visibility.Collapsed;
            return;
        }
        Canvas.SetLeft(SelectionRectangle, layout.Display.X + pixels.X * layout.Scale);
        Canvas.SetTop(SelectionRectangle, layout.Display.Y + pixels.Y * layout.Scale);
        SelectionRectangle.Width = pixels.Width * layout.Scale;
        SelectionRectangle.Height = pixels.Height * layout.Scale;
        SelectionRectangle.Visibility = Visibility.Visible;
    }

    private bool TryGetImageLayout(BitmapSource frame, out ImageLayout layout)
    {
        var scale = Math.Min(PreviewSurface.ActualWidth / frame.PixelWidth, PreviewSurface.ActualHeight / frame.PixelHeight);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            layout = default;
            return false;
        }
        var width = frame.PixelWidth * scale;
        var height = frame.PixelHeight * scale;
        var left = (PreviewSurface.ActualWidth - width) / 2;
        var top = (PreviewSurface.ActualHeight - height) / 2;
        layout = new(scale, new Rect(left, top, width, height), frame.PixelWidth, frame.PixelHeight);
        return true;
    }

    private static Point ToImagePixel(Point point, ImageLayout layout) => new(
        Math.Clamp((point.X - layout.Display.X) / layout.Scale, 0, layout.PixelWidth),
        Math.Clamp((point.Y - layout.Display.Y) / layout.Scale, 0, layout.PixelHeight));

    private static Rect MakeRect(Point start, Point end) => new(
        Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
        Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));

    private static string Percent(double value) => (value * 100).ToString("0.##", CultureInfo.CurrentCulture);
    private readonly record struct ImageLayout(double Scale, Rect Display, int PixelWidth, int PixelHeight);

    private void SetBusy(bool busy)
    {
        CaptureButton.IsEnabled = RefreshButton.IsEnabled = WindowList.IsEnabled = !busy;
        SearchButton.IsEnabled = SearchAndClickButton.IsEnabled = !busy;
    }

    private void WriteLog(string message)
    {
        // Mantém apenas um histórico curto neste protótipo.
        if (Log.LineCount > 100) Log.Clear();
        Log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        Log.ScrollToEnd();
    }
}
