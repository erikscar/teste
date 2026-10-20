using System.Runtime.InteropServices;

namespace Macro.Services;

public sealed class WindowClickService
{
    public async Task ClickBackgroundAsync(PreviewWindow target, int x, int y,
        int frameWidth, int frameHeight, CancellationToken token)
    {
        ValidateTarget(target);
        token.ThrowIfCancellationRequested();
        nint position;
        var previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            if (!GetClientRect(target.Handle, out var rect) || rect.Right <= 0 || rect.Bottom <= 0)
                throw new InvalidOperationException("Não foi possível medir a área do jogo.");
            if (rect.Right != frameWidth || rect.Bottom != frameHeight)
                throw new InvalidOperationException("A janela mudou de tamanho após a detecção. Faça o teste novamente.");
            if (x < 0 || y < 0 || x >= rect.Right || y >= rect.Bottom || x > short.MaxValue || y > short.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(x));
            position = new nint((y << 16) | (x & 0xffff));
        }
        finally
        {
            if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
        }

        await SendBackgroundMouseAsync(target, 0x0200, 0, position);
        await Task.Delay(150, token);
        token.ThrowIfCancellationRequested();
        // Release even if button-down times out: delivery may already have occurred.
        try
        {
            await SendBackgroundMouseAsync(target, 0x0201, 1, position);
            await Task.Delay(180, token);
        }
        finally { await SendBackgroundMouseAsync(target, 0x0202, 0, position); }
    }

    private static Task SendBackgroundMouseAsync(PreviewWindow target, uint message, nuint buttons, nint position) => Task.Run(() =>
    {
        ValidateWindow(target);
        Marshal.SetLastPInvokeError(0);
        // Wait for window dispatch, with a bounded timeout; this is not proof of an in-game action.
        if (SendMessageTimeout(target.Handle, message, buttons, position, 0x0002 | 0x0020, 750, out _) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(error == 5
                ? "Clique bloqueado (erro 5). Execute o macro com o mesmo nível de permissão do jogo."
                : $"A janela não confirmou o processamento da mensagem 0x{message:X} (erro {error}; possível tempo limite).");
        }
    });

    public async Task Prepare720pAsync(PreviewWindow target, CancellationToken token, Action<string>? log = null)
    {
        await Ensure720pAsync(target, token);
        await ActivateAsync(target, token);
        log?.Invoke($"MIR4 em 1280 × 720 na área do jogo (PID {target.ProcessId}).");
    }

    public async Task Ensure720pAsync(PreviewWindow target, CancellationToken token)
    {
        ValidateWindow(target);
        token.ThrowIfCancellationRequested();
        if (IsZoomed(target.Handle) || IsIconic(target.Handle))
        {
            ShowWindowAsync(target.Handle, 9); // SW_RESTORE
            for (var attempt = 0; attempt < 20 && (IsZoomed(target.Handle) || IsIconic(target.Handle)); attempt++)
                await Task.Delay(50, token);
            if (IsZoomed(target.Handle) || IsIconic(target.Handle))
                throw new InvalidOperationException("Não foi possível restaurar a janela MIR4 para ajustar a resolução.");
        }

        var stableFrames = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            token.ThrowIfCancellationRequested();
            stableFrames = ResizeClientTo720p(target) ? stableFrames + 1 : 0;
            if (stableFrames >= 3) return;
            await Task.Delay(150, token);
        }
        throw new InvalidOperationException("O MIR4 não manteve a área do jogo em 1280 × 720. Verifique se o modo janela permite esse tamanho.");
    }

    private static bool ResizeClientTo720p(PreviewWindow target)
    {
        var oldDpi = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            ValidateWindow(target);
            if (!GetWindowRect(target.Handle, out var outer) || !GetClientRect(target.Handle, out var client))
                throw new InvalidOperationException("Não foi possível medir a janela MIR4.");

            // A área cliente é o quadro do jogo; somamos as bordas para obter o tamanho externo.
            var width = 1280 + outer.Right - outer.Left - client.Right;
            var height = 720 + outer.Bottom - outer.Top - client.Bottom;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(target.Handle, 2), ref info))
                throw new InvalidOperationException("Não foi possível consultar a área disponível do monitor.");
            var work = info.Work;
            if (width > work.Right - work.Left || height > work.Bottom - work.Top)
                throw new InvalidOperationException("A janela MIR4 em 720p não cabe na área disponível do monitor.");

            var x = Math.Clamp(outer.Left, work.Left, work.Right - width);
            var y = Math.Clamp(outer.Top, work.Top, work.Bottom - height);
            if (client.Right == 1280 && client.Bottom == 720 && outer.Left == x && outer.Top == y)
                return true;
            if (!SetWindowPos(target.Handle, 0, x, y, width, height, 0x0004 | 0x0010))
                throw new InvalidOperationException("O Windows recusou ajustar a janela MIR4 para 720p.");
            return false;
        }
        finally { if (oldDpi != 0) SetThreadDpiAwarenessContext(oldDpi); }
    }

    public async Task MaximizeAndActivateAsync(PreviewWindow target, CancellationToken token)
    {
        GetWindowThreadProcessId(target.Handle, out var processId);
        if (!IsWindow(target.Handle) || processId != target.ProcessId)
            throw new InvalidOperationException("A janela alvo não está mais disponível.");
        token.ThrowIfCancellationRequested();
        ShowWindowAsync(target.Handle, 3); // SW_MAXIMIZE restaura e maximiza, inclusive se minimizada.
        await Task.Delay(250, token);
        await ActivateAsync(target, token);
    }

    public async Task ResizeToSmallestAsync(PreviewWindow target, CancellationToken token, Action<string>? log = null)
    {
        ValidateTarget(target);
        token.ThrowIfCancellationRequested();
        if (IsZoomed(target.Handle))
        {
            ShowWindowAsync(target.Handle, 9); // SW_RESTORE: mantém a janela visível.
            for (var attempt = 0; attempt < 20 && IsZoomed(target.Handle); attempt++)
                await Task.Delay(50, token);
            if (IsZoomed(target.Handle))
                throw new InvalidOperationException("Não foi possível restaurar a janela para redimensioná-la.");
        }
        if (!GetWindowRect(target.Handle, out var rect))
            throw new InvalidOperationException("Não foi possível ler o tamanho da janela MIR4.");

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        for (var attempt = 0; attempt < 24; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var requestedWidth = Math.Max(1, (int)Math.Round(width * 0.85));
            var requestedHeight = Math.Max(1, (int)Math.Round(height * 0.85));
            if (requestedWidth == width && requestedHeight == height) break;

            if (!SetWindowPos(target.Handle, 0, rect.Left, rect.Top, requestedWidth, requestedHeight,
                0x0004 | 0x0010)) // SWP_NOZORDER | SWP_NOACTIVATE
                throw new InvalidOperationException("O Windows recusou redimensionar a janela MIR4.");
            await Task.Delay(120, token);
            if (!GetWindowRect(target.Handle, out rect))
                throw new InvalidOperationException("Não foi possível confirmar o novo tamanho da janela MIR4.");

            var newWidth = rect.Right - rect.Left;
            var newHeight = rect.Bottom - rect.Top;
            if (newWidth == width && newHeight == height) break; // O jogo impôs seu tamanho mínimo.
            width = newWidth;
            height = newHeight;
        }

        log?.Invoke($"Janela MIR4 mantida visível após redução: {width} × {height} (PID {target.ProcessId}).");
    }

    public async Task ActivateAsync(PreviewWindow target, CancellationToken token)
    {
        ValidateTarget(target);
        token.ThrowIfCancellationRequested();
        if (GetForegroundWindow() == target.Handle) return;
        SetForegroundWindow(target.Handle);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(50, token);
            if (GetForegroundWindow() == target.Handle)
            {
                await Task.Delay(150, token);
                return;
            }
        }
        throw new InvalidOperationException("O Windows não colocou o MIR4 em primeiro plano. Ative a janela do jogo e tente novamente.");
    }

    public nint CaptureForegroundWindow() => GetForegroundWindow();

    public void RestoreForegroundWindow(nint windowHandle, Action<string>? log = null)
    {
        if (windowHandle == 0 || !IsWindow(windowHandle) || GetForegroundWindow() == windowHandle) return;
        SetForegroundWindow(windowHandle);
        log?.Invoke(GetForegroundWindow() == windowHandle
            ? "Foco retornado à janela que estava ativa antes da rotina."
            : "O Windows manteve o foco em outra janela.");
    }

    public async Task<(int X, int Y)> ClickRelativeAsync(PreviewWindow target, double x, double y,
        CancellationToken token, Action<string>? log = null)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(x));
        log?.Invoke($"Clique: ativando PID {target.ProcessId}, HWND=0x{target.Handle:X}.");
        await ActivateAsync(target, token);
        var point = await MoveCursorAsync(target, x, y, token);
        log?.Invoke($"Clique: movimento SendInput aceito para X={point.X}, Y={point.Y}.");
        await Task.Delay(Random.Shared.Next(160, 281), token);
        VerifyClickTarget(target, point, x, y);
        token.ThrowIfCancellationRequested();
        SendMouse(0x0002, "pressionar");
        try
        {
            log?.Invoke("Clique: botão esquerdo pressionado (SendInput aceito).");
            await Task.Delay(Random.Shared.Next(70, 131), token);
        }
        finally
        {
            // Mesmo ao cancelar, sempre libera o botão que já foi pressionado.
            SendMouse(0x0004, "soltar");
        }
        log?.Invoke("Clique: botão esquerdo solto (SendInput aceito).");
        return (point.X, point.Y);
    }

    public async Task PressEscapeThreeTimesAsync(PreviewWindow target, CancellationToken token, Action<string>? log = null)
    {
        await ActivateAsync(target, token);
        for (var press = 1; press <= 3; press++)
        {
            token.ThrowIfCancellationRequested();
            SendKeyboard(0x1B, 0, $"pressionar Esc ({press}/3)");
            try { await Task.Delay(80, token); }
            finally { SendKeyboard(0x1B, 0x0002, $"soltar Esc ({press}/3)"); }
            log?.Invoke($"Esc {press}/3 enviado para MIR4 (PID {target.ProcessId}).");
            if (press < 3) await Task.Delay(180, token);
        }
    }

    public async Task SwipeRightAsync(PreviewWindow target, CancellationToken token)
    {
        await ActivateAsync(target, token);
        var start = await MoveCursorAsync(target, 0.25, 0.50, token);
        VerifyClickTarget(target, start, 0.25, 0.50);
        token.ThrowIfCancellationRequested();
        SendMouse(0x0002, "iniciar arrasto");
        try
        {
            await Task.Delay(100, token);
            for (var step = 1; step <= 4; step++)
            {
                if (GetForegroundWindow() != target.Handle)
                    throw new InvalidOperationException("MIR4 perdeu o foco durante o arrasto.");
                var x = 0.25 + 0.50 * step / 4;
                var point = await MoveCursorAsync(target, x, 0.50, token);
                VerifyClickTarget(target, point, x, 0.50);
            }
        }
        finally { SendMouse(0x0004, "terminar arrasto"); }
    }

    public async Task PressKeyAsync(PreviewWindow target, ushort virtualKey, string keyName,
        CancellationToken token, Action<string>? log = null)
    {
        await ActivateAsync(target, token);
        token.ThrowIfCancellationRequested();
        SendKeyboard(virtualKey, 0, $"pressionar {keyName}");
        try { await Task.Delay(100, token); }
        finally { SendKeyboard(virtualKey, 0x0002, $"soltar {keyName}"); }
        log?.Invoke($"Tecla {keyName} enviada para MIR4 (PID {target.ProcessId}).");
    }

    public async Task PressEscapeTimesAsync(PreviewWindow target, int count, CancellationToken token,
        Action<string>? log = null)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        await ActivateAsync(target, token);
        for (var press = 1; press <= count; press++)
        {
            token.ThrowIfCancellationRequested();
            SendKeyboard(0x1B, 0, $"pressionar Esc ({press}/{count})");
            try { await Task.Delay(80, token); }
            finally { SendKeyboard(0x1B, 0x0002, $"soltar Esc ({press}/{count})"); }
            log?.Invoke($"Esc {press}/{count} enviado para MIR4 (PID {target.ProcessId}).");
            if (press < count) await Task.Delay(180, token);
        }
    }

    public async Task ReplaceTextAsync(PreviewWindow target, string text, CancellationToken token,
        Action<string>? log = null)
    {
        await ActivateAsync(target, token);
        token.ThrowIfCancellationRequested();
        SendKeyboard(0x11, 0, "pressionar Ctrl");
        try
        {
            SendKeyboard(0x41, 0, "selecionar o texto existente");
            SendKeyboard(0x41, 0x0002, "soltar A");
        }
        finally { SendKeyboard(0x11, 0x0002, "soltar Ctrl"); }

        foreach (var character in text)
        {
            token.ThrowIfCancellationRequested();
            if (character is < '0' or > '9')
                throw new ArgumentException("Este campo aceita apenas dígitos.", nameof(text));
            var key = (ushort)character;
            SendKeyboard(key, 0, $"digitar {character}");
            try { await Task.Delay(45, token); }
            finally { SendKeyboard(key, 0x0002, $"soltar {character}"); }
        }
        log?.Invoke($"Texto substituído no campo MIR4 (PID {target.ProcessId}).");
    }

    public async Task PressCtrlNumberAsync(PreviewWindow target, ushort numberKey, CancellationToken token,
        Action<string>? log = null)
    {
        if (numberKey is < 0x30 or > 0x39) throw new ArgumentOutOfRangeException(nameof(numberKey));
        await ActivateAsync(target, token);
        token.ThrowIfCancellationRequested();
        SendKeyboard(0x11, 0, "pressionar Ctrl");
        try
        {
            SendKeyboard(numberKey, 0, "pressionar número com Ctrl");
            try { await Task.Delay(100, token); }
            finally { SendKeyboard(numberKey, 0x0002, "soltar número com Ctrl"); }
        }
        finally { SendKeyboard(0x11, 0x0002, "soltar Ctrl"); }
        log?.Invoke($"Atalho Ctrl+{numberKey - 0x30} enviado para MIR4 (PID {target.ProcessId}).");
    }

    public async Task ScrollDownAtRelativeAsync(PreviewWindow target, double x, double y,
        CancellationToken token, Action<string>? log = null, int notches = 1)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(x));
        if (notches < 1) throw new ArgumentOutOfRangeException(nameof(notches));
        await ActivateAsync(target, token);
        token.ThrowIfCancellationRequested();
        var point = await MoveCursorAsync(target, x, y, token);
        VerifyClickTarget(target, point, x, y);
        SendMouse(0x0800, "rolar a lista para baixo", data: unchecked((uint)(-120 * notches)));
        log?.Invoke($"Roda do mouse rolada {notches} nível(is) para baixo em X={point.X}, Y={point.Y} (PID {target.ProcessId}).");
    }

    private static void ValidateTarget(PreviewWindow target)
    {
        ValidateWindow(target);
        if (IsIconic(target.Handle))
            throw new InvalidOperationException("A janela alvo está minimizada.");
    }

    private static void ValidateWindow(PreviewWindow target)
    {
        GetWindowThreadProcessId(target.Handle, out var processId);
        if (!IsWindow(target.Handle) || processId != target.ProcessId)
            throw new InvalidOperationException("A janela alvo não está mais disponível.");
    }

    private static NativePoint ResolvePoint(PreviewWindow target, double x, double y)
    {
        ValidateTarget(target);
        if (!GetClientRect(target.Handle, out var rect) || rect.Right <= 0 || rect.Bottom <= 0)
            throw new InvalidOperationException("Não foi possível obter o tamanho da janela.");
        var point = new NativePoint
        {
            X = Math.Clamp((int)Math.Round(x * rect.Right), 0, rect.Right - 1),
            Y = Math.Clamp((int)Math.Round(y * rect.Bottom), 0, rect.Bottom - 1)
        };
        if (!ClientToScreen(target.Handle, ref point))
            throw new InvalidOperationException("Não foi possível converter a posição do ícone para a tela.");
        return point;
    }

    private static NativePoint PositionCursor(PreviewWindow target, double x, double y)
    {
        var oldDpi = SetThreadDpiAwarenessContext(new nint(-4));
        try { return MoveCursorToPoint(ResolvePoint(target, x, y)); }
        finally { if (oldDpi != 0) SetThreadDpiAwarenessContext(oldDpi); }
    }

    private static async Task<NativePoint> MoveCursorAsync(PreviewWindow target, double x, double y, CancellationToken token)
    {
        NativePoint start;
        NativePoint end;
        var oldDpi = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            end = ResolvePoint(target, x, y);
            if (!GetCursorPos(out start))
                throw new InvalidOperationException("Não foi possível ler a posição do mouse.");
        }
        finally { if (oldDpi != 0) SetThreadDpiAwarenessContext(oldDpi); }
        var distance = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
        var duration = Math.Clamp(140 + distance * 0.35 + Random.Shared.Next(0, 121), 160, 650);
        var steps = Math.Max(8, (int)(duration / 16));
        var controlX = start.X + (end.X - start.X) * (0.25 + Random.Shared.NextDouble() * 0.5);
        var controlY = start.Y + (end.Y - start.Y) * (0.25 + Random.Shared.NextDouble() * 0.5);
        for (var step = 1; step <= steps; step++)
        {
            token.ThrowIfCancellationRequested();
            ValidateTarget(target);
            if (GetForegroundWindow() != target.Handle)
                throw new InvalidOperationException("MIR4 perdeu o foco durante o movimento do mouse.");
            var position = MouseVariation.CurvePoint(start.X, start.Y, end.X, end.Y,
                controlX, controlY, step / (double)steps);
            MoveCursorToPoint(new NativePoint
            {
                X = (int)Math.Round(position.X),
                Y = (int)Math.Round(position.Y)
            });
            await Task.Delay((int)(duration / steps), token);
        }
        return end;
    }

    private static NativePoint MoveCursorToPoint(NativePoint point)
    {
        var oldDpi = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            // Usa eventos de movimento, inclusive em desktops com vários monitores.
            var left = GetSystemMetrics(76);
            var top = GetSystemMetrics(77);
            var width = GetSystemMetrics(78);
            var height = GetSystemMetrics(79);
            if (width <= 0 || height <= 0 || point.X < left || point.Y < top ||
                point.X >= left + width || point.Y >= top + height)
                throw new InvalidOperationException("O ponto detectado está fora da área visível dos monitores.");
            var absoluteX = (int)(((long)(point.X - left) * 65536 + 32768) / width);
            var absoluteY = (int)(((long)(point.Y - top) * 65536 + 32768) / height);
            SendMouse(0x0001 | 0x8000 | 0x4000, "mover", absoluteX, absoluteY);
            return point;
        }
        finally { if (oldDpi != 0) SetThreadDpiAwarenessContext(oldDpi); }
    }

    private static void VerifyClickTarget(PreviewWindow target, NativePoint expected, double x, double y)
    {
        var oldDpi = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            var current = ResolvePoint(target, x, y);
            if (current.X != expected.X || current.Y != expected.Y)
                throw new InvalidOperationException("A janela se moveu durante o clique. Execute a busca novamente.");
            if (GetForegroundWindow() != target.Handle || GetAncestor(WindowFromPoint(expected), 2) != target.Handle)
                throw new InvalidOperationException("O ponto detectado está coberto ou o MIR4 perdeu o foco. Clique cancelado.");
            if (!GetCursorPos(out var cursor) || Math.Abs(cursor.X - expected.X) > 2 || Math.Abs(cursor.Y - expected.Y) > 2)
                throw new InvalidOperationException("O cursor saiu do ponto detectado antes do clique. Execute a busca novamente.");
        }
        finally { if (oldDpi != 0) SetThreadDpiAwarenessContext(oldDpi); }
    }

    private static void SendMouse(uint flags, string stage, int x = 0, int y = 0, uint data = 0)
    {
        Input[] inputs = [new() { Type = 0, Data = new() { Mouse = new() { X = x, Y = y, Data = data, Flags = flags } } }];
        Marshal.SetLastPInvokeError(0);
        if (SendInput(1, inputs, Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException($"O Windows recusou {stage} o mouse (erro {Marshal.GetLastWin32Error()}). Se o jogo estiver elevado, execute o macro com o mesmo nível de permissão.");
    }

    private static void SendKeyboard(ushort virtualKey, uint flags, string stage)
    {
        Input[] inputs = [new() { Type = 1, Data = new() { Keyboard = new() { VirtualKey = virtualKey, Flags = flags } } }];
        Marshal.SetLastPInvokeError(0);
        if (SendInput(1, inputs, Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException($"O Windows recusou {stage} (erro {Marshal.GetLastWin32Error()}). Se o jogo estiver elevado, execute o macro com o mesmo nível de permissão.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y,
        int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wParam, nint lParam,
        uint flags, uint timeout, out nuint result);
}
