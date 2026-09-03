using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FrameBouncer.Services;
using FrameBouncer.ViewModels;
using Application = System.Windows.Application;

namespace FrameBouncer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _isRealClose;
    private bool _skipExitReset;
    private IntPtr _mouseHookId;
    private NativeMethods.HookProc? _mouseHookDelegate;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        viewModel.RequestClose += () => Close();
        viewModel.RequestMinimize += OnRequestMinimize;
        viewModel.RequestRestore += OnRequestRestore;
        // Update-Ablauf: App muss sich WIRKLICH beenden (auch bei Tray-Modus),
        // damit der Updater die Dateien ersetzen kann (Spec Punkt 10/15).
        // Kein Limit-Reset: der Updater startet die App neu und die Profile
        // werden dort automatisch wieder angewendet.
        viewModel.RequestForceExit += ExitAppForUpdate;

        Closing += OnClosing;

        PreviewKeyDown += OnPreviewKeyDown;

        try
        {
            InitTrayIcon();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FrameBouncer] Tray init failed: {ex.Message}");
        }

        Show();

        // Live-Sprachwechsel: Tray-Texte sofort mit übersetzen.
        Localization.LanguageChanged += UpdateTrayTexts;
    }

    private System.Windows.Forms.ToolStripMenuItem? _trayShowItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayExitItem;

    private void InitTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon();
        _trayIcon.Icon = CreateTrayIcon();
        _trayIcon.Text = Localization.T("Tray.Text");
        _trayIcon.Visible = false;

        _trayIcon.DoubleClick += (_, _) => ShowFromTray();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        _trayShowItem = new System.Windows.Forms.ToolStripMenuItem(Localization.T("Tray.ShowWindow"), null, (_, _) => ShowFromTray());
        _trayExitItem = new System.Windows.Forms.ToolStripMenuItem(Localization.T("Tray.Exit"), null, (_, _) => ExitApp());
        menu.Items.Add(_trayShowItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_trayExitItem);

        _trayIcon.ContextMenuStrip = menu;
    }

    /// <summary>Re-localizes the tray tooltip and menu after a language switch.</summary>
    private void UpdateTrayTexts()
    {
        if (_trayIcon is not null)
            _trayIcon.Text = Localization.T("Tray.Text");
        if (_trayShowItem is not null)
            _trayShowItem.Text = Localization.T("Tray.ShowWindow");
        if (_trayExitItem is not null)
            _trayExitItem.Text = Localization.T("Tray.Exit");
    }

    private static System.Drawing.Icon CreateTrayIcon()
    {
        // Versuche app.ico aus EXE-Verzeichnis zu laden
        var icoPath = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "app.ico");

        if (System.IO.File.Exists(icoPath))
        {
            try
            {
                return new System.Drawing.Icon(icoPath);
            }
            catch { }
        }

        // Fallback: Icon aus Ressource
        try
        {
            var uri = new System.Uri("pack://application:,,,/app.ico", System.UriKind.Absolute);
            var stream = System.Windows.Application.GetResourceStream(uri);
            if (stream is not null)
            {
                return new System.Drawing.Icon(stream.Stream);
            }
        }
        catch { }

        // Letzter Fallback
        return System.Drawing.SystemIcons.Application;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isRealClose)
        {
            // Wirklich schließen: alle in dieser Session angewendeten Limits
            // aufheben, damit Spiele nach dem Beenden wieder unlimitiert laufen.
            FinishRealClose();
            return;
        }

        if (_viewModel.MinimizeToTray)
        {
            // Tray-Modus: Fenster verstecken, Icon im Tray anzeigen
            e.Cancel = true;
            Hide();
            if (_trayIcon is not null)
                _trayIcon.Visible = true;
            return;
        }

        // Kein Tray: Programm schließen (Reset + Tray aufräumen)
        _isRealClose = true;
        FinishRealClose();
    }

    /// <summary>
    /// Echter Exit: setzt alle von FrameBouncer in dieser Session angewendeten
    /// RTSS-Limits auf 0 zurück (manuelles Apply UND Auto-Apply – unabhängig vom
    /// Frame-Timer, der bei Auto-Apply nicht läuft). Beim Update-Ablauf wird der
    /// Reset übersprungen (die neue App-Instanz re-appliziert die Profile).
    /// </summary>
    private void FinishRealClose()
    {
        if (!_skipExitReset)
        {
            try
            {
                _viewModel.ResetFpsLimit();
            }
            catch
            {
                // Ein Reset-Fehler darf das Beenden niemals blockieren.
            }
        }
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon is not null)
            _trayIcon.Visible = false;
    }

    private void ExitApp()
    {
        _isRealClose = true;
        Close();
    }

    /// <summary>Update-Ablauf: beenden OHNE Limit-Reset (siehe FinishRealClose).</summary>
    private void ExitAppForUpdate()
    {
        _skipExitReset = true;
        ExitApp();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is System.Windows.Shapes.Path or System.Windows.Controls.Border or System.Windows.Controls.Grid or System.Windows.Controls.Panel)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && _viewModel.IsPickingWindow)
        {
            _viewModel.CancelPickCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRequestMinimize()
    {
        WindowState = WindowState.Minimized;
        InstallMouseHook();
    }

    private void OnRequestRestore()
    {
        UninstallMouseHook();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void InstallMouseHook()
    {
        _mouseHookDelegate = MouseHookCallback;
        _mouseHookId = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _mouseHookDelegate,
            NativeMethods.GetModuleHandle(Process.GetCurrentProcess().MainModule!.ModuleName),
            0);
    }

    private void UninstallMouseHook()
    {
        if (_mouseHookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
            _mouseHookDelegate = null;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)NativeMethods.WM_LBUTTONUP)
        {
            UninstallMouseHook();
            Dispatcher.BeginInvoke(new Action(() => _viewModel.CompletePick()));
        }
        return NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }
}
