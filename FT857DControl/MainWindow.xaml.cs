using System.IO;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Shapes;
using NAudio.Wave;
using FT857DControl.Models;
using FT857DControl.Services;

namespace FT857DControl;

public partial class MainWindow : Window
{
    private bool _fullScreen;
    private WindowStyle _savedWindowStyle;
    private ResizeMode _savedResizeMode;
    private WindowState _savedWindowState;

    private readonly RadioConnection _radio = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _lampTimer;
    private WaveInEvent? _audioInput;
    private readonly object _scopeLock = new();
    private float[] _scopeSamples = new float[256];
    private bool _lampPhase;
    private bool _rxActive;
    private bool _pcSquelchEnabled;
    private int _pcSquelchLevel = 3;
    private bool _pcSquelchMuted;
    private long _frequencyHz = 145_500_000;
    private RadioMode _mode = RadioMode.FM;
    private bool _split;
    private bool _vfoB;
    private bool _transmitting;
    private bool _polling;
    private int _pollFailures;
    private DateTime _suppressPollUntil = DateTime.MinValue;
    private bool _sessionConnected;
    private bool _clarifier;
    private int _ritHz;
    private bool _scanning;
    private bool _memoryMode;
    private long _memoryHz = 145_500_000;
    private RadioMode _memoryModeValue = RadioMode.FM;
    private long _homeHz = 145_500_000;
    private RadioMode _homeMode = RadioMode.FM;
    private int _functionStepIndex;
    private int _softPageIndex;
    private readonly long[] _steps = [10, 100, 1000, 5000, 10000, 100000];
    private bool _dialDragging;
    private double _lastDialAngle;
    private double _dialVisualAngle;
    private bool _dialBusy;
    private bool _dialLocked;
    private double _afVisualAngle;
    private double _rfSqlVisualAngle;
    private int _lcdColourIndex;
    private int _meterModeIndex; // 0=S, 1=Po, 2=ALC, 3=SWR
    private byte _lastRawSMeter;
    private int _radioMemoryChannel = 1;
    private int _filterIndex = 1;
    private readonly decimal[] _ctcssTones = [67.0m,69.3m,71.9m,74.4m,77.0m,79.7m,82.5m,85.4m,88.5m,91.5m,94.8m,97.4m,100.0m,103.5m,107.2m,110.9m,114.8m,118.8m,123.0m,127.3m,131.8m,136.5m,141.3m,146.2m,151.4m,156.7m,159.8m,162.2m,165.5m,167.9m,171.3m,173.8m,177.3m,179.9m,183.5m,186.2m,189.9m,192.8m,196.6m,199.5m,203.5m,206.5m,210.7m,218.1m,225.7m,229.1m,233.6m,241.8m,250.3m,254.1m];
    private decimal _ctcssTone = 88.5m;
    private bool _ctcssEnabled;
    private bool _preamp, _attenuator, _noiseBlanker, _agcFast, _vox, _compressor, _toneSql, _breakIn;
    private int _repeaterShift;
    private long _repeaterOffsetHz = 600_000;
    private readonly MemoryChannel?[] _memories = new MemoryChannel?[200];
    private static readonly string MemoryFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HamTech M0FXB FT857D Controller", "memories.json");
    private sealed record MemoryChannel(string Name, long FrequencyHz, RadioMode Mode, bool CtcssEnabled, decimal CtcssTone, int Shift, long OffsetHz);

    private readonly long[] _bandHomes = [1_900_000, 3_650_000, 7_100_000, 10_125_000, 14_200_000, 18_100_000, 21_200_000, 24_940_000, 28_500_000, 50_150_000, 145_000_000, 433_000_000];
    private readonly RadioMode[] _modeCycle = [RadioMode.LSB, RadioMode.USB, RadioMode.CW, RadioMode.CWR, RadioMode.DIG, RadioMode.AM, RadioMode.FM, RadioMode.WFM];

    public MainWindow()
    {
        InitializeComponent();
        // Baud choices are declared in XAML so they are always visible in the drop-down.
        BaudBox.Text = "9600";
        CtcssBox.ItemsSource = _ctcssTones.Select(t => t.ToString("0.0", CultureInfo.InvariantCulture));
        CtcssBox.SelectedItem = "88.5";
        MemoryChannelBox.ItemsSource = Enumerable.Range(1, 200).Select(i => $"M-{i:000}").ToArray();
        MemoryChannelBox.SelectedIndex = 0;
        RadioMemoryBox.ItemsSource = Enumerable.Range(1, 200).Select(i => $"M-{i:000}").ToArray();
        RadioMemoryBox.SelectedIndex = 0;
        LoadMemories();
        RefreshPorts();
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _pollTimer.Tick += PollTimer_Tick;
        _lampTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _lampTimer.Tick += (_, _) => UpdateActivityLamps();
        _lampTimer.Start();
        StartAudioScope();
        Closed += (_, _) => { StopAudioScope(); _radio.Dispose(); };
        UpdateDisplay();
        UpdateSoftKeys();
    }


    private void StartAudioScope()
    {
        StopAudioScope();
        try
        {
            _audioInput = new WaveInEvent
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 45,
                NumberOfBuffers = 3
            };
            _audioInput.DataAvailable += AudioInput_DataAvailable;
            _audioInput.RecordingStopped += (_, e) => Dispatcher.Invoke(() =>
            {
                if (e.Exception != null) AudioScopeStatus.Text = "AUDIO INPUT ERROR";
            });
            _audioInput.StartRecording();
            AudioScopeStatus.Text = "DEFAULT AUDIO INPUT • LIVE";
        }
        catch
        {
            AudioScopeStatus.Text = "NO AUDIO INPUT — CLICK RESTART";
        }
    }

    private void StopAudioScope()
    {
        try { _audioInput?.StopRecording(); } catch { }
        _audioInput?.Dispose();
        _audioInput = null;
    }

    private void RestartAudioScope_Click(object sender, RoutedEventArgs e) => StartAudioScope();

    private void AudioInput_DataAvailable(object? sender, WaveInEventArgs e)
    {
        int count = e.BytesRecorded / 2;
        if (count <= 0) return;
        var next = new float[256];
        int stride = Math.Max(1, count / next.Length);
        for (int i = 0; i < next.Length; i++)
        {
            int sampleIndex = Math.Min(count - 1, i * stride);
            short sample = BitConverter.ToInt16(e.Buffer, sampleIndex * 2);
            next[i] = sample / 32768f;
        }
        lock (_scopeLock) _scopeSamples = next;
        Dispatcher.BeginInvoke(DrawAudioScope);
    }

    private void AudioScopeCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawAudioScope();

    private void DrawAudioScope()
    {
        if (AudioScopeCanvas == null || AudioScopeTrace == null) return;
        double w = AudioScopeCanvas.ActualWidth, h = AudioScopeCanvas.ActualHeight;
        if (w < 2 || h < 2) return;
        ScopeMidLine.X1 = 0; ScopeMidLine.X2 = w; ScopeMidLine.Y1 = ScopeMidLine.Y2 = h / 2;
        float[] samples;
        lock (_scopeLock) samples = (float[])_scopeSamples.Clone();
        var points = new PointCollection(samples.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            double x = i * (w / (samples.Length - 1));
            double y = h / 2 - samples[i] * (h * 0.44);
            points.Add(new Point(x, y));
        }
        AudioScopeTrace.Points = points;
    }

    private bool Connected => _sessionConnected;

    // Windows multimedia keys control the current default playback endpoint.
    private const byte VkVolumeMute = 0xAD;
    private const byte VkVolumeDown = 0xAE;
    private const byte VkVolumeUp = 0xAF;
    private const uint KeyeventfKeyup = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private static void SendMediaKey(byte key)
    {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyeventfKeyup, UIntPtr.Zero);
    }

    private void VolumeDown_Click(object sender, RoutedEventArgs e)
    {
        SendMediaKey(VkVolumeDown);
        StatusText.Text = "Windows volume down";
    }

    private void VolumeUp_Click(object sender, RoutedEventArgs e)
    {
        SendMediaKey(VkVolumeUp);
        StatusText.Text = "Windows volume up";
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        SendMediaKey(VkVolumeMute);
        StatusText.Text = "Windows mute toggled";
    }


    // The IC-706MKIIG standard CI-V set does not provide AF squelch level control.
    // This PC-side squelch uses the radio's real CI-V S-meter reading and mutes/unmutes
    // Windows audio when the received level crosses the selected threshold.
    private void SqlToggle_Click(object sender, RoutedEventArgs e)
    {
        _pcSquelchEnabled = !_pcSquelchEnabled;
        SqlButton.Content = _pcSquelchEnabled ? "PC SQL ON" : "PC SQL OFF";
        SqlButton.Foreground = new SolidColorBrush(_pcSquelchEnabled ? Color.FromRgb(130, 230, 90) : Colors.White);
        if (!_pcSquelchEnabled && _pcSquelchMuted) SetPcSquelchMute(false);
        StatusText.Text = _pcSquelchEnabled ? $"PC squelch enabled at S{_pcSquelchLevel}" : "PC squelch disabled";
    }

    private void SqlDown_Click(object sender, RoutedEventArgs e)
    {
        _pcSquelchLevel = Math.Max(0, _pcSquelchLevel - 1);
        SqlLevelText.Text = $"S{_pcSquelchLevel}";
    }

    private void SqlUp_Click(object sender, RoutedEventArgs e)
    {
        _pcSquelchLevel = Math.Min(15, _pcSquelchLevel + 1);
        SqlLevelText.Text = _pcSquelchLevel <= 9 ? $"S{_pcSquelchLevel}" : $"+{(_pcSquelchLevel - 9) * 10}";
    }

    private void ApplyPcSquelch(int level)
    {
        if (!_pcSquelchEnabled || _transmitting) return;
        SetPcSquelchMute(level < _pcSquelchLevel);
    }

    private void SetPcSquelchMute(bool shouldMute)
    {
        if (_pcSquelchMuted == shouldMute) return;
        SendMediaKey(VkVolumeMute);
        _pcSquelchMuted = shouldMute;
        MuteButton.Content = shouldMute ? "SQL MUTED" : "MUTE";
    }

    private void RfGainInfo_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "RF Gain: use the radio SQL/RF knob (standard IC-706MKIIG CI-V has no RF Gain level command)";
        MessageBox.Show("The IC-706MKIIG standard CI-V command set does not provide a command to change the physical RF Gain level. Use the radio's SQL/RF knob for true RF Gain. I have deliberately not used undocumented EEPROM writes, because those can alter persistent radio data.", "RF Gain", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    private void FillScreen_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Maximized;

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; }
        else if (e.Key == Key.Escape && _fullScreen) { ToggleFullScreen(); e.Handled = true; }
    }

    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _savedWindowStyle = WindowStyle;
            _savedResizeMode = ResizeMode;
            _savedWindowState = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _fullScreen = true;
            StatusText.Text = "Full screen — F11 or Esc to restore";
        }
        else
        {
            WindowStyle = _savedWindowStyle;
            ResizeMode = _savedResizeMode;
            WindowState = _savedWindowState == WindowState.Minimized ? WindowState.Normal : _savedWindowState;
            _fullScreen = false;
            StatusText.Text = "Windowed mode";
        }
    }

    private void RestoreWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_fullScreen) ToggleFullScreen();
        WindowState = WindowState.Normal;
    }

    private void Tuner_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("IC-706MKIIG ANTENNA TUNER\n\nUse the radio front-panel TUNER/CALL control and the tuner settings described in the IC-706MKIIG manual. This controller does not send an undocumented tuner-start CI-V command.", "IC-706MKIIG Tuner", MessageBoxButton.OK, MessageBoxImage.Information);
        StatusText.Text = "Tuner control remains on the radio front panel";
    }

    private void AlwaysOnTop_Click(object sender, RoutedEventArgs e)
    {
        Topmost = AlwaysOnTopMenu.IsChecked;
        StatusText.Text = Topmost ? "Always on top enabled" : "Always on top disabled";
    }

    private void TuningStepMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string tag || !long.TryParse(tag, out var step)) return;
        var index = Array.IndexOf(_steps, step);
        if (index >= 0) _functionStepIndex = index;
        StatusText.Text = $"VFO tuning step: {step:N0} Hz";
    }

    private async void MenuPttOn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_transmitting) return;
            throw new NotSupportedException("This CI-V-only cable has no documented IC-706MKIIG PTT command. Use microphone/radio PTT or a separate hardware PTT interface.");
        }
        catch (Exception ex) { ShowError("PTT failed", ex); }
    }

    private async void MenuPttOff_Click(object sender, RoutedEventArgs e) => await ReleasePttAsync();

    private void Instructions_Click(object sender, RoutedEventArgs e)
    {
        var help = new Window
        {
            Title = "Instructions — HamTech M0FXB ICOM IC-706MKIIG Controller",
            Width = 820,
            Height = 720,
            MinWidth = 520,
            MinHeight = 420,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            Background = new SolidColorBrush(Color.FromRgb(20, 22, 23)),
            Foreground = Brushes.White
        };

        var panel = new StackPanel { Margin = new Thickness(18) };
        void Heading(string text)
        {
            panel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 211, 90)),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 14, 0, 5)
            });
        }
        void Body(string text)
        {
            panel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21,
                Margin = new Thickness(0, 0, 0, 5)
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = "HamTech M0FXB ICOM IC-706MKIIG Controller",
            Foreground = Brushes.White,
            FontSize = 24,
            FontWeight = FontWeights.Bold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "by M0FXB Andreas — Operating Instructions",
            Foreground = new SolidColorBrush(Color.FromRgb(255, 211, 90)),
            FontSize = 16,
            Margin = new Thickness(0, 2, 0, 10)
        });

        Heading("1. Connect to the radio");
        Body("Connect the IC-706MKIIG CI-V cable. Choose the COM port and the same baud rate configured in the radio (4800, 9600 or 38400), then press Connect. Use Refresh if a newly attached COM port is not shown. Demo mode lets you operate the interface without a radio.");

        Heading("2. Frequency and VFO knob");
        Body("Turn the large VFO dial with the mouse wheel, or hold the left mouse button and drag around the dial. The frequency buttons provide fixed +/- steps. FUNC cycles the tuning step; Settings > Tuning step selects 10 Hz, 100 Hz, 1 kHz, 5 kHz, 10 kHz or 100 kHz. UP/DWN tune by 1 kHz. BAND cycles amateur-band starting frequencies.");

        Heading("3. Modes, VFO, split and RIT");
        Body("LSB, USB, CW, CWR, AM, FM, DIG and PKT select the operating mode. A/B switches VFO. SPL toggles split operation. CLAR enables/disables clarifier/RIT; use the RIT -, CLR and + buttons to change or clear the offset.");

        Heading("4. PTT, TX/RX lamps and signal meter");
        Body("The desktop PTT control is intentionally disabled because the IC-706MKIIG manual does not document a CI-V PTT command. Use the microphone/radio PTT. The bar meter and analog S-meter use the documented CI-V S-meter command.");

        Heading("5. Repeater shift and CTCSS");
        Body("Select a CTCSS tone, then use Tone ON or Tone OFF. For a repeater choose SIMPLEX, - or +, enter the shift amount in MHz (for example 0.600), and press APPLY. The controller sends the repeater offset, shift direction and tone settings to the radio.");

        Heading("6. PC memory channels — SAVE");
        Body("There are 50 application memory channels, CH 01 to CH 50. First tune the radio and select the mode, CTCSS tone/state, repeater direction and shift amount you want to store. Select a red memory channel number, type an optional name such as GB3WR, then press SAVE. The memory stores name, receive frequency, mode, CTCSS state/tone, shift direction and offset. Memories are saved on the PC and survive program restarts.");

        Heading("7. PC memory channels — RECALL");
        Body("Select the required CH number and press RECALL. The saved frequency, mode, CTCSS tone/state and repeater shift are sent back to the connected IC-706MKIIG. These are controller/application memories; they do not overwrite the radio's internal EEPROM memory channels.");

        Heading("8. RADIO memories — READ RADIO");
        Body("The red M-001 to M-200 selector addresses the IC-706MKIIG's actual internal regular memory records. Connect the radio, select a channel, then press READ RADIO. The controller uses the undocumented EEPROM READ command only and displays the radio memory tag, frequency, mode, repeater direction and CTCSS tone where present. This feature is deliberately READ ONLY: the program does not issue the EEPROM write command, so READ RADIO cannot alter calibration data or radio memories.");

        Heading("8. HOME, V/M, MW and SCAN");
        Body("HOME recalls the controller home frequency/mode. V/M switches the controller's VFO/memory behavior. MW stores the current controller memory state used by that V/M function. SCAN starts/stops the controller scan function. The dedicated 50-channel SAVE/RECALL bank is the recommended way to keep named repeater and operating memories.");

        Heading("9. PC audio volume, mute and squelch");
        Body("VOL - and VOL + change the Windows master playback volume. MUTE toggles Windows playback mute. SQL ON/OFF enables the controller's PC-audio squelch; SQL - and SQL + change the S-meter threshold. When enabled, Windows audio is muted below the selected signal threshold and opened above it.");

        Heading("10. RF Gain");
        Body("The RF - / RF + controls display information only. Standard IC-706MKIIG CI-V does not provide a safe command for the physical RF Gain setting, so true RF Gain remains controlled by the radio's SQL/RF knob.");

        Heading("11. Audio scope");
        Body("The green audio scope uses the Windows default recording input. Feed your radio/interface receive audio into a Windows recording device to see the waveform. If you change audio devices, press RESTART on the scope.");

        Heading("12. Top menus");
        Body("File closes the program. View refreshes COM ports, reads the radio and controls Always on top. Radio provides connection, read, VFO, split, clarifier and PTT commands. Settings selects tuning step and refreshes COM ports. Instructions opens this guide. Help contains VFO knob help and About.");

        Heading("13. Resizing");
        Body("Drag any window edge or the lower-right resize grip. The radio panel scales to fit the available window while keeping the controls together.");

        Heading("Safety note");
        Body("Before transmitting, confirm the correct frequency, mode, repeater shift, CTCSS tone and antenna. PC volume/squelch controls affect Windows audio, not the radio's physical AF volume knob.");

        var close = new Button
        {
            Content = "CLOSE INSTRUCTIONS",
            Height = 38,
            Margin = new Thickness(0, 18, 0, 12),
            FontWeight = FontWeights.Bold,
            Background = new SolidColorBrush(Color.FromRgb(51, 56, 58)),
            Foreground = Brushes.White
        };
        close.Click += (_, _) => help.Close();
        panel.Children.Add(close);

        help.Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        help.ShowDialog();
    }

    private void KnobHelp_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show("VFO KNOB\n\n• Roll the mouse wheel over the large dial to tune.\n• Or hold the left mouse button and drag around the dial.\n• FUNC cycles the tuning step.\n• Settings → Tuning step selects an exact step.\n\nEach movement sends a real CI-V Set Frequency command to the IC-706MKIIG.", "VFO knob", MessageBoxButton.OK, MessageBoxImage.Information);

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show("IC-706MKIIG CI-V Control\nRadio-style desktop controller\n\nCI-V functions include frequency, mode, VFO A/B, split, repeater duplex and S-meter. Undocumented PTT/RIT commands are intentionally not sent.", "About IC-706MKIIG Control", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Dial_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!Connected) { ShowError("VFO dial", new InvalidOperationException("Connect to the radio or enable Demo mode first.")); return; }
        _dialDragging = true;
        _lastDialAngle = GetDialAngle(e.GetPosition(VfoDial));
        VfoDial.CaptureMouse();
        e.Handled = true;
    }

    private void Dial_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dialDragging = false;
        VfoDial.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Dial_LostMouseCapture(object sender, MouseEventArgs e) => _dialDragging = false;

    private async void Dial_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dialDragging || e.LeftButton != MouseButtonState.Pressed || _dialBusy) return;
        var angle = GetDialAngle(e.GetPosition(VfoDial));
        var delta = angle - _lastDialAngle;
        if (delta > 180) delta -= 360;
        if (delta < -180) delta += 360;
        if (Math.Abs(delta) < 4.0) return;

        var ticks = Math.Clamp((int)(delta / 4.0), -12, 12);
        if (ticks == 0) ticks = Math.Sign(delta);
        _lastDialAngle = angle;
        await TuneDialAsync(ticks);
    }

    private double GetDialAngle(Point point)
    {
        var cx = VfoDial.ActualWidth / 2.0;
        var cy = VfoDial.ActualHeight / 2.0;
        return Math.Atan2(point.Y - cy, point.X - cx) * 180.0 / Math.PI;
    }

    private async Task TuneDialAsync(int ticks)
    {
        if (_dialBusy) return;
        _dialBusy = true;
        try
        {
            var step = _steps[_functionStepIndex];
            var delta = ticks * step;
            var target = Math.Clamp(_frequencyHz + delta, 100_000, 470_000_000);
            await SendAsync(CatProtocol.SetFrequency(target), $"VFO dial · {delta:+#;-#} Hz · step {step:N0} Hz");
            _frequencyHz = target;
            _dialVisualAngle = (_dialVisualAngle + ticks * 7.5) % 360.0;
            DialIndicatorTransform.Angle = _dialVisualAngle;
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("Dial tune failed", ex); }
        finally { _dialBusy = false; }
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void PortBox_DropDownOpened(object sender, EventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        var selected = (PortBox.Text ?? "").Trim();
        var ports = RadioConnection.AvailablePorts();
        PortBox.ItemsSource = ports;
        if (!string.IsNullOrWhiteSpace(selected) && ports.Contains(selected, StringComparer.OrdinalIgnoreCase))
            PortBox.Text = ports.First(p => string.Equals(p, selected, StringComparison.OrdinalIgnoreCase));
        else if (ports.Length > 0)
            PortBox.Text = ports[0];
        StatusText.Text = ports.Length == 0 ? "No serial ports found — Demo mode is available" : $"Found {ports.Length} serial port(s)";
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (Connected)
        {
            if (_transmitting) await ReleasePttAsync();
            _pollTimer.Stop();
            _radio.Close();
            _sessionConnected = false;
            SetConnectedUi(false, "Disconnected");
            return;
        }

        try
        {
            if (DemoCheck.IsChecked == true)
            {
                SetConnectedUi(true, "Demo mode — no radio commands are being sent");
            }
            else
            {
                var port = (PortBox.Text ?? "").Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(port)) throw new InvalidOperationException("Select a COM port, or type one such as COM3.");

                var baudText = (BaudBox.Text ?? "").Trim();
                if (!int.TryParse(baudText, out var baud) || (baud != 4800 && baud != 9600 && baud != 19200 && baud != 38400))
                    throw new InvalidOperationException("Choose the CI-V baud rate configured in IC-706MKIIG Initial Set item 35. It must match the radio.");

                StatusText.Text = $"Opening {port} at {baud} baud…";
                _radio.Open(port, baud);

                // Do a real CI-V query before claiming the radio is connected.
                // Verify the connection with real IC-706MKIIG CI-V frequency/mode queries.
                RadioState? state = null;
                Exception? lastCatError = null;
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        state = await _radio.ReadStateAsync();
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastCatError = ex;
                        if (attempt < 3) await Task.Delay(250);
                    }
                }
                if (state is null)
                    throw new IOException("COM port opened, but no valid IC-706MKIIG CI-V reply was received after 3 attempts. " + _radio.LastTrace, lastCatError);

                _frequencyHz = state.Value.FrequencyHz;
                _mode = state.Value.Mode;
                UpdateDisplay();
                SetConnectedUi(true, $"CI-V OK · {port} · {baud} baud");
            }
            _sessionConnected = true;
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            _pollTimer.Stop();
            _radio.Close();
            _sessionConnected = false;
            SetConnectedUi(false, $"CI-V connection failed: {ex.Message}");
            ShowError("Could not connect to IC-706MKIIG CI-V", new Exception(
                ex.Message + "\n\nIC-706MKIIG check: Menu 34 CI-V ADDRES = 58H; Menu 35 CI-V BAUD matches the app; Menu 37 CI-V 731 = OFF (5-byte frequency). Connect the CI-V interface to the 3.5 mm REMOTE jack.\n\nDiagnostic: " + _radio.LastTrace));
        }
    }

    private void SetConnectedUi(bool connected, string message)
    {
        ConnectButton.Content = connected ? "DISCONNECT" : "CONNECT";
        ConnectButton.Background = new SolidColorBrush(connected ? Color.FromRgb(110, 57, 42) : Color.FromRgb(39, 100, 73));
        LinkLamp.Fill = new SolidColorBrush(connected ? Color.FromRgb(71, 220, 112) : Color.FromRgb(102, 102, 102));
        PortBox.IsEnabled = !connected;
        BaudBox.IsEnabled = !connected;
        DemoCheck.IsEnabled = !connected;
        RefreshButton.IsEnabled = !connected;
        StatusText.Text = message;
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_polling || _radio.IsBusy || !Connected || DemoCheck.IsChecked == true || DateTime.UtcNow < _suppressPollUntil) return;
        _polling = true;
        try
        {
            var state = await _radio.ReadStateAsync();
            _frequencyHz = state.FrequencyHz;
            _mode = state.Mode;
            UpdateDisplay();

            // Native IC-706MKIIG metering: 15 02 returns 0000-0255 BCD.
            var rawMeter = await _radio.ReadRxStatusAsync();
            _lastRawSMeter = rawMeter;
            var meter16 = Math.Clamp((int)Math.Round(rawMeter / 255.0 * 15.0), 0, 15);
            if (_meterModeIndex == 0) UpdateSignalMeter(meter16);

            // 15 01 is the documented squelch-condition query.  It is the best
            // documented RX activity indication available over IC-706MKIIG CI-V.
            _rxActive = await _radio.ReadSquelchOpenAsync();
            _transmitting = false; // no documented CI-V TX/PTT-status query on this model
            UpdateActivityLamps();
            _pollFailures = 0;
            StatusText.Text = $"CI-V LIVE · S-meter {rawMeter:000}/255 · {(_rxActive ? "RX / SQL OPEN" : "SQL CLOSED")}";
        }
        catch (Exception ex)
        {
            // A single missed frame on a shared/echoing CI-V bus should not kill live control.
            // Keep polling and only report a compact warning; successful traffic clears it.
            _pollFailures++;
            StatusText.Text = $"CI-V retry {_pollFailures} · {ex.Message}";
        }
        finally { _polling = false; }
    }

    private void UpdateActivityLamps()
    {
        _lampPhase = !_lampPhase;
        TxLamp.Fill = new SolidColorBrush(_transmitting ? Color.FromRgb(255, 55, 45) : Color.FromRgb(72, 18, 15));
        RxLamp.Fill = new SolidColorBrush(!_transmitting && _rxActive ? Color.FromRgb(174, 232, 35) : Color.FromRgb(38, 50, 13));
    }

    private void UpdateSignalMeter(int level)
    {
        level = Math.Clamp(level, 0, 15);
        SignalText.Text = level <= 9 ? $"S{level}" : $"+{(level - 9) * 10}";

        var segments = new Border[] { SignalSeg0, SignalSeg1, SignalSeg2, SignalSeg3, SignalSeg4, SignalSeg5, SignalSeg6, SignalSeg7, SignalSeg8, SignalSeg9, SignalSeg10, SignalSeg11, SignalSeg12, SignalSeg13, SignalSeg14, SignalSeg15 };
        for (int i = 0; i < segments.Length; i++)
        {
            bool lit = i <= level;
            // S0-S9 are green. S9+10 through +60 are red for an immediate strong-signal warning.
            Color on = i <= 9 ? Color.FromRgb(42, 225, 74) : Color.FromRgb(245, 48, 42);
            Color off = i <= 9 ? Color.FromRgb(28, 62, 34) : Color.FromRgb(70, 28, 26);
            segments[i].Background = new SolidColorBrush(lit ? on : off);
            segments[i].BorderBrush = new SolidColorBrush(lit ? on : Color.FromRgb(55, 70, 55));
        }

        var angle = -62.0 + (level / 15.0) * 124.0;
        AnalogNeedleTransform.Angle = angle;
        AnalogMeterText.Text = SignalText.Text;
        ApplyPcSquelch(level);
    }

    private void UpdateTxMeter(int level, bool highSwr)
    {
        level = Math.Clamp(level, 0, 15);
        SignalText.Text = highSwr ? "SWR!" : $"PO {level:00}";
        var segments = new Border[] { SignalSeg0, SignalSeg1, SignalSeg2, SignalSeg3, SignalSeg4, SignalSeg5, SignalSeg6, SignalSeg7, SignalSeg8, SignalSeg9, SignalSeg10, SignalSeg11, SignalSeg12, SignalSeg13, SignalSeg14, SignalSeg15 };
        for (int i = 0; i < segments.Length; i++)
        {
            bool lit = i <= level;
            Color on = highSwr ? Color.FromRgb(255, 45, 35) : (i < 11 ? Color.FromRgb(42, 225, 74) : Color.FromRgb(245, 48, 42));
            Color off = highSwr ? Color.FromRgb(75, 22, 20) : (i < 11 ? Color.FromRgb(28, 62, 34) : Color.FromRgb(70, 28, 26));
            segments[i].Background = new SolidColorBrush(lit ? on : off);
            segments[i].BorderBrush = new SolidColorBrush(lit ? on : Color.FromRgb(55, 70, 55));
        }
        AnalogNeedleTransform.Angle = -62.0 + (level / 15.0) * 124.0;
        AnalogMeterText.Text = highSwr ? "HI SWR" : "TX PO";
    }

    private async Task SendAsync(byte[] command, string status)
    {
        if (!Connected) throw new InvalidOperationException("Connect to the radio or enable Demo mode first.");
        if (DemoCheck.IsChecked != true) await _radio.SendAsync(command);
        StatusText.Text = DemoCheck.IsChecked == true ? $"Demo · {status}" : status;
    }

    private void MeterCycle_Click(object sender, RoutedEventArgs e)
    {
        _meterModeIndex = (_meterModeIndex + 1) % 4;
        string name = _meterModeIndex switch { 0 => "S", 1 => "Po", 2 => "ALC", _ => "SWR" };
        MeterButton.Content = $"METER {name}";
        if (_meterModeIndex == 0)
        {
            UpdateSignalMeter(Math.Clamp((int)Math.Round(_lastRawSMeter / 255.0 * 15.0), 0, 15));
            StatusText.Text = "METER · live CI-V S-meter (15 02)";
        }
        else
        {
            // The IC-706MKIIG front panel can display Po/ALC/SWR on transmit, but
            // its published CI-V table exposes only S-meter level (15 02).
            ClearMeterForUnsupportedTx(name);
            StatusText.Text = $"METER {name} · radio front-panel measurement; no documented CI-V read command";
        }
    }

    private void ClearMeterForUnsupportedTx(string name)
    {
        SignalText.Text = name;
        var segments = new Border[] { SignalSeg0, SignalSeg1, SignalSeg2, SignalSeg3, SignalSeg4, SignalSeg5, SignalSeg6, SignalSeg7, SignalSeg8, SignalSeg9, SignalSeg10, SignalSeg11, SignalSeg12, SignalSeg13, SignalSeg14, SignalSeg15 };
        foreach (var segment in segments)
        {
            segment.Background = new SolidColorBrush(Color.FromRgb(28, 45, 30));
            segment.BorderBrush = new SolidColorBrush(Color.FromRgb(55, 70, 55));
        }
        AnalogNeedleTransform.Angle = -62;
        AnalogMeterText.Text = $"{name} · RADIO";
    }

    private async void SetFrequency_Click(object sender, RoutedEventArgs e) => await SetFrequencyFromBoxAsync();

    private void LcdPanel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Capture the far-left M-page cell BEFORE the LCD frequency-entry handler can see it.
        // Coordinates are relative to the complete LCD panel.  This intentionally gives the
        // M1/M2/M3/M4 selector its own generous hit area, including the surrounding padding.
        var p = e.GetPosition(LcdPanel);
        if (p.X >= 12 && p.X <= 175 && p.Y >= 225 && p.Y <= 288)
        {
            e.Handled = true;
            CycleSoftPage();
        }
    }

    private void LcdPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FrequencyBox.Visibility == Visibility.Visible) return;
        BeginFrequencyEntry();
        e.Handled = true;
    }

    private void FrequencyReadout_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        BeginFrequencyEntry();
        e.Handled = true;
    }

    private void BeginFrequencyEntry()
    {
        FrequencyBox.Text = (_frequencyHz / 1_000_000m).ToString("0.#####", CultureInfo.InvariantCulture);
        FrequencyReadout.Visibility = Visibility.Collapsed;
        FrequencyBox.Visibility = Visibility.Visible;
        FrequencyBox.Focus();
        FrequencyBox.SelectAll();
    }

    private async void FrequencyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SetFrequencyFromBoxAsync(); EndFrequencyEntry(); }
        else if (e.Key == Key.Escape) { e.Handled = true; EndFrequencyEntry(); UpdateDisplay(); }
    }

    private void FrequencyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (FrequencyBox.Visibility == Visibility.Visible) { EndFrequencyEntry(); UpdateDisplay(); }
    }

    private void EndFrequencyEntry()
    {
        FrequencyBox.Visibility = Visibility.Collapsed;
        FrequencyReadout.Visibility = Visibility.Visible;
    }

    private void AfKnob_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var steps = Math.Max(1, Math.Abs(e.Delta) / 120);
        for (var i = 0; i < steps; i++) SendMediaKey(e.Delta > 0 ? VkVolumeUp : VkVolumeDown);
        _afVisualAngle = Math.Clamp(_afVisualAngle + (e.Delta > 0 ? 12 : -12), -135, 135);
        AfIndicatorTransform.Angle = _afVisualAngle;
        AfLevelText.Text = e.Delta > 0 ? "PC VOL +" : "PC VOL −";
        StatusText.Text = e.Delta > 0 ? "Windows PC audio volume up" : "Windows PC audio volume down";
        e.Handled = true;
    }

    private void AfKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SendMediaKey(VkVolumeMute);
        AfLevelText.Text = "MUTE";
        StatusText.Text = "Windows PC audio mute toggled";
        e.Handled = true;
    }

    private void RfSqlKnob_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _pcSquelchLevel = Math.Clamp(_pcSquelchLevel + (e.Delta > 0 ? 1 : -1), 0, 15);
        _pcSquelchEnabled = true;
        _rfSqlVisualAngle = Math.Clamp(_rfSqlVisualAngle + (e.Delta > 0 ? 12 : -12), -135, 135);
        RfSqlIndicatorTransform.Angle = _rfSqlVisualAngle;
        SqlButton.Content = "PC SQL ON";
        SqlLevelText.Text = _pcSquelchLevel <= 9 ? $"S{_pcSquelchLevel}" : $"+{(_pcSquelchLevel - 9) * 10}";
        StatusText.Text = $"RF/SQL knob → PC squelch threshold {SqlLevelText.Text} (radio RF gain is not CI-V controllable)";
        e.Handled = true;
    }

    private async Task SetFrequencyFromBoxAsync()
    {
        try
        {
            var parsed = ParseFrequency(FrequencyBox.Text);
            await SendAsync(CatProtocol.SetFrequency(parsed), $"Frequency set to {FormatFrequency(parsed)} MHz");
            _frequencyHz = parsed;
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("Could not set frequency", ex); UpdateDisplay(); }
    }

    private async void Tune_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var delta = long.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
            var target = _frequencyHz + delta;
            await SendAsync(CatProtocol.SetFrequency(target), $"Tuned {delta / 1000:+0;-0} kHz");
            _frequencyHz = target;
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("Could not tune", ex); }
    }

    private async void Mode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var mode = Enum.Parse<RadioMode>((string)((Button)sender).Tag);
            await SendAsync(CatProtocol.SetMode(mode), $"Mode set to {mode}");
            _mode = mode;
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("Could not set mode", ex); }
    }

    private async void Vfo_Click(object sender, RoutedEventArgs e)
    {
        try { await SendAsync(CatProtocol.ToggleVfo(), "VFO changed"); _vfoB = !_vfoB; UpdateDisplay(); }
        catch (Exception ex) { ShowError("Could not change VFO", ex); }
    }

    private async void Split_Click(object sender, RoutedEventArgs e)
    {
        try { var next = !_split; await SendAsync(CatProtocol.SetSplit(next), $"Split {(next ? "on" : "off")}"); _split = next; UpdateDisplay(); }
        catch (Exception ex) { ShowError("Could not change split", ex); }
    }

    private async void Read_Click(object sender, RoutedEventArgs e)
    {
        if (DemoCheck.IsChecked == true) { StatusText.Text = "Demo · simulated radio state refreshed"; UpdateDisplay(); return; }
        try
        {
            if (!_radio.IsOpen) throw new InvalidOperationException("Connect to the radio first.");
            var state = await _radio.ReadStateAsync();
            _frequencyHz = state.FrequencyHz; _mode = state.Mode; UpdateDisplay();
            StatusText.Text = "Radio state read successfully";
        }
        catch (Exception ex) { ShowError("Could not read radio", ex); }
    }

    private async Task ReadRadioAsync()
    {
        if (DemoCheck.IsChecked == true)
        {
            UpdateDisplay();
            return;
        }

        if (!_radio.IsOpen)
            throw new InvalidOperationException("Connect to the radio first.");

        var state = await _radio.ReadStateAsync();
        _frequencyHz = state.FrequencyHz;
        _mode = state.Mode;
    }

    private async void Ptt_Down(object sender, MouseButtonEventArgs e)
    {
        try
        {
            PttButton.CaptureMouse();
            throw new NotSupportedException("This CI-V-only cable has no documented IC-706MKIIG PTT command. Use microphone/radio PTT or a separate hardware PTT interface.");
        }
        catch (Exception ex) { ShowError("PTT failed", ex); }
    }

    private async void Ptt_Up(object sender, MouseButtonEventArgs e) => await ReleasePttAsync();
    private async void Ptt_LostCapture(object sender, MouseEventArgs e) { if (_transmitting) await ReleasePttAsync(); }

    private async Task ReleasePttAsync()
    {
        try { await Task.CompletedTask; }
        catch (Exception ex) { ShowError("Could not release PTT", ex); }
        finally
        {
            _transmitting = false;
            PttButton.ReleaseMouseCapture();
            PttButton.Content = "PTT";
            PttButton.Background = new SolidColorBrush(Color.FromRgb(128, 46, 46));
        }
    }

    private async void Home_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SendAsync(CatProtocol.SetFrequency(_homeHz), $"HOME · {FormatFrequency(_homeHz)} MHz");
            await SendAsync(CatProtocol.SetMode(_homeMode), $"HOME · {_homeMode}");
            _frequencyHz = _homeHz; _mode = _homeMode; _memoryMode = false; UpdateDisplay();
        }
        catch (Exception ex) { ShowError("HOME failed", ex); }
    }

    private async void Clar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _clarifier = !_clarifier;
            await SendAsync(CatProtocol.SetClarifier(_clarifier), $"Clarifier {(_clarifier ? "ON" : "OFF")}");
            UpdateDisplay();
        }
        catch (Exception ex) { _clarifier = !_clarifier; ShowError("Clarifier failed", ex); }
    }


    // IC-706MKIIG-style M1-M4 function pages.  The fourth slot is intentionally
    // blank: the real M-pages use three F-key selections, not an EXIT soft key.
    private static readonly string[][] SoftPages =
    [
        ["SPL", "A/B", "A=B", ""],
        ["MW", "M→V", "V/M", ""],
        ["FIL", "NB", "MET", ""],
        ["VOX", "COMP", "AGC", ""]
    ];

    private void SoftMenu_Click(object sender, RoutedEventArgs e)
    {
        // The physical MENU button below the LCD is the page selector, just like the IC-706MKIIG.
        // It is deliberately UI-only: no CI-V command is sent and no radio polling is involved.
        e.Handled = true;
        CycleSoftPage();
    }

    private void SoftMenuLabel_Click(object sender, MouseButtonEventArgs e)
    {
        // PC-controller shortcut: clicking the far-left LCD M1/M2/M3/M4 area
        // cycles the same local soft-key page as the physical MENU button.
        // Mark handled so the parent LCD does not open frequency entry.
        e.Handled = true;
        CycleSoftPage();
    }

    private void CycleSoftPage()
    {
        _softPageIndex++;
        if (_softPageIndex >= SoftPages.Length) _softPageIndex = 0;
        UpdateSoftKeys();
        StatusText.Text = $"Controller function page M{_softPageIndex + 1} · IC-706MKIIG CI-V has no command to change the radio LCD M-page";
    }

    private void UpdateSoftKeys()
    {
        var p = SoftPages[_softPageIndex];
        SoftPageLabel.Text = $"M{_softPageIndex + 1}";
        Soft1Label.Text = p[0]; Soft2Label.Text = p[1]; Soft3Label.Text = p[2]; Soft4Label.Text = p[3];
        SoftF1Button.Content = p[0]; SoftF2Button.Content = p[1]; SoftF3Button.Content = p[2]; SoftF4Button.Content = p[3];
        SoftF1Button.IsEnabled = p[0].Length > 0; SoftF2Button.IsEnabled = p[1].Length > 0; SoftF3Button.IsEnabled = p[2].Length > 0; SoftF4Button.IsEnabled = p[3].Length > 0;
        SoftF1Button.ToolTip = $"M{_softPageIndex + 1} · {p[0]}"; SoftF2Button.ToolTip = $"M{_softPageIndex + 1} · {p[1]}"; SoftF3Button.ToolTip = $"M{_softPageIndex + 1} · {p[2]}"; SoftF4Button.ToolTip = p[3].Length > 0 ? $"M{_softPageIndex + 1} · {p[3]}" : null;
    }

    private void SoftLabel_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is TextBlock t && int.TryParse(t.Tag?.ToString(), out var key)) ExecuteSoftKey(key, sender, e);
    }

    private void SoftExitLabel_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Read_Click(sender, new RoutedEventArgs());
    }

    private void SoftKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && int.TryParse(b.Tag?.ToString(), out var key)) ExecuteSoftKey(key, sender, e);
    }

    private void ExecuteSoftKey(int key, object sender, RoutedEventArgs e)
    {
        var action = SoftPages[_softPageIndex][key];
        switch (action)
        {
            case "SPL": Split_Click(sender, e); break;
            case "A/B": Vfo_Click(sender, e); break;
            case "A=B": CivFeature_Click(new Button { Tag = "A=B" }, e); break;
            case "FIL": FilterCycle_Click(sender, e); break;
            case "V/M": Vm_Click(sender, e); break;
            case "MW": RadioMemoryWrite_Click(sender, e); break;
            case "M→V": MemoryToVfo_Click(sender, e); break;
            case "MET": MeterCycle_Click(sender, e); break;
            case "NB": CivFeature_Click(new Button { Tag = "NB" }, e); break;
            case "P.AMP": CivFeature_Click(new Button { Tag = "PREAMP" }, e); break;
            case "ATT": CivFeature_Click(new Button { Tag = "ATT" }, e); break;
            case "AGC": CivFeature_Click(new Button { Tag = "AGC" }, e); break;
            case "VOX": CivFeature_Click(new Button { Tag = "VOX" }, e); break;
            case "COMP": CivFeature_Click(new Button { Tag = "COMP" }, e); break;
            case "TONE": CivFeature_Click(new Button { Tag = "TONE" }, e); break;
            case "TSQL": CivFeature_Click(new Button { Tag = "TSQL" }, e); break;
        }
    }

    private void ExecuteSoftKey(int key, object sender, MouseButtonEventArgs e)
    {
        ExecuteSoftKey(key, sender, new RoutedEventArgs());
    }

    private async void Func_Click(object sender, RoutedEventArgs e)
    {
        _functionStepIndex = (_functionStepIndex + 1) % _steps.Length;
        var step = _steps[_functionStepIndex];
        StepButton.Content = $"STEP\n{FormatStep(step)}";
        try
        {
            await SendAsync(CatProtocol.SetTuningStep(step), $"Tuning step {step:N0} Hz");
            StatusText.Text = $"Radio tuning step: {step:N0} Hz · controller dial matched";
        }
        catch (Exception ex) { ShowError("Tuning-step change failed", ex); }
    }

    private static string FormatStep(long step) => step >= 1000 ? $"{step / 1000.0:0.#}k" : $"{step}";

    private void DisplayColour_Click(object sender, RoutedEventArgs e)
    {
        _lcdColourIndex = (_lcdColourIndex + 1) % 4;
        var colours = new[]
        {
            ("GREEN", Color.FromRgb(121, 214, 50)),
            ("ORANGE", Color.FromRgb(255, 174, 66)),
            ("BLUE", Color.FromRgb(116, 200, 255)),
            ("WHITE", Color.FromRgb(238, 242, 238))
        };
        var choice = colours[_lcdColourIndex];
        LcdPanel.Background = new SolidColorBrush(choice.Item2);
        FrequencyBox.Background = new SolidColorBrush(choice.Item2);
        StatusText.Text = $"LCD colour: {choice.Item1}";
    }

    private void TxPower_Click(object sender, RoutedEventArgs e)
    {
        var bandPower = _frequencyHz >= 400_000_000 ? "up to 20 W on 70 cm" : _frequencyHz >= 140_000_000 ? "up to 50 W on 2 m" : "up to 100 W on HF/6 m";
        StatusText.Text = "TX POWER · set Q1 RF POWER on the radio";
        MessageBox.Show($"TX POWER\n\nThe IC-706MKIIG has continuously adjustable RF output ({bandPower}), but its documented CI-V command table does not provide a safe RF-power setting command.\n\nUse DISPLAY (hold 2 seconds) → Q1 RF POWER on the radio and turn the main dial.\n\nThe controller will not use undocumented EEPROM writes.", "TX Power", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Vm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // V/M must switch the RADIO, not merely the controller display.
            // Use the documented bare 07 command when leaving memory and 08+channel
            // when entering memory.  Do not perform an immediate CI-V read here;
            // the normal poll refreshes frequency/mode after the rig has settled.
            _suppressPollUntil = DateTime.UtcNow.AddMilliseconds(1200);
            if (_memoryMode)
            {
                // IC-706MKIIG manual: 07 = VFO, 07 00 = VFO A.
                // Send both documented forms with a settling gap. This is deliberate:
                // the first exits MEMORY and the second explicitly selects VFO A.
                await SendAsync(CatProtocol.SetVfoMode(), "V/M · exit MEMORY");
                await Task.Delay(180);
                await SendAsync(CatProtocol.SetVfoA(), "V/M · VFO A");
                await Task.Delay(350);
                _memoryMode = false;
                _vfoB = false;
                MemoryDisplayText.Text = string.Empty;
                StatusText.Text = "V/M · VFO A selected (CI-V 07, then 07 00)";
            }
            else
            {
                // 08 with no subcommand enters memory mode; then 08 mc selects channel.
                await SendAsync(CatProtocol.SetMemoryMode(), "V/M · MEMORY mode");
                await Task.Delay(180);
                await SendAsync(CatProtocol.SelectMemory(_radioMemoryChannel), $"V/M · MEMORY M-{_radioMemoryChannel:00}");
                await Task.Delay(350);
                _memoryMode = true;
                MemoryDisplayText.Text = $"M-{_radioMemoryChannel:00}";
                StatusText.Text = $"V/M · MEMORY M-{_radioMemoryChannel:00} selected (CI-V 08)";
            }
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("V/M failed", ex); }
    }

    private async void MemoryToVfo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _suppressPollUntil = DateTime.UtcNow.AddMilliseconds(1200);
            await SendAsync(CatProtocol.MemoryToVfo(), "M→V · memory copied to VFO");
            await Task.Delay(250);
            await SendAsync(CatProtocol.SetVfoMode(), "M→V · VFO");
            _memoryMode = false;
            _vfoB = false;
            MemoryDisplayText.Text = string.Empty;
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("M→V failed", ex); }
    }

    private void MemoryWrite_Click(object sender, RoutedEventArgs e)
    {
        _memoryHz = _frequencyHz; _memoryModeValue = _mode;
        StatusText.Text = $"MW · saved app memory: {FormatFrequency(_memoryHz)} {_memoryModeValue}";
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _scanning = !_scanning;
            await SendAsync(CatProtocol.Scan(_scanning), _scanning ? "IC-706MKIIG scan started" : "IC-706MKIIG scan stopped");
            ScanButton.Content = _scanning ? "STOP" : "SCAN";
        }
        catch (Exception ex) { _scanning = false; ScanButton.Content = "SCAN"; ShowError("Scan failed", ex); }
    }

    private async void CivFeature_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        try
        {
            byte[] cmd; string status;
            switch (tag)
            {
                case "PRE":
                case "PREAMP": _preamp=!_preamp; if (_preamp) _attenuator=false; cmd=CatProtocol.SetPreamp(_preamp); status=$"Preamp {(_preamp?"ON":"OFF")}"; break;
                case "ATT": _attenuator=!_attenuator; if (_attenuator) _preamp=false; cmd=CatProtocol.SetAttenuator(_attenuator); status=$"20 dB ATT {(_attenuator?"ON":"OFF")}"; break;
                case "NB": _noiseBlanker=!_noiseBlanker; cmd=CatProtocol.SetNoiseBlanker(_noiseBlanker); status=$"Noise blanker {(_noiseBlanker?"ON":"OFF")}"; break;
                case "AGC": _agcFast=!_agcFast; cmd=CatProtocol.SetAgcFast(_agcFast); status=$"AGC {(_agcFast?"FAST":"SLOW")}"; break;
                case "VOX": _vox=!_vox; cmd=CatProtocol.SetVox(_vox); status=$"VOX {(_vox?"ON":"OFF")}"; break;
                case "COMP": _compressor=!_compressor; cmd=CatProtocol.SetCompressor(_compressor); status=$"Compressor {(_compressor?"ON":"OFF")}"; break;
                case "TONE": _ctcssEnabled=!_ctcssEnabled; cmd=CatProtocol.SetTone(_ctcssEnabled); status=$"Repeater tone {(_ctcssEnabled?"ON":"OFF")}"; break;
                case "TSQL": _toneSql=!_toneSql; cmd=CatProtocol.SetToneSquelch(_toneSql); status=$"Tone squelch {(_toneSql?"ON":"OFF")}"; break;
                case "BK": _breakIn=!_breakIn; cmd=CatProtocol.SetBreakIn(_breakIn); status=$"Break-in {(_breakIn?"ON":"OFF")}"; break;
                case "A=B": cmd=CatProtocol.EqualizeVfos(); status="VFO A=B"; break;
                default: return;
            }
            await SendAsync(cmd, status);
        }
        catch (Exception ex) { ShowError("CI-V function failed", ex); }
    }

    private async void Power_Click(object sender, RoutedEventArgs e)
    {
        // CI-V cannot safely power an IC-706MKIIG on/off. Use this faceplate button as Connect/Disconnect.
        Connect_Click(sender, e);
        await Task.CompletedTask;
    }


    private void DialLock_Click(object sender, RoutedEventArgs e)
    {
        _dialLocked = !_dialLocked;
        DialLockButton.Foreground = new SolidColorBrush(_dialLocked ? Color.FromRgb(255, 190, 70) : Colors.White);
        StatusText.Text = _dialLocked ? "Controller tuning dial LOCKED" : "Controller tuning dial unlocked";
    }

    private async void FilterCycle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _filterIndex = (_filterIndex + 1) % 3;
            await SendAsync(CatProtocol.SetModeFilter(_mode, (byte)_filterIndex), $"{_mode} filter {_filterIndex}");
        }
        catch (Exception ex) { ShowError("Filter change failed", ex); }
    }

    private async void RadioMemoryStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || !int.TryParse(b.Tag?.ToString(), out var delta)) return;
        try
        {
            _radioMemoryChannel = ((_radioMemoryChannel - 1 + delta + 99) % 99) + 1;
            await SendAsync(CatProtocol.SetMemoryMode(), "Radio memory mode");
            await SendAsync(CatProtocol.SelectMemory(_radioMemoryChannel), $"Radio memory {_radioMemoryChannel:00}");
            _memoryMode = true;
            MemoryDisplayText.Text = $"M-{_radioMemoryChannel:00}";
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("Memory channel change failed", ex); }
    }

    private async void RadioMemoryWrite_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = MessageBox.Show(this,
                $"Write the displayed frequency/mode into IC-706MKIIG memory {_radioMemoryChannel:00}?",
                "Write radio memory", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            // IC-706MKIIG CI-V: 08 selects memory mode/channel; 09 writes the displayed settings.
            await SendAsync(CatProtocol.SetMemoryMode(), "Radio MEMORY mode");
            await SendAsync(CatProtocol.SelectMemory(_radioMemoryChannel), $"Selected memory {_radioMemoryChannel:00}");
            await SendAsync(CatProtocol.MemoryWrite(), $"MW · memory {_radioMemoryChannel:00} written");
            _memoryMode = true;
            MemoryDisplayText.Text = $"M-{_radioMemoryChannel:00}";
            await ReadRadioAsync();
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("Radio memory write failed", ex); }
    }

    private async void RadioMemoryClear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = MessageBox.Show(this, $"Clear radio memory {_radioMemoryChannel:00}?", "Clear IC-706MKIIG memory", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            await SendAsync(CatProtocol.MemoryClear(), $"Radio memory {_radioMemoryChannel:00} cleared");
        }
        catch (Exception ex) { ShowError("Radio memory clear failed", ex); }
    }

    private async void BandGrid_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is null) return;
        try
        {
            var target = long.Parse(button.Tag.ToString()!, CultureInfo.InvariantCulture);
            await SendAsync(CatProtocol.SetFrequency(target), $"Band {button.Content} · {FormatFrequency(target)}");
            _frequencyHz = target;
            _memoryMode = false;
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("Band selection failed", ex); }
    }

    private async void Band_Click(object sender, RoutedEventArgs e)
    {
        var direction = sender is Button b && b.Tag?.ToString() == "DOWN" ? -1 : 1;
        try
        {
            int nearest = 0;
            long best = long.MaxValue;
            for (int i = 0; i < _bandHomes.Length; i++)
            {
                long d = Math.Abs(_bandHomes[i] - _frequencyHz);
                if (d < best) { best = d; nearest = i; }
            }
            int nextIndex = (nearest + direction + _bandHomes.Length) % _bandHomes.Length;
            var next = _bandHomes[nextIndex];
            await SendAsync(CatProtocol.SetFrequency(next), $"Band {(direction > 0 ? "up" : "down")} · {FormatFrequency(next)}");
            _frequencyHz = next; _memoryMode = false; UpdateDisplay();
        }
        catch (Exception ex) { ShowError("Band change failed", ex); }
    }

    private async void ModeStep_Click(object sender, RoutedEventArgs e)
    {
        var direction = sender is Button b && b.Tag?.ToString() == "DOWN" ? -1 : 1;
        try
        {
            int i = Array.IndexOf(_modeCycle, _mode);
            if (i < 0) i = 0;
            var next = _modeCycle[(i + direction + _modeCycle.Length) % _modeCycle.Length];
            await SendAsync(CatProtocol.SetMode(next), $"Mode {next}");
            _mode = next; UpdateDisplay();
        }
        catch (Exception ex) { ShowError("Mode change failed", ex); }
    }

    private async void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || !byte.TryParse(b.Tag?.ToString(), out var filter)) return;
        try { await SendAsync(CatProtocol.SetModeFilter(_mode, filter), $"{_mode} filter {filter}"); }
        catch (Exception ex) { ShowError("Filter change failed", ex); }
    }

    private async void Step_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || !long.TryParse(b.Tag?.ToString(), out var step)) return;
        try
        {
            await SendAsync(CatProtocol.SetTuningStep(step), $"Tuning step {step:N0} Hz");
            var i = Array.IndexOf(_steps, step); if (i >= 0) { _functionStepIndex = i; StepButton.Content = $"STEP\n{FormatStep(step)}"; }
        }
        catch (Exception ex) { ShowError("Tuning-step change failed", ex); }
    }

    private async void Dial_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_dialLocked) { StatusText.Text = "Tuning dial is LOCKED"; return; }
        await TuneDialAsync(e.Delta > 0 ? 1 : -1);
    }

    private async void RitStep_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _ritHz = Math.Clamp(_ritHz + int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture), -9990, 9990);
            await SendAsync(CatProtocol.SetClarifierOffset(_ritHz), $"RIT {_ritHz / 1000.0:+0.00;-0.00;0.00} kHz");
            if (!_clarifier) { await SendAsync(CatProtocol.SetClarifier(true), "Clarifier ON"); _clarifier = true; }
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("RIT failed", ex); }
    }

    private async void RitClear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _ritHz = 0;
            await SendAsync(CatProtocol.SetClarifierOffset(0), "RIT cleared");
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("RIT clear failed", ex); }
    }

    private decimal SelectedCtcssTone()
    {
        var text = CtcssBox.SelectedItem?.ToString() ?? CtcssBox.Text;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var tone)) throw new FormatException("Select a CTCSS tone.");
        return tone;
    }

    private long SelectedShiftHz()
    {
        if (!decimal.TryParse(ShiftAmountBox.Text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var mhz) || mhz < 0 || mhz > 99.99999m)
            throw new FormatException("Enter repeater shift in MHz, for example 0.600 or 1.600.");
        return decimal.ToInt64(decimal.Round(mhz * 1_000_000m / 10m) * 10m);
    }

    private async void CtcssOn_Click(object sender, RoutedEventArgs e)
    {
        try { _ctcssTone = SelectedCtcssTone(); await SendAsync(CatProtocol.SetCtcssTone(_ctcssTone), $"CTCSS {_ctcssTone:0.0} Hz"); await SendAsync(CatProtocol.SetCtcssMode(true), "CTCSS encoder ON"); _ctcssEnabled = true; UpdateRepeaterInfo(); }
        catch (Exception ex) { ShowError("CTCSS failed", ex); }
    }

    private async void CtcssOff_Click(object sender, RoutedEventArgs e)
    {
        try { await SendAsync(CatProtocol.SetCtcssMode(false), "CTCSS OFF"); _ctcssEnabled = false; UpdateRepeaterInfo(); }
        catch (Exception ex) { ShowError("CTCSS failed", ex); }
    }

    private async void Shift_Click(object sender, RoutedEventArgs e)
    {
        try { _repeaterShift = int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture); await SendAsync(CatProtocol.SetRepeaterShift(_repeaterShift), _repeaterShift < 0 ? "Repeater − shift" : _repeaterShift > 0 ? "Repeater + shift" : "Simplex"); UpdateRepeaterInfo(); }
        catch (Exception ex) { ShowError("Repeater shift failed", ex); }
    }

    private async void ApplyRepeater_Click(object sender, RoutedEventArgs e)
    {
        try { _repeaterOffsetHz = SelectedShiftHz(); await SendAsync(CatProtocol.SetRepeaterOffset(_repeaterOffsetHz), $"Repeater offset {_repeaterOffsetHz / 1_000_000.0:0.000} MHz"); await SendAsync(CatProtocol.SetRepeaterShift(_repeaterShift), "Repeater settings applied"); if (_ctcssEnabled) { _ctcssTone = SelectedCtcssTone(); await SendAsync(CatProtocol.SetCtcssTone(_ctcssTone), $"CTCSS {_ctcssTone:0.0} Hz"); await SendAsync(CatProtocol.SetCtcssMode(true), "CTCSS encoder ON"); } UpdateRepeaterInfo(); }
        catch (Exception ex) { ShowError("Repeater setup failed", ex); }
    }


    private void ReadRadioMemory_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "The IC-706MKIIG standard CI-V command set does not include a documented command for reading the radio's stored memory alpha tags.\n\n" +
            "This controller therefore keeps its own 200 named memories and does not use undocumented EEPROM addresses.",
            "IC-706MKIIG memory names", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveMemory_Click(object sender, RoutedEventArgs e)
    {
        try { var i = MemoryChannelBox.SelectedIndex; if (i < 0) return; _ctcssTone = SelectedCtcssTone(); _repeaterOffsetHz = SelectedShiftHz(); var name = string.IsNullOrWhiteSpace(MemoryNameBox.Text) ? $"CH {i + 1:00}" : MemoryNameBox.Text.Trim(); _memories[i] = new(name, _frequencyHz, _mode, _ctcssEnabled, _ctcssTone, _repeaterShift, _repeaterOffsetHz); SaveMemories(); MemoryInfoText.Text = $"Saved M-{i + 1:000} {name} · {FormatFrequency(_frequencyHz)} {_mode}"; MemoryDisplayText.Text = $"M-{i + 1:000} {name}"; }
        catch (Exception ex) { ShowError("Memory save failed", ex); }
    }

    private async void RecallMemory_Click(object sender, RoutedEventArgs e)
    {
        try { var i = MemoryChannelBox.SelectedIndex; if (i < 0 || _memories[i] is not { } m) throw new InvalidOperationException("That memory channel is empty."); MemoryNameBox.Text = m.Name; _frequencyHz = m.FrequencyHz; _mode = m.Mode; _ctcssEnabled = m.CtcssEnabled; _ctcssTone = m.CtcssTone; _repeaterShift = m.Shift; _repeaterOffsetHz = m.OffsetHz; CtcssBox.SelectedItem = m.CtcssTone.ToString("0.0", CultureInfo.InvariantCulture); ShiftAmountBox.Text = (m.OffsetHz / 1_000_000m).ToString("0.00000", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.'); await SendAsync(CatProtocol.SetFrequency(m.FrequencyHz), $"Memory {i + 1:00} {m.Name}"); await SendAsync(CatProtocol.SetMode(m.Mode), $"Mode {m.Mode}"); await SendAsync(CatProtocol.SetRepeaterOffset(m.OffsetHz), "Repeater offset restored"); await SendAsync(CatProtocol.SetRepeaterShift(m.Shift), "Repeater shift restored"); await SendAsync(CatProtocol.SetCtcssTone(m.CtcssTone), $"CTCSS {m.CtcssTone:0.0} Hz"); await SendAsync(CatProtocol.SetCtcssMode(m.CtcssEnabled), m.CtcssEnabled ? "CTCSS ON" : "CTCSS OFF"); UpdateDisplay(); UpdateRepeaterInfo(); MemoryInfoText.Text = $"Recalled M-{i + 1:000} {m.Name}"; MemoryDisplayText.Text = $"M-{i + 1:000} {m.Name}"; }
        catch (Exception ex) { ShowError("Memory recall failed", ex); }
    }

    private void UpdateRepeaterInfo() => MemoryInfoText.ToolTip = $"CTCSS {(_ctcssEnabled ? _ctcssTone.ToString("0.0") + " Hz" : "OFF")} · Shift {(_repeaterShift < 0 ? "−" : _repeaterShift > 0 ? "+" : "simplex")} {_repeaterOffsetHz / 1_000_000.0:0.000} MHz";

    private void LoadMemories()
    {
        try { if (!File.Exists(MemoryFile)) return; var loaded = JsonSerializer.Deserialize<MemoryChannel?[]>(File.ReadAllText(MemoryFile)); if (loaded is null) return; Array.Copy(loaded, _memories, Math.Min(loaded.Length, _memories.Length)); }
        catch { }
    }

    private void SaveMemories()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MemoryFile)!);
        File.WriteAllText(MemoryFile, JsonSerializer.Serialize(_memories, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task SetRadioStateAsync(long frequency, RadioMode mode, string status)
    {
        await SendAsync(CatProtocol.SetFrequency(frequency), status);
        await SendAsync(CatProtocol.SetMode(mode), status);
        _frequencyHz = frequency; _mode = mode;
    }

    private void UpdateDisplay()
    {
        if (FrequencyBox.Visibility != Visibility.Visible) FrequencyBox.Text = FormatFrequency(_frequencyHz);
        FrequencyReadout.Text = FormatFrequency(_frequencyHz);
        ModeText.Text = _mode.ToString();
        VfoText.Text = _vfoB ? "VFO B" : "VFO A";
        SplitText.Text = _split ? "SPLIT ON" : "SPLIT OFF";
        SplitButton.Background = new SolidColorBrush(_split ? Color.FromRgb(164, 105, 30) : Color.FromRgb(69, 74, 79));
        ClarButton.Background = new SolidColorBrush(_clarifier ? Color.FromRgb(164, 105, 30) : Color.FromRgb(23, 25, 26));
        VfoText.Text = _memoryMode ? "MEM" : (_vfoB ? "VFO B" : "VFO A");
    }

    private static string FormatFrequency(long hz)
    {
        var mhz = hz / 1_000_000;
        var fraction10Hz = (hz % 1_000_000) / 10;
        var fraction = fraction10Hz.ToString("D5");
        return $"{mhz:000}.{fraction[..3]}.{fraction[3..]}";
    }

    private static long ParseFrequency(string text)
    {
        var trimmed = text.Trim().Replace(',', '.');
        if (trimmed.Count(c => c == '.') > 1)
        {
            var digits = trimmed.Replace(".", "");
            if (!long.TryParse(digits, out var units10Hz)) throw new FormatException("Enter a frequency such as 145.500 or 145.500.00.");
            return units10Hz * 10;
        }
        if (!decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mhz))
            throw new FormatException("Enter a frequency in MHz, such as 145.500.");
        return decimal.ToInt64(decimal.Round(mhz * 1_000_000m / 10m) * 10m);
    }

    private static void ShowError(string title, Exception ex) =>
        MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
