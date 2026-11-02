param(
    [string]$AppPath = (Join-Path $PSScriptRoot '../bin/Release/net9.0-windows/OtterDesktop.App.dll')
)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run with pwsh -NoProfile -STA -File Tools/Test-Companion.ps1'
}
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CompanionCaptureCheck
{
    [DllImport("user32.dll")]
    public static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
}
'@

$settingsDirectory = Join-Path ([IO.Path]::GetTempPath()) ('OtterDesktop.Smoke.' + [Guid]::NewGuid().ToString('N'))
[AppContext]::SetData('OtterDesktop.SettingsDirectory', $settingsDirectory)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$window = $null
$script:checkCount = 0

function Get-Field([object]$Target, [string]$Name) {
    $Target.GetType().GetField($Name, $flags).GetValue($Target)
}

function Set-Field([object]$Target, [string]$Name, [object]$Value) {
    $Target.GetType().GetField($Name, $flags).SetValue($Target, $Value)
}

function Invoke-Companion([string]$Name, [object[]]$Arguments = @()) {
    $window.GetType().GetMethod($Name, $flags).Invoke($window, $Arguments)
}

function Invoke-Click([string]$Name) {
    $null = Invoke-Companion $Name @($window, [Windows.RoutedEventArgs]::new())
}

function Assert-Companion([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checkCount++
    Write-Output "PASS: $Message"
}

function Invoke-DispatcherFrame([int]$Milliseconds) {
    $frame = [Windows.Threading.DispatcherFrame]::new()
    $timer = [Windows.Threading.DispatcherTimer]::new()
    $timer.Interval = [TimeSpan]::FromMilliseconds($Milliseconds)
    $timer.Add_Tick({
        param($timerSender)
        $timerSender.Stop()
        $frame.Continue = $false
    }.GetNewClosure())
    $timer.Start()
    [Windows.Threading.Dispatcher]::PushFrame($frame)
}

function Complete-Walk {
    for ($step = 0; $step -lt 1000 -and (Get-Field $window '_walking'); $step++) {
        $null = Invoke-Companion 'UpdateWalk' @([double]0.1)
    }
    if (Get-Field $window '_walking') { throw 'Walk did not reach its target' }
}

function Wait-CompanionCondition([scriptblock]$Condition, [int]$TimeoutMilliseconds = 10000) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (-not (& $Condition)) {
        if ($deadline.ElapsedMilliseconds -ge $TimeoutMilliseconds) { throw 'Timed out waiting for companion action' }
        Invoke-DispatcherFrame 50
    }
}

function Assert-CaptureExcluded([Windows.Window]$Target, [string]$Name) {
    $handle = [Windows.Interop.WindowInteropHelper]::new($Target).Handle
    $affinity = [uint32]0
    $success = [CompanionCaptureCheck]::GetWindowDisplayAffinity($handle, [ref]$affinity)
    Assert-Companion ($success -and $affinity -eq 0x11) "$Name has Windows capture exclusion"
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path $AppPath).Path)
    [Windows.Application].GetField('_resourceAssembly', [Reflection.BindingFlags]'Static,NonPublic').SetValue($null, $assembly)
    $app = [Windows.Application]::new()
    $app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
    [Threading.SynchronizationContext]::SetSynchronizationContext(
        [Windows.Threading.DispatcherSynchronizationContext]::new([Windows.Threading.Dispatcher]::CurrentDispatcher))
    $null = [IO.Directory]::CreateDirectory($settingsDirectory)
    [IO.File]::WriteAllText((Join-Path $settingsDirectory 'settings.json'), (@{ ManualMeetingMode = $true } | ConvertTo-Json))
    $window = [Activator]::CreateInstance($assembly.GetType('OtterDesktop.MainWindow'))
    $detector = $assembly.GetType('OtterDesktop.WorkEnvironmentDetector')
    $presentation = $detector.GetMethod('IsPresentationModeActive', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, @())
    Assert-Companion ($presentation -is [bool]) 'Windows presentation detection API is callable'
    $settings = Get-Field $window '_settings'
    Assert-Companion (-not $settings.ManualMeetingMode) 'Manual meeting mode resets to off even when saved as enabled'
    $settings.Paused = $false
    $settings.ManualMeetingMode = $false
    $settings.QuietDuringTeamsMeetings = $true
    $settings.QuietDuringFullscreenApps = $true
    $settings.ReactToTyping = $true
    $settings.HideFromScreenCapture = $true
    $settings.Scale = 1.0
    Set-Field $window '_workCheckRunning' $true
    $window.Show()
    Invoke-DispatcherFrame 100
    (Get-Field $window '_renderTimer').Stop()
    (Get-Field $window '_workTimer').Stop()
    Assert-CaptureExcluded $window 'Otter'

    Assert-Companion ($settings.AnimationSpeed -eq 1.0) 'Animation speed defaults to normal'
    $normalDuration = Invoke-Companion 'AnimationDuration' @('waving')
    Assert-Companion ($normalDuration -eq 700) 'Normal speed preserves the base animation duration'
    $null = Invoke-Companion 'SetAnimation' @('idle')
    $clock = Get-Field $window '_clock'
    $oldRemaining = (Get-Field $window '_nextFrameAt') - $clock.ElapsedMilliseconds
    Set-Field $window '_manualActionUntil' ($clock.ElapsedMilliseconds + 1000)
    $speedMenu = Get-Field $window 'AnimationSpeedMenuItem'
    $slowOption = $speedMenu.Items[0]
    $window.GetType().GetMethod('AnimationSpeed_Click', $flags).Invoke(
        $window, @($slowOption, [Windows.RoutedEventArgs]::new()))
    Assert-Companion ($settings.AnimationSpeed -eq 0.5) 'Slow preset updates animation speed'
    Assert-Companion ((Invoke-Companion 'AnimationDuration' @('waving')) -eq 1400) 'Slow speed doubles animation duration'
    $newRemaining = (Get-Field $window '_nextFrameAt') - $clock.ElapsedMilliseconds
    Assert-Companion ([Math]::Abs($newRemaining - $oldRemaining * 2) -lt 80) "Changing speed adjusts the current frame immediately (before=$oldRemaining ms, after=$newRemaining ms)"
    $actionRemaining = (Get-Field $window '_manualActionUntil') - $clock.ElapsedMilliseconds
    Assert-Companion ([Math]::Abs($actionRemaining - 2000) -lt 80) 'Active interactions retain their quiet-mode override at slower speeds'
    Assert-Companion ($slowOption.IsChecked -and -not (($speedMenu.Items | Where-Object Tag -eq '1.0' | Select-Object -First 1).IsChecked)) 'Only the selected speed preset is checked'
    $null = Invoke-Companion 'SetAnimationSpeed' @([double]2.0)
    Assert-Companion ((Invoke-Companion 'FrameDuration' @('idle', [int]0)) -eq 140) 'Fast preset halves idle frame duration'
    Assert-Companion ((Invoke-Companion 'AnimationDuration' @('waving')) -eq 350) 'Fast preset halves interaction duration'
    $null = Invoke-Companion 'SetAnimationSpeed' @([double]1.25)
    $loadedSettings = $assembly.GetType('OtterDesktop.PetSettings').GetMethod(
        'Load', [Reflection.BindingFlags]'Static,Public,NonPublic').Invoke($null, @())
    Assert-Companion ($loadedSettings.AnimationSpeed -eq 1.25) 'Animation speed persists across launches'
    $null = Invoke-Companion 'SetAnimationSpeed' @([double]1.0)
    Set-Field $window '_manualActionUntil' ([long]0)

    $keyboard = Get-Field $window '_keyboardActivity'
    Set-Field $keyboard '_lastKeyPressAt' ([long]0)
    Assert-Companion (-not $keyboard.IsTyping) 'No typing before the first key press'
    Set-Field $keyboard '_lastKeyPressAt' ([Environment]::TickCount64)
    Assert-Companion $keyboard.IsTyping 'Recent typing is detected'
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion ((Get-Field $window '_currentAnimation') -eq 'typing') 'Typing uses the look-down animation'
    $typingFrame = (Get-Field $window 'PetImage').Source
    Set-Field $window '_nextFrameAt' ([long]0)
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion ((Get-Field $window '_frameIndex') -eq 4 -and [object]::ReferenceEquals($typingFrame, (Get-Field $window 'PetImage').Source)) 'Typing holds the looking-down frame instead of looping'
    Set-Field $keyboard '_lastKeyPressAt' ([Environment]::TickCount64 - 2000)
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion (-not (Get-Field $window '_typingMode')) 'Typing reaction expires'

    Set-Field $window '_teamsMeetingActive' $true
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion (Get-Field $window '_quietPoseVisible') 'Meetings use the sleeping state'
    Assert-Companion ((Get-Field $window '_currentAnimation') -eq 'sleeping') 'Automatic meeting mode uses the curled sleeping pose'
    $meetingFrame = (Get-Field $window 'PetImage').Source
    Set-Field $window '_nextFrameAt' ([long]0)
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion (-not [object]::ReferenceEquals($meetingFrame, (Get-Field $window 'PetImage').Source) -and (Get-Field $window '_frameIndex') -eq 1) 'Meeting sleep animates gentle breathing'
    Set-Field $keyboard '_lastKeyPressAt' ([Environment]::TickCount64)
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion ((Get-Field $window '_currentAnimation') -eq 'sleeping' -and -not (Get-Field $window '_typingMode')) 'Meeting sleep takes priority over typing'
    Set-Field $keyboard '_lastKeyPressAt' ([Environment]::TickCount64 - 2000)
    Invoke-Click 'Wave_Click'
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion ((Get-Field $window '_currentAnimation') -eq 'waving' -and -not (Get-Field $window '_quietPoseVisible')) 'Explicit interaction works during meetings'
    Set-Field $window '_manualActionUntil' ([long]0)
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion (Get-Field $window '_quietPoseVisible') 'Quiet mode resumes after interaction'
    Assert-Companion ((Get-Field $window '_currentAnimation') -eq 'sleeping') 'The otter returns to sleep after meeting interaction'
    Set-Field $window '_teamsMeetingActive' $false
    Set-Field $window '_fullscreenAppActive' $true
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion (Get-Field $window '_quietPoseVisible') 'Fullscreen apps keep the otter quiet'
    Assert-Companion ((Get-Field $window '_currentAnimation') -eq 'sleeping') 'Fullscreen and presentations use the sleeping pose'
    Set-Field $window '_nextFrameAt' ([long]0)
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion ((Get-Field $window '_frameIndex') -eq 1) 'Fullscreen sleep animates gentle breathing'
    Set-Field $window '_fullscreenAppActive' $false
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion ((Get-Field $window '_currentAnimation') -eq 'idle') 'The otter wakes when work quiet mode ends'

    $area = [Windows.SystemParameters]::WorkArea
    $window.Left = $area.Left + 50
    $window.Top = $area.Top + 30
    Assert-Companion ((Get-Field $window '_atlas').PixelHeight -eq 2496) 'Atlas includes the appended sleeping row'
    $sleep = (Get-Field $window '_animations')['sleeping']
    Assert-Companion ($sleep.Row -eq 11 -and $sleep.Frames -eq 8 -and $sleep.Loop) 'Eight sleeping frames form a looping animation'
    $null = Invoke-Companion 'UpdateInactivity' @([long]300000)
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion ((Get-Field $window '_currentAnimation') -eq 'sleeping' -and -not (Get-Field $window '_walking')) 'Inactivity uses the curled sleeping animation without movement'
    $sleepLeft = $window.Left
    $sleepTop = $window.Top
    Set-Field $window '_nextFrameAt' ([long]0)
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion ((Get-Field $window '_frameIndex') -eq 1 -and $window.Left -eq $sleepLeft -and $window.Top -eq $sleepTop) 'Sleeping breathes without moving the window'
    Set-Field $window '_teamsMeetingActive' $true
    Set-Field $window '_nextFrameAt' ([long]0)
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion ((Get-Field $window '_currentAnimation') -eq 'sleeping' -and (Get-Field $window '_frameIndex') -eq 2) 'Inactivity sleep keeps breathing when a meeting begins'
    Set-Field $window '_teamsMeetingActive' $false
    Set-Field $window '_nextFrameAt' ([long]0)
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion ((Get-Field $window '_frameIndex') -eq 3) 'Inactivity sleep keeps breathing after the meeting'
    for ($frame = 0; $frame -lt 5; $frame++) {
        Set-Field $window '_nextFrameAt' ([long]0)
        $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    }
    Assert-Companion ((Get-Field $window '_frameIndex') -eq 0) 'Sleeping wraps to the first atlas frame'
    Invoke-Click 'Wave_Click'
    Assert-Companion (-not (Get-Field $window '_idleSleeping') -and (Get-Field $window '_currentAnimation') -eq 'waving') 'Explicit interaction wakes the sleeping otter'
    Set-Field $window '_manualActionUntil' ([long]0)
    Set-Field $keyboard '_lastKeyPressAt' ([Environment]::TickCount64)
    $null = Invoke-Companion 'UpdateInactivity' @([long]300000)
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion ((Get-Field $window '_idleSleeping') -and (Get-Field $window '_currentAnimation') -eq 'sleeping') 'Keyboard activity does not wake sleep'

    $dragStart = [Windows.Point]::new($window.Left + 50, $window.Top + 50)
    $null = Invoke-Companion 'BeginPetDrag' @($dragStart)
    $null = Invoke-Companion 'UpdatePetDrag' @([Windows.Point]::new($dragStart.X + 20, $dragStart.Y + 10))
    Assert-Companion ((Get-Field $window '_currentAnimation') -eq 'sleeping' -and (Get-Field $window '_dragSleeping') -and (Get-Field $window '_dragMoved')) 'Dragging a sleeping otter moves it without waking it'
    $null = Invoke-Companion 'FinishDrag'
    Assert-Companion ((Get-Field $window '_idleSleeping') -and (Get-Field $window '_currentAnimation') -eq 'sleeping') 'Sleeping continues after releasing a drag'
    $null = Invoke-Companion 'BeginPetDrag' @($dragStart)
    $null = Invoke-Companion 'FinishDrag'
    Assert-Companion (-not (Get-Field $window '_idleSleeping') -and (Get-Field $window '_currentAnimation') -eq 'waving') 'Clicking a sleeping otter wakes it with a small wave'

    $dragStart = [Windows.Point]::new($window.Left + 50, $window.Top + 50)
    $null = Invoke-Companion 'BeginPetDrag' @($dragStart)
    $null = Invoke-Companion 'UpdatePetDrag' @([Windows.Point]::new($dragStart.X + 20, $dragStart.Y))
    Assert-Companion ((Get-Field $window '_currentAnimation') -eq 'running-right') 'Dragging an awake otter right shows running right'
    $null = Invoke-Companion 'UpdatePetDrag' @([Windows.Point]::new($dragStart.X - 20, $dragStart.Y))
    Assert-Companion ((Get-Field $window '_currentAnimation') -eq 'running-left') 'Dragging an awake otter left shows running left'
    $null = Invoke-Companion 'FinishDrag'
    Assert-Companion ((Get-Field $window '_currentAnimation') -eq 'idle') 'An awake otter returns to idle after dragging'

    Set-Field $keyboard '_lastKeyPressAt' ([Environment]::TickCount64 - 2000)
    $inactivity = $window.GetType().GetMethod('GetInactivityMilliseconds', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, @())
    Assert-Companion ($inactivity -ge 0) 'Windows keyboard and mouse inactivity duration is available'
    $null = Invoke-Companion 'UpdateInactivity' @([long]299999)
    Assert-Companion (-not (Get-Field $window '_idleSleeping')) 'Inactivity sleep does not start before five minutes'
    $null = Invoke-Companion 'UpdateInactivity' @([long]300000)
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion ((Get-Field $window '_idleSleeping') -and (Get-Field $window '_currentAnimation') -eq 'sleeping') 'Inactivity sleep starts at five minutes'
    Set-Field $keyboard '_lastKeyPressAt' ([Environment]::TickCount64)
    $null = Invoke-Companion 'UpdateInactivity' @([long]0)
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion ((Get-Field $window '_idleSleeping') -and (Get-Field $window '_currentAnimation') -eq 'sleeping') 'Keyboard and mouse activity do not wake inactivity sleep'
    $dragStart = [Windows.Point]::new($window.Left + 50, $window.Top + 50)
    $null = Invoke-Companion 'BeginPetDrag' @($dragStart)
    $null = Invoke-Companion 'UpdatePetDrag' @([Windows.Point]::new($dragStart.X + 12, $dragStart.Y))
    $null = Invoke-Companion 'FinishDrag'
    Assert-Companion ((Get-Field $window '_idleSleeping') -and (Get-Field $window '_currentAnimation') -eq 'sleeping') 'Dragging does not clear inactivity sleep'
    $null = Invoke-Companion 'BeginPetDrag' @($dragStart)
    $null = Invoke-Companion 'FinishDrag'
    Assert-Companion (-not (Get-Field $window '_idleSleeping') -and (Get-Field $window '_currentAnimation') -eq 'waving') 'A click clears inactivity sleep and waves'

    $null = Invoke-Companion 'CancelTransientPlay'
    $null = Invoke-Companion 'SetAnimation' @('idle')
    Set-Field $keyboard '_lastKeyPressAt' ([Environment]::TickCount64 - 2000)
    $idleLeft = $window.Left
    $idleTop = $window.Top
    for ($tick = 0; $tick -lt 100; $tick++) {
        Set-Field $window '_nextFrameAt' ([long]0)
        $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    }
    Assert-Companion ((Get-Field $window '_currentAnimation') -eq 'idle' -and -not (Get-Field $window '_walking') -and $window.Left -eq $idleLeft -and $window.Top -eq $idleTop) 'Normal activity stays idle without automatic actions or wandering'

    Invoke-Click 'GiveSnack_Click'
    Assert-CaptureExcluded (Get-Field $window '_snackWindow') 'Snack'
    Complete-Walk
    Wait-CompanionCondition { $null -eq (Get-Field $window '_propSequence') }
    Assert-Companion ($null -eq (Get-Field $window '_snackWindow') -and $null -eq (Get-Field $window '_propSequence')) 'Snack finishes and releases its sequence'

    Invoke-Click 'TossBall_Click'
    Assert-CaptureExcluded (Get-Field $window '_ballWindow') 'Ball'
    Wait-CompanionCondition { Get-Field $window '_walking' }
    Complete-Walk
    Wait-CompanionCondition { $null -eq (Get-Field $window '_propSequence') }
    Assert-Companion ($null -eq (Get-Field $window '_ballWindow') -and $null -eq (Get-Field $window '_propSequence')) 'Ball chase finishes and releases its sequence'

    Invoke-Click 'TossBall_Click'
    (Get-Field $window 'PauseMenuItem').IsChecked = $true
    Invoke-Click 'Pause_Click'
    Invoke-DispatcherFrame 100
    Assert-Companion ($settings.Paused -and $null -eq (Get-Field $window '_ballWindow') -and -not (Get-Field $window '_walking')) 'Pause safely cancels an in-flight toss'
    Invoke-Click 'GiveSnack_Click'
    Assert-Companion (-not $settings.Paused) 'Explicit snack interaction resumes a paused pet'

    Set-Field $window '_teamsSharingActive' $true
    $null = Invoke-Companion 'UpdateSharingVisibility'
    Assert-Companion ($window.Opacity -eq 0 -and -not $window.IsHitTestVisible) 'Screen sharing hides the pet completely'
    Assert-Companion ($null -eq (Get-Field $window '_snackWindow')) 'Screen sharing removes toys'
    $position = $window.Left
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    Assert-Companion ($window.Left -eq $position) 'Hidden pet does not wander'
    (Get-Field $window 'CapturePrivacyMenuItem').IsChecked = $false
    Invoke-Click 'CapturePrivacy_Click'
    Assert-Companion ($window.Opacity -eq 1) 'Privacy opt-out restores visibility during sharing'
    $affinity = [uint32]0
    $handle = [Windows.Interop.WindowInteropHelper]::new($window).Handle
    $null = [CompanionCaptureCheck]::GetWindowDisplayAffinity($handle, [ref]$affinity)
    Assert-Companion ($affinity -eq 0) 'Privacy opt-out removes capture exclusion'
    (Get-Field $window 'CapturePrivacyMenuItem').IsChecked = $true
    Invoke-Click 'CapturePrivacy_Click'
    Assert-Companion ($window.Opacity -eq 0) 'Re-enabling privacy hides an active share'
    Set-Field $window '_teamsSharingActive' $false
    $null = Invoke-Companion 'UpdateSharingVisibility'
    Assert-Companion ($window.Opacity -eq 1 -and $window.IsHitTestVisible) 'Pet returns after sharing'

    $menu = Get-Field $window 'PetMenu'
    $menu.IsOpen = $true
    Invoke-DispatcherFrame 100
    $menuSource = [Windows.PresentationSource]::FromVisual($menu)
    $affinity = [uint32]0
    $success = [CompanionCaptureCheck]::GetWindowDisplayAffinity($menuSource.Handle, [ref]$affinity)
    Assert-Companion ($success -and $affinity -eq 0x11) 'Pet context menu has Windows capture exclusion'
    $menu.IsOpen = $false

    Invoke-Click 'GiveSnack_Click'
    (Get-Field $window 'MeetingNowMenuItem').IsChecked = $true
    Invoke-Click 'MeetingNow_Click'
    Assert-Companion ($null -eq (Get-Field $window '_propSequence') -and -not (Get-Field $window '_walking')) 'Manual meeting mode cancels existing play'
    Assert-Companion ((Get-Field $window '_currentAnimation') -eq 'sleeping') 'Manual meeting mode uses the curled sleeping pose'
    (Get-Field $window 'MeetingNowMenuItem').IsChecked = $false
    Invoke-Click 'MeetingNow_Click'

    Set-Field $window '_manualActionUntil' ([long]0)
    $null = Invoke-Companion 'UpdateInactivity' @([long]300000)
    $null = Invoke-Companion 'RenderTimer_Tick' @($null, [EventArgs]::Empty)
    $window.UpdateLayout()
    $image = Get-Field $window 'PetImage'
    Assert-Companion ($image.Source.PixelWidth -eq 192 -and $image.Source.PixelHeight -eq 208) 'Sprite frames load with the expected dimensions'
    $render = [Windows.Media.Imaging.RenderTargetBitmap]::new(192, 208, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $render.Render($window.Content)
    $pixels = [byte[]]::new(192 * 208 * 4)
    $render.CopyPixels($pixels, 192 * 4, 0)
    $visiblePixels = 0
    for ($pixel = 3; $pixel -lt $pixels.Length; $pixel += 4) {
        if ($pixels[$pixel] -gt 0) { $visiblePixels++ }
    }
    Assert-Companion ($visiblePixels -gt 1000) 'Rendered otter contains visible sprite pixels'
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($render))
    $output = [IO.File]::Create((Join-Path $PSScriptRoot '../obj/companion-smoke.png'))
    try { $encoder.Save($output) } finally { $output.Dispose() }
    Write-Output "All $script:checkCount companion smoke checks passed."
}
catch {
    Write-Host $_.Exception.ToString()
    throw
}
finally {
    if ($null -ne $window) { $window.Close() }
    if (Test-Path $settingsDirectory) {
        Remove-Item $settingsDirectory -Recurse
    }
}