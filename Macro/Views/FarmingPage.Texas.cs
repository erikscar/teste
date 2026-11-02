using System.Runtime.InteropServices;
using System.Windows;
using Macro.Services;

namespace Macro.Views;

public partial class FarmingPage
{
    private CancellationTokenSource? _texasCancellation;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private async void StartTexas_Click(object sender, RoutedEventArgs e)
    {
        if (_texasCancellation is not null) return;
        if (_runCancellation is not null)
        {
            TexasStatusText.Text = "Pare as outras rotinas antes de iniciar Texas.";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _texasCancellation = cancellation;
        StartTexasButton.IsEnabled = false;
        StopTexasButton.IsEnabled = true;
        BtnStart.IsEnabled = false;
        var token = cancellation.Token;
        try
        {
            var target = LauncherWindowResolver.Resolve(_windowCaptureService.ListWindows())
                .GetValueOrDefault("MIR4 Launcher 1")
                ?? throw new InvalidOperationException("MIR4 Launcher 1 (Mir4G[1]) não está aberto.");
            await _windowClickService.MaximizeAndActivateAsync(target, token);
            TexasStatusText.Text = "Texas ativo · Launcher 1 · aguardando Mouse4";
            AppendLog("Texas: Launcher 1 maximizado; Mouse4 clica no botão acima de Amovível. Use Stop Texas para parar.");
            var wasPressed = (GetAsyncKeyState(0x05) & 0x8000) != 0; // VK_XBUTTON1 / Mouse4
            while (true)
            {
                await Task.Delay(1, token);
                var pressed = (GetAsyncKeyState(0x05) & 0x8000) != 0;
                var newPress = pressed && !wasPressed;
                wasPressed = pressed;
                // Não captura cliques laterais feitos em outros aplicativos.
                if (!newPress || _windowClickService.CaptureForegroundWindow() != target.Handle) continue;
                // Centro do botão no print: (1865, 325), descontando a barra de título de 23 px.
                await _windowClickService.ClickRelativeAndRestoreInstantAsync(target, 1865d / 1920, 302d / 1007, token);
                AppendLog("Texas: clique enviado acima de Amovível; cursor devolvido à posição inicial.");
                wasPressed = (GetAsyncKeyState(0x05) & 0x8000) != 0;
            }
        }
        catch (OperationCanceledException) { TexasStatusText.Text = "Texas parado"; }
        catch (Exception ex)
        {
            TexasStatusText.Text = ex.Message;
            AppendLog($"Texas: {ex.Message}");
        }
        finally
        {
            _texasCancellation = null;
            StartTexasButton.IsEnabled = true;
            StopTexasButton.IsEnabled = false;
            BtnStart.IsEnabled = true;
        }
    }

    private void StopTexas_Click(object sender, RoutedEventArgs e) => _texasCancellation?.Cancel();
}
