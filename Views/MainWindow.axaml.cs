using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PMCL.Models;
using PMCL.ViewModels;

namespace PMCL.Views;

/// <summary>
/// Borderless shell window. Hosts the custom title bar (drag / maximize /
/// minimize / close), reacts to the configured close action and keeps the
/// launcher available from the notification-area icon while the game runs.
/// </summary>
public partial class MainWindow : Window
{
    private LauncherSession? _session;
    private bool _isExiting;
    private bool _isHiddenToTray;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// True while the window is hidden and only the tray icon remains.
    /// </summary>
    public bool IsHiddenToTray => _isHiddenToTray;

    private LauncherSession? Session => (DataContext as MainViewModel)?.Session;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_session is not null)
        {
            _session.GameStarted -= OnGameStarted;
            _session.GameExited -= OnGameExited;
        }

        _session = Session;

        if (_session is not null)
        {
            _session.GameStarted += OnGameStarted;
            _session.GameExited += OnGameExited;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (e.Cancel || _isExiting)
            return;

        if (Session?.Settings.CloseAction != LauncherCloseAction.MinimizeToTray)
            return;

        e.Cancel = true;
        HideToTray();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != WindowStateProperty)
            return;

        if (MaximizeButton is not null)
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    /// <summary>
    /// Hides the window without shutting the launcher down so downloads and
    /// the game session keep running in the background.
    /// </summary>
    public void HideToTray()
    {
        _isHiddenToTray = true;
        ShowInTaskbar = false;
        Hide();
        Session?.SetStatus("已最小化到托盘，双击托盘图标可重新打开启动器");
    }

    /// <summary>
    /// Restores the window from the notification area.
    /// </summary>
    public void ShowFromTray()
    {
        _isHiddenToTray = false;
        ShowInTaskbar = true;
        Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
    }

    /// <summary>
    /// Closes every window and terminates the launcher, bypassing the
    /// minimize-to-tray close action.
    /// </summary>
    public void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    private void OnGameStarted(object? sender, GameStartedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            var action = Session?.Settings.OnGameLaunchAction ?? GameLaunchWindowAction.None;
            switch (action)
            {
                case GameLaunchWindowAction.MinimizeToTray:
                    HideToTray();
                    break;
                case GameLaunchWindowAction.CloseLauncher:
                    ExitApplication();
                    break;
            }
        });

    private void OnGameExited(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (!_isHiddenToTray || Session?.Settings.RestoreAfterGameExit != true)
                return;

            ShowFromTray();
        });

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount > 1
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || IsCaptionButton(e.Source))
            return;

        if (WindowState == WindowState.Maximized)
            return;

        BeginMoveDrag(e);
    }

    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsCaptionButton(e.Source))
            return;

        ToggleMaximize();
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object? sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
        => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private static bool IsCaptionButton(object? source)
    {
        if (source is not Visual visual)
            return false;

        for (Visual? current = visual; current is not null; current = current.GetVisualParent())
        {
            if (current is Button)
                return true;
        }

        return false;
    }
}