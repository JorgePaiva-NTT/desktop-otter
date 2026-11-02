using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OtterDesktop;

public partial class MainWindow : Window
{
    private const int CellWidth = 192;
    private const int CellHeight = 208;
    private const long InactivityTimeoutMilliseconds = 5 * 60 * 1000;
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "OtterDesktop";

    private readonly record struct Animation(int Row, int Frames, int[] Durations, bool Loop = false);

    private readonly Dictionary<string, Animation> _animations = new()
    {
        ["idle"] = new(0, 6, [280, 110, 110, 140, 140, 320], true),
        ["running-right"] = new(1, 8, [120, 120, 120, 120, 120, 120, 120, 220], true),
        ["running-left"] = new(2, 8, [120, 120, 120, 120, 120, 120, 120, 220], true),
        ["waving"] = new(3, 4, [140, 140, 140, 280]),
        ["jumping"] = new(4, 5, [140, 140, 140, 140, 280]),
        ["failed"] = new(5, 8, [140, 140, 140, 140, 140, 140, 140, 240]),
        ["waiting"] = new(6, 6, [150, 150, 150, 150, 150, 260]),
        ["running"] = new(7, 6, [120, 120, 120, 120, 120, 220]),
        ["review"] = new(8, 6, [150, 150, 150, 150, 150, 280]),
        ["typing"] = new(8, 6, [180, 150, 150, 180, 150, 300], true),
        ["sleeping"] = new(11, 8, [480, 480, 480, 560, 560, 480, 480, 480], true)
    };

    private readonly Dictionary<(int Row, int Column), CroppedBitmap> _frameCache = new();
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _workTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = new();
    private readonly PetSettings _settings;

    private BitmapImage? _atlas;
    private string _currentAnimation = "idle";
    private int _frameIndex;
    private long _nextFrameAt;
    private long _lookResumeAt;
    private bool _dragging;
    private bool _dragMoved;
    private bool _dragSleeping;
    private bool _closing;
    private bool _walking;
    private double _walkTargetX;
    private double _walkTargetTop;
    private int _walkDirection;
    private long _lastTickAt;
    private long _manualActionUntil;
    private bool _idleSleeping;
    private Point _dragStartCursor;
    private double _dragStartLeft;
    private double _dragStartTop;
    private double _dragLastCursorX;
    private bool _typingMode;
    private bool _quietPoseVisible;
    private bool _teamsMeetingActive;
    private bool _teamsSharingActive;
    private bool _hiddenForSharing;
    private bool _capturePrivacyApplied;
    private bool _fullscreenAppActive;
    private bool _workCheckRunning;
    private GlobalKeyboardActivity? _keyboardActivity;
    private CompanionPropWindow? _ballWindow;
    private CompanionPropWindow? _snackWindow;
    private CancellationTokenSource? _propSequence;
    private Action? _walkArrivalAction;

    public MainWindow()
    {
        InitializeComponent();
        _settings = PetSettings.Load();
        _settings.ManualMeetingMode = false;
        SourceInitialized += (_, _) =>
            _capturePrivacyApplied = WindowPrivacy.Apply(this, _settings.HideFromScreenCapture);
        _renderTimer.Tick += RenderTimer_Tick;
        _workTimer.Tick += WorkTimer_Tick;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadSpriteSheet();
        ApplySettings();
        ClampToWorkArea();
        SetAnimation("idle");
        _lastTickAt = _clock.ElapsedMilliseconds;
        _capturePrivacyApplied = WindowPrivacy.Apply(this, _settings.HideFromScreenCapture);
        _keyboardActivity = new GlobalKeyboardActivity();
        _renderTimer.Start();
        _workTimer.Start();
        _ = RefreshWorkEnvironmentAsync();
    }

    private void LoadSpriteSheet()
    {
        _atlas = new BitmapImage();
        _atlas.BeginInit();
        _atlas.DecodePixelWidth = CellWidth * 8;
        _atlas.UriSource = new Uri("pack://application:,,,/Assets/otter-spritesheet-v2.png", UriKind.Absolute);
        _atlas.CacheOption = BitmapCacheOption.OnLoad;
        _atlas.EndInit();
        _atlas.Freeze();
    }

    private void ApplySettings()
    {
        _settings.Scale = Math.Clamp(_settings.Scale, 0.75, 1.5);
        _settings.AnimationSpeed = NormalizeAnimationSpeed(_settings.AnimationSpeed);
        Width = CellWidth * _settings.Scale;
        Height = CellHeight * _settings.Scale;
        Topmost = _settings.AlwaysOnTop;

        if (_settings.Left is double savedLeft && _settings.Top is double savedTop)
        {
            Left = savedLeft;
            Top = savedTop;
        }
        else
        {
            MoveToDefaultPosition();
        }
    }

    private CroppedBitmap Frame(int row, int column)
    {
        if (_atlas is null)
        {
            throw new InvalidOperationException("The sprite sheet has not loaded.");
        }

        var key = (row, column);
        if (_frameCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var frame = new CroppedBitmap(_atlas, new Int32Rect(column * CellWidth, row * CellHeight, CellWidth, CellHeight));
        frame.Freeze();
        _frameCache[key] = frame;
        return frame;
    }

    private void SetAnimation(string name)
    {
        if (!_animations.ContainsKey(name))
        {
            return;
        }

        _currentAnimation = name;
        _quietPoseVisible = false;
        _frameIndex = 0;
        _lookResumeAt = _clock.ElapsedMilliseconds + 650;
        PetImage.Source = Frame(_animations[name].Row, 0);
        _nextFrameAt = _clock.ElapsedMilliseconds + FrameDuration(name, 0);
    }

    private void RenderTimer_Tick(object? sender, EventArgs e)
    {
        if (_atlas is null)
        {
            return;
        }

        var now = _clock.ElapsedMilliseconds;
        var elapsed = Math.Clamp((now - _lastTickAt) / 1000.0, 0, 0.1);
        _lastTickAt = now;

        if (_hiddenForSharing)
        {
            return;
        }

        if (_settings.Paused)
        {
            return;
        }

        var quietMode = IsQuietModeActive();
        if (_currentAnimation == "sleeping" && !quietMode && !_dragSleeping)
        {
            SetAnimation("idle");
        }

        if (!quietMode && _quietPoseVisible && !_dragSleeping)
        {
            LeaveQuietPose();
        }
        var typing = _settings.ReactToTyping &&
                     _keyboardActivity?.IsTyping == true &&
                     !IsManualActionActive(now) &&
                     !quietMode;

        if (typing && !_typingMode && !_dragging)
        {
            _typingMode = true;
            StopWalking();
            SetAnimation("typing");
            _frameIndex = 4;
            PetImage.Source = Frame(_animations["typing"].Row, _frameIndex);
        }
        else if (!typing && _typingMode)
        {
            _typingMode = false;
            SetAnimation("idle");
        }

        if (_dragging)
        {
            if (_dragSleeping)
            {
                ShowSleepingPose(now, true);
            }
            else if (_dragMoved && now >= _nextFrameAt)
            {
                AdvanceAnimation(now);
            }
        }
        else if (_walking && (!quietMode || IsManualActionActive(now)))
        {
            UpdateWalk(elapsed);
            if (_walking && now >= _nextFrameAt)
            {
                AdvanceAnimation(now);
            }
        }
        else if (quietMode && !IsManualActionActive(now))
        {
            ShowQuietPose();
            return;
        }
        else if (_typingMode)
        {
            _quietPoseVisible = false;
            return;
        }
        else if (_currentAnimation == "idle" && now >= _lookResumeAt && TryShowLookFrame())
        {
            // Cursor-driven look frames intentionally suspend the idle loop.
        }
        else if (now >= _nextFrameAt)
        {
            AdvanceAnimation(now);
        }

    }

    private bool IsQuietModeActive() =>
        _idleSleeping || IsWorkQuietModeActive();

    private bool IsWorkQuietModeActive() =>
        IsMeetingModeActive() ||
        (_settings.QuietDuringFullscreenApps && _fullscreenAppActive);

    private bool IsMeetingModeActive() =>
        _settings.ManualMeetingMode ||
        (_settings.QuietDuringTeamsMeetings && _teamsMeetingActive);

    private bool IsManualActionActive(long now) =>
        now < _manualActionUntil || _propSequence is not null ||
        (_walking && _walkArrivalAction is not null);

    private void ShowQuietPose()
    {
        ShowSleepingPose(_clock.ElapsedMilliseconds, true);
        _quietPoseVisible = true;
    }

    private void ShowSleepingPose(long now, bool animate)
    {
        StopWalking();
        _typingMode = false;
        if (_currentAnimation != "sleeping")
        {
            SetAnimation("sleeping");
        }
        if (animate && now >= _nextFrameAt)
        {
            AdvanceAnimation(now);
        }
    }

    private void LeaveQuietPose()
    {
        if (!_quietPoseVisible)
        {
            return;
        }

        _quietPoseVisible = false;
        SetAnimation("idle");
    }

    private void AdvanceAnimation(long now)
    {
        var animation = _animations[_currentAnimation];
        _frameIndex++;

        if (_frameIndex >= animation.Frames)
        {
            if (animation.Loop)
            {
                _frameIndex = 0;
            }
            else
            {
                SetAnimation("idle");
                return;
            }
        }

        PetImage.Source = Frame(animation.Row, _frameIndex);
        _nextFrameAt = now + FrameDuration(_currentAnimation, _frameIndex);
    }

    private int FrameDuration(string animation, int frameIndex) =>
        Math.Max(1, (int)Math.Round(_animations[animation].Durations[frameIndex] / _settings.AnimationSpeed));

    private static double NormalizeAnimationSpeed(double speed) =>
        double.IsFinite(speed) ? Math.Clamp(speed, 0.5, 2.0) : 1.0;

    private bool TryShowLookFrame()
    {
        if (!GetCursorPos(out var cursorPixels))
        {
            return false;
        }

        var cursor = ScreenPixelsToDips(cursorPixels.X, cursorPixels.Y);
        var centerX = Left + Width / 2;
        var centerY = Top + Height * 0.38;
        var dx = cursor.X - centerX;
        var dy = cursor.Y - centerY;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < 35 || distance > 520)
        {
            return false;
        }

        var angle = Math.Atan2(dx, -dy) * 180 / Math.PI;
        if (angle < 0)
        {
            angle += 360;
        }

        var direction = (int)Math.Round(angle / 22.5) % 16;
        var row = direction < 8 ? 9 : 10;
        var column = direction % 8;
        PetImage.Source = Frame(row, column);
        _nextFrameAt = _clock.ElapsedMilliseconds + 100;
        return true;
    }

    private Point ScreenPixelsToDips(int x, int y)
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget is { } target
            ? target.TransformFromDevice.Transform(new Point(x, y))
            : new Point(x, y);
    }

    private void UpdateWalk(double seconds)
    {
        var speed = 72 * _settings.Scale;
        var distanceX = _walkTargetX - Left;
        var distanceY = _walkTargetTop - Top;
        var distance = Math.Sqrt(distanceX * distanceX + distanceY * distanceY);
        var step = speed * seconds;

        if (distance <= step)
        {
            Left = _walkTargetX;
            Top = _walkTargetTop;
            _walking = false;
            _walkDirection = 0;
            var arrivalAction = _walkArrivalAction;
            _walkArrivalAction = null;
            SetAnimation("idle");
            SaveSettings();
            arrivalAction?.Invoke();
        }
        else
        {
            Left += distanceX / distance * step;
            Top += distanceY / distance * step;
        }
    }

    private void BeginWalkTo(double targetLeft, Action arrivalAction)
    {
        var area = GetCurrentWorkArea();
        _walkTargetX = Math.Clamp(targetLeft, area.Left, Math.Max(area.Left, area.Right - Width));
        _walkTargetTop = Top;
        _walkArrivalAction = arrivalAction;
        _settings.Paused = false;
        _typingMode = false;
        WakeFromPersistentSleep();

        if (Math.Abs(_walkTargetX - Left) < 8 && Math.Abs(_walkTargetTop - Top) < 8)
        {
            Left = _walkTargetX;
            Top = _walkTargetTop;
            _walkArrivalAction = null;
            arrivalAction();
            return;
        }

        _walkDirection = _walkTargetX > Left ? 1 : -1;
        _walking = true;
        SetAnimation(_walkDirection > 0 ? "running-right" : "running-left");
    }

    private void StopWalking()
    {
        _walking = false;
        _walkDirection = 0;
        _walkArrivalAction = null;
    }

    private void UpdateInactivity(long milliseconds)
    {
        if (milliseconds >= InactivityTimeoutMilliseconds && !_dragging &&
            !_settings.Paused && !IsManualActionActive(_clock.ElapsedMilliseconds))
        {
            _idleSleeping = true;
        }
    }

    private static long GetInactivityMilliseconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info)
            ? unchecked((uint)Environment.TickCount - info.Time)
            : 0;
    }

    private void WakeFromPersistentSleep()
    {
        _idleSleeping = false;
    }

    private async void WorkTimer_Tick(object? sender, EventArgs e)
    {
        UpdateInactivity(GetInactivityMilliseconds());
        await RefreshWorkEnvironmentAsync();
    }

    private async Task RefreshWorkEnvironmentAsync()
    {
        if (_workCheckRunning)
        {
            return;
        }

        _workCheckRunning = true;
        try
        {
            var wasQuiet = IsQuietModeActive();
            var processId = Environment.ProcessId;
            var state = await Task.Run(() => WorkEnvironmentDetector.Detect(processId));
            if (_closing)
            {
                return;
            }
            _teamsMeetingActive = state.TeamsMeetingActive;
            _teamsSharingActive = state.TeamsSharingActive;
            _fullscreenAppActive = state.FullscreenAppActive;
            UpdateSharingVisibility();
            var isQuiet = IsQuietModeActive();

            if (!wasQuiet && isQuiet && !IsManualActionActive(_clock.ElapsedMilliseconds))
            {
                StopWalking();
                ShowQuietPose();
            }
            else if (wasQuiet && !isQuiet)
            {
                LeaveQuietPose();
            }
        }
        catch
        {
            // Work-companion detection is best effort and must never interrupt the pet.
        }
        finally
        {
            _workCheckRunning = false;
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        Point cursor;
        if (GetCursorPos(out var cursorPixels))
        {
            cursor = ScreenPixelsToDips(cursorPixels.X, cursorPixels.Y);
        }
        else
        {
            var local = e.GetPosition(this);
            cursor = new Point(Left + local.X, Top + local.Y);
        }
        BeginPetDrag(cursor);
        CaptureMouse();
        e.Handled = true;
    }

    private void BeginPetDrag(Point cursor)
    {
        _dragSleeping = _currentAnimation == "sleeping" ||
            (IsQuietModeActive() && !IsManualActionActive(_clock.ElapsedMilliseconds));
        CancelTransientPlay();
        _typingMode = false;
        _dragging = true;
        _dragMoved = false;
        _dragStartCursor = cursor;
        _dragLastCursorX = cursor.X;
        _dragStartLeft = Left;
        _dragStartTop = Top;
        if (_dragSleeping)
        {
            ShowSleepingPose(_clock.ElapsedMilliseconds, false);
        }
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed || !GetCursorPos(out var cursorPixels))
        {
            return;
        }

        UpdatePetDrag(ScreenPixelsToDips(cursorPixels.X, cursorPixels.Y));
        e.Handled = true;
    }

    private void UpdatePetDrag(Point cursor)
    {
        var dx = cursor.X - _dragStartCursor.X;
        var dy = cursor.Y - _dragStartCursor.Y;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 4)
        {
            return;
        }

        _dragMoved = true;
        Left = _dragStartLeft + dx;
        Top = _dragStartTop + dy;

        var horizontalDelta = cursor.X - _dragLastCursorX;
        if (_dragSleeping)
        {
            ShowSleepingPose(_clock.ElapsedMilliseconds, false);
        }
        else if (Math.Abs(horizontalDelta) >= 1.5)
        {
            var desired = horizontalDelta > 0 ? "running-right" : "running-left";
            if (_currentAnimation != desired)
            {
                SetAnimation(desired);
            }
        }
        else if (_currentAnimation is not ("running-right" or "running-left"))
        {
            SetAnimation(dx >= 0 ? "running-right" : "running-left");
        }
        _dragLastCursorX = cursor.X;
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            FinishDrag();
            e.Handled = true;
        }
    }

    private void Window_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            FinishDrag();
        }
    }

    private void FinishDrag()
    {
        var moved = _dragMoved;
        var sleeping = _dragSleeping;
        _dragging = false;
        _dragMoved = false;
        _dragSleeping = false;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        ClampToWorkArea();
        SaveSettings();
        if (moved)
        {
            if (sleeping || IsQuietModeActive())
            {
                ShowQuietPose();
            }
            else
            {
                SetAnimation("idle");
            }
        }
        else
        {
            PlayUserAction("waving");
        }
    }

    private void PetMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (PresentationSource.FromVisual(PetMenu) is HwndSource menuSource)
        {
            WindowPrivacy.Apply(menuSource.Handle, _settings.HideFromScreenCapture);
        }
        PauseMenuItem.IsChecked = _settings.Paused;
        TopmostMenuItem.IsChecked = Topmost;
        StartupMenuItem.IsChecked = IsStartupEnabled();
        CapturePrivacyMenuItem.IsChecked = _settings.HideFromScreenCapture;
        MeetingNowMenuItem.IsChecked = _settings.ManualMeetingMode;
        TeamsQuietMenuItem.IsChecked = _settings.QuietDuringTeamsMeetings;
        FullscreenQuietMenuItem.IsChecked = _settings.QuietDuringFullscreenApps;
        TypingMenuItem.IsChecked = _settings.ReactToTyping;
        foreach (var item in AnimationSpeedMenuItem.Items.OfType<MenuItem>())
        {
            item.IsChecked = double.TryParse(item.Tag as string, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var speed) &&
                Math.Abs(speed - _settings.AnimationSpeed) < 0.001;
        }
        WorkStatusMenuItem.Header = _teamsSharingActive
            ? _hiddenForSharing ? "Status: Screen sharing - hidden" : "Status: Screen sharing - privacy off"
            : _settings.HideFromScreenCapture && !_capturePrivacyApplied
                ? "Status: Capture exclusion unavailable"
            : _settings.ManualMeetingMode
                ? "Status: Meeting mode - sleeping"
                : _settings.QuietDuringTeamsMeetings && _teamsMeetingActive
                ? "Status: Teams meeting - sleeping"
                : _settings.QuietDuringFullscreenApps && _fullscreenAppActive
                    ? "Status: Fullscreen - sleeping"
                    : _idleSleeping
                        ? "Status: Sleeping after inactivity"
                        : _typingMode
                            ? "Status: Typing companion"
                            : "Status: Available";
    }

    private void Wave_Click(object sender, RoutedEventArgs e) => PlayUserAction("waving");
    private void Jump_Click(object sender, RoutedEventArgs e) => PlayUserAction("jumping");
    private void Wait_Click(object sender, RoutedEventArgs e) => PlayUserAction("waiting");
    private void Review_Click(object sender, RoutedEventArgs e) => PlayUserAction("review");

    private void PlayUserAction(string animation)
    {
        CancelTransientPlay();
        Play(animation);
    }

    private async void TossBall_Click(object sender, RoutedEventArgs e)
    {
        CancelTransientPlay();
        StopWalking();
        WakeFromPersistentSleep();
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _propSequence = cancellation;

        var area = GetCurrentWorkArea();
        var direction = Left + Width / 2 < area.Left + area.Width / 2 ? 1 : -1;
        if (_random.Next(2) == 0)
        {
            direction *= -1;
        }

        var startLeft = Left + Width / 2 - 21;
        var startTop = Top + Height * 0.35;
        var distance = _random.Next(190, 341) * _settings.Scale;
        var targetLeft = Math.Clamp(startLeft + direction * distance, area.Left + 10, area.Right - 52);
        var targetTop = Math.Clamp(Top + Height - 42, area.Top + 10, area.Bottom - 48);

        var ball = new CompanionPropWindow(CompanionPropKind.Ball)
        {
            Left = startLeft,
            Top = startTop,
            Topmost = Topmost,
            ExcludeFromCapture = _settings.HideFromScreenCapture
        };
        _ballWindow = ball;
        ball.Show();
        RaisePetAboveProps();
        Play("waving");

        try
        {
            const int steps = 38;
            for (var step = 0; step <= steps; step++)
            {
                token.ThrowIfCancellationRequested();
                var progress = step / (double)steps;
                var inverse = 1 - progress;
                var arcTop = Math.Min(startTop, targetTop) - 115 * _settings.Scale;
                ball.Left = startLeft + (targetLeft - startLeft) * progress;
                ball.Top = inverse * inverse * startTop + 2 * inverse * progress * arcTop + progress * progress * targetTop;
                await Task.Delay(16, token);
            }

            token.ThrowIfCancellationRequested();
            BeginWalkTo(targetLeft - Width * 0.42, () =>
            {
                Play("jumping");
                _ = FinishBallPlayAsync(ball, cancellation);
            });
        }
        catch (OperationCanceledException)
        {
            CompletePlaySequence(cancellation);
        }
    }

    private async Task FinishBallPlayAsync(CompanionPropWindow ball, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await Task.Delay(950, token);
            if (ball.IsVisible)
            {
                ball.Close();
            }
            if (ReferenceEquals(_ballWindow, ball))
            {
                _ballWindow = null;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            CompletePlaySequence(cancellation);
        }
    }

    private void GiveSnack_Click(object sender, RoutedEventArgs e)
    {
        CancelTransientPlay();
        StopWalking();
        WakeFromPersistentSleep();
        var cancellation = new CancellationTokenSource();
        _propSequence = cancellation;

        var area = GetCurrentWorkArea();
        var placeRight = Left + Width + 80 < area.Right;
        var snackLeft = placeRight ? Left + Width - 24 : Left - 42;
        snackLeft = Math.Clamp(snackLeft, area.Left + 5, area.Right - 69);
        var snackTop = Math.Clamp(Top + Height - 34, area.Top, area.Bottom - 40);

        var snack = new CompanionPropWindow(CompanionPropKind.Snack)
        {
            Left = snackLeft,
            Top = snackTop,
            Topmost = Topmost,
            ExcludeFromCapture = _settings.HideFromScreenCapture
        };
        _snackWindow = snack;
        snack.Show();
        RaisePetAboveProps();

        BeginWalkTo(snackLeft + 32 - Width / 2, () =>
        {
            Play("waiting");
            _ = FinishSnackPlayAsync(snack, cancellation);
        });
    }

    private async Task FinishSnackPlayAsync(CompanionPropWindow snack, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await Task.Delay(1050, token);
            if (snack.IsVisible)
            {
                snack.Close();
            }
            if (ReferenceEquals(_snackWindow, snack))
            {
                _snackWindow = null;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            CompletePlaySequence(cancellation);
        }
    }

    private void CancelTransientPlay()
    {
        StopWalking();
        _manualActionUntil = 0;
        _propSequence?.Cancel();
        _propSequence?.Dispose();
        _propSequence = null;

        if (_ballWindow is not null)
        {
            if (_ballWindow.IsVisible)
            {
                _ballWindow.Close();
            }
            _ballWindow = null;
        }
        if (_snackWindow is not null)
        {
            if (_snackWindow.IsVisible)
            {
                _snackWindow.Close();
            }
            _snackWindow = null;
        }
    }

    private void CompletePlaySequence(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_propSequence, cancellation))
        {
            _propSequence = null;
            cancellation.Dispose();
        }
    }

    private void RaisePetAboveProps()
    {
        if (!Topmost)
        {
            return;
        }

        Topmost = false;
        Topmost = true;
    }

    private void Play(string animation)
    {
        StopWalking();
        WakeFromPersistentSleep();
        _typingMode = false;
        _settings.Paused = false;
        SetAnimation(animation);
        _manualActionUntil = _clock.ElapsedMilliseconds + AnimationDuration(animation) + 120;
    }

    private int AnimationDuration(string animation) =>
        _animations.TryGetValue(animation, out var definition)
            ? (int)Math.Round(definition.Durations.Sum() / _settings.AnimationSpeed)
            : 1000;

    private void SetAnimationSpeed(double speed)
    {
        var normalizedSpeed = NormalizeAnimationSpeed(speed);
        if (Math.Abs(normalizedSpeed - _settings.AnimationSpeed) < 0.001)
        {
            return;
        }

        var now = _clock.ElapsedMilliseconds;
        if (_nextFrameAt > now)
        {
            var remaining = _nextFrameAt - now;
            _nextFrameAt = now + Math.Max(1, (long)Math.Round(remaining * _settings.AnimationSpeed / normalizedSpeed));
        }
        if (_manualActionUntil > now)
        {
            var remaining = _manualActionUntil - now;
            _manualActionUntil = now + Math.Max(1, (long)Math.Round(remaining * _settings.AnimationSpeed / normalizedSpeed));
        }
        _settings.AnimationSpeed = normalizedSpeed;
        foreach (var item in AnimationSpeedMenuItem.Items.OfType<MenuItem>())
        {
            item.IsChecked = double.TryParse(item.Tag as string, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var itemSpeed) &&
                Math.Abs(itemSpeed - normalizedSpeed) < 0.001;
        }
        SaveSettings();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _settings.Paused = PauseMenuItem.IsChecked;
        if (_settings.Paused)
        {
            CancelTransientPlay();
            _typingMode = false;
        }
        else
        {
            SetAnimation("idle");
        }
        SaveSettings();
    }

    private void CapturePrivacy_Click(object sender, RoutedEventArgs e)
    {
        _settings.HideFromScreenCapture = CapturePrivacyMenuItem.IsChecked;
        ApplyCapturePrivacyToAllWindows();
        UpdateSharingVisibility();
        SaveSettings();
    }

    private void TeamsQuiet_Click(object sender, RoutedEventArgs e)
    {
        _settings.QuietDuringTeamsMeetings = TeamsQuietMenuItem.IsChecked;
        if (!_settings.QuietDuringTeamsMeetings && !_settings.QuietDuringFullscreenApps)
        {
            LeaveQuietPose();
        }
        SaveSettings();
    }

    private void MeetingNow_Click(object sender, RoutedEventArgs e)
    {
        _settings.ManualMeetingMode = MeetingNowMenuItem.IsChecked;
        if (_settings.ManualMeetingMode)
        {
            CancelTransientPlay();
            ShowQuietPose();
        }
        else if (!IsQuietModeActive())
        {
            LeaveQuietPose();
        }
        SaveSettings();
    }

    private void FullscreenQuiet_Click(object sender, RoutedEventArgs e)
    {
        _settings.QuietDuringFullscreenApps = FullscreenQuietMenuItem.IsChecked;
        if (!_settings.QuietDuringTeamsMeetings && !_settings.QuietDuringFullscreenApps)
        {
            LeaveQuietPose();
        }
        SaveSettings();
    }

    private void Typing_Click(object sender, RoutedEventArgs e)
    {
        _settings.ReactToTyping = TypingMenuItem.IsChecked;
        if (!_settings.ReactToTyping && _typingMode)
        {
            _typingMode = false;
            SetAnimation("idle");
        }
        SaveSettings();
    }

    private void AnimationSpeed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item &&
            double.TryParse(Convert.ToString(item.Tag, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var speed))
        {
            SetAnimationSpeed(speed);
        }
    }

    private void ApplyCapturePrivacyToAllWindows()
    {
        _capturePrivacyApplied = WindowPrivacy.Apply(this, _settings.HideFromScreenCapture);
        foreach (var prop in new[] { _ballWindow, _snackWindow })
        {
            if (prop is not null && prop.IsVisible)
            {
                prop.ExcludeFromCapture = _settings.HideFromScreenCapture;
                _capturePrivacyApplied &= WindowPrivacy.Apply(prop, _settings.HideFromScreenCapture);
            }
        }
        if (PresentationSource.FromVisual(PetMenu) is HwndSource menuSource)
        {
            WindowPrivacy.Apply(menuSource.Handle, _settings.HideFromScreenCapture);
        }
    }

    private void UpdateSharingVisibility()
    {
        var hide = _settings.HideFromScreenCapture && _teamsSharingActive;
        if (hide && !_hiddenForSharing)
        {
            PetMenu.IsOpen = false;
            CancelTransientPlay();
            _typingMode = false;
        }
        _hiddenForSharing = hide;
        Opacity = hide ? 0 : 1;
        IsHitTestVisible = !hide;
        foreach (var prop in new[] { _ballWindow, _snackWindow })
        {
            if (prop is not null)
            {
                prop.Opacity = hide ? 0 : 1;
            }
        }
    }

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostMenuItem.IsChecked;
        _settings.AlwaysOnTop = Topmost;
        foreach (var prop in new[] { _ballWindow, _snackWindow })
        {
            if (prop is not null)
            {
                prop.Topmost = Topmost;
            }
        }
        SaveSettings();
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true);
            if (key is null)
            {
                throw new InvalidOperationException("The Windows startup settings are unavailable.");
            }

            if (StartupMenuItem.IsChecked)
            {
                var launcher = Path.Combine(AppContext.BaseDirectory, "OtterDesktop.exe");
                var executable = File.Exists(launcher)
                    ? launcher
                    : Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Start with Windows is available in the published OtterDesktop.exe.");
                }
                key.SetValue(StartupValueName, $"\"{executable}\"");
            }
            else
            {
                key.DeleteValue(StartupValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            StartupMenuItem.IsChecked = IsStartupEnabled();
            MessageBox.Show(ex.Message, "Otter Desktop", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: false);
            return key?.GetValue(StartupValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    private void Scale_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var scale))
        {
            return;
        }

        var centerX = Left + Width / 2;
        var bottom = Top + Height;
        _settings.Scale = scale;
        Width = CellWidth * scale;
        Height = CellHeight * scale;
        Left = centerX - Width / 2;
        Top = bottom - Height;
        ClampToWorkArea();
        SaveSettings();
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        MoveToDefaultPosition();
        SaveSettings();
        PlayUserAction("jumping");
    }

    private void MoveToDefaultPosition()
    {
        var area = GetWorkAreaNearCursor();
        Left = area.Right - Width - 24;
        Top = area.Bottom - Height - 12;
    }

    private void ClampToWorkArea()
    {
        var area = GetCurrentWorkArea();
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Otter Desktop 1.14\n\nAn offline work companion that stays put until you interact. Control animation playback from 50% to 200% in the right-click menu. Typing holds a looking-down pose. Meetings, presentations, and five minutes of inactivity trigger breathing sleep. Click to wake and wave; dragging preserves sleep or shows running while awake. Manual meeting mode resets on restart. Screen-capture protection keeps Otter and its accessories out of supported screen shares.",
            "About Otter",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _closing = true;
        Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_closing)
        {
            _closing = true;
        }
        _renderTimer.Stop();
        _workTimer.Stop();
        _keyboardActivity?.Dispose();
        _keyboardActivity = null;
        CancelTransientPlay();
        SaveSettings();
    }

    private void SaveSettings()
    {
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.AlwaysOnTop = Topmost;
        _settings.Save();
    }

    private Rect GetCurrentWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            if (TryGetMonitorWorkArea(monitor, out var workArea))
            {
                return workArea;
            }
        }
        return SystemParameters.WorkArea;
    }

    private Rect GetWorkAreaNearCursor()
    {
        if (GetCursorPos(out var cursor))
        {
            var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
            if (TryGetMonitorWorkArea(monitor, out var workArea))
            {
                return workArea;
            }
        }
        return SystemParameters.WorkArea;
    }

    private bool TryGetMonitorWorkArea(IntPtr monitor, out Rect workArea)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            var topLeft = ScreenPixelsToDips(info.Work.Left, info.Work.Top);
            var bottomRight = ScreenPixelsToDips(info.Work.Right, info.Work.Bottom);
            workArea = new Rect(topLeft, bottomRight);
            return true;
        }

        workArea = Rect.Empty;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);
}
