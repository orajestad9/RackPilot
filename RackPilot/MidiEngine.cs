using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using System.Diagnostics;
using System.Runtime;
using System.Linq;

namespace RackPilot
{
    internal class MidiEngine : IAsyncDisposable
    {
        private double _swing = 0.58;       // 0.50 = straight, 0.58–0.62 = nice swing
        private int _accentEvery = 4;       // accent every N steps
        private int _humanizeMs = 6;        // +/- timing jitter in ms
        private int _humanizeVel = 6;       // +/- velocity jitter
        private volatile bool _running = true;
        private volatile bool _randomPattern = false;
        private int _bpm;
        private readonly MidiSettings _cfg;
        private readonly OutputDevice _out;
        private readonly FourBitNumber _channel;
        private readonly CancellationTokenSource _cts = new();
        private Task? _clockTask, _seqTask, _ccTask;

        public MidiEngine(MidiSettings cfg)
        {
            _cfg = cfg;
            _out = OutputDevice.GetByName(cfg.OutputDeviceName)
                ?? throw new InvalidOperationException($"MIDI out '{cfg.OutputDeviceName}' not found. Use --list to enumerate.");
            _channel = (FourBitNumber)Math.Clamp(cfg.Channel - 1, 0, 15);
            _bpm = _cfg.Clock.Bpm;
        }

        public static void ListDevices()
        {
            Console.WriteLine("Available MIDI OUT devices:");
            foreach (var d in OutputDevice.GetAll()) Console.WriteLine($" - {d.Name}");
        }

        public Task StartAsync()
        {
            // Low-latency GC and higher process priority while running
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { /* non-Windows ok */ }
             
            // (optional) pick a GM patch up front
            Send(new ProgramChangeEvent((SevenBitNumber)80) { Channel = _channel }); // Square Lead

            // Kick off background loops (no local 'token' needed)
            Task.Run(() => ConsoleUiLoop(_cts.Token));
            if (_cfg.Clock.SendClock) _clockTask = Task.Run(() => ClockLoop(_cts.Token));
            _seqTask = Task.Run(() => SequencerLoop(_cts.Token));
            if (_cfg.Cc.Enabled) _ccTask = Task.Run(() => CcLoop(_cts.Token));

            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await Task.WhenAll(new[] { _clockTask, _seqTask, _ccTask }.Where(t => t != null)!); } catch { /* ignore */ }
            // Send MIDI Stop for good measure
            try { Send(new StopEvent()); } catch { }
            _out.Dispose();
        }

        private void ClockLoop(CancellationToken ct)
        {
            // 24 PPQN MIDI Clock
            var intervalMs = 60000.0 / (_cfg.Clock.Bpm * 24.0);
            var sw = Stopwatch.StartNew();
            long tick = 0;
            Send(new StartEvent());
            while (!ct.IsCancellationRequested)
            {
                var bpm = Math.Max(20, Math.Min(300, Volatile.Read(ref _bpm)));
                intervalMs = 60000.0 / (bpm * 24.0);
                if (!_running) { Thread.Sleep(5); sw.Restart(); tick = 0; continue; }
                double nextMs = (++tick) * intervalMs;
                BusyWaitUntil(sw, nextMs, ct);
                Send(new TimingClockEvent());
            }
        }

        private void SequencerLoop(CancellationToken ct)
        {
            int steps = Math.Max(1, _cfg.Note.StepsPerBar);
            double stepMs;
            var sw = Stopwatch.StartNew();
            int stepIndex = 0;

            // Simple arpeggio source based on scale
            int root = NoteNameToMidi(_cfg.Note.Root);
            var scale = BuildScale(root, _cfg.Note.Scale);
            int[] arpDegrees = { 0, 2, 4, 7 }; // 1, 3, 5, octave+ a 5th feel

            while (!ct.IsCancellationRequested)
            {
                var bpm = Math.Max(20, Math.Min(300, Volatile.Read(ref _bpm)));
                stepMs = 60000.0 / (bpm * (steps / 4.0));
                if (!_running) { Thread.Sleep(5); sw.Restart(); stepIndex = 0; continue; }
                double nextMs = (stepIndex + 1) * stepMs;
                string pattern = _randomPattern ? "random" : _cfg.Note.Pattern;

                int note = pattern.ToLowerInvariant() switch
                {
                    "random" => scale[Random.Shared.Next(scale.Length)],
                    _ => scale[arpDegrees[stepIndex % arpDegrees.Length]]
                };

                // --- groove params ---
                bool oddStep = (stepIndex % 2) == 1;
                int swingDelay = oddStep ? (int)Math.Clamp((_swing - 0.5) * stepMs, 0, stepMs * 0.4) : 0;

                // Humanize timing (small, symmetric jitter)
                int jitter = Random.Shared.Next(-_humanizeMs, _humanizeMs + 1);

                // Compute velocity with accents + humanize
                int baseVel = _cfg.Note.Velocity;
                bool accented = (_accentEvery > 0) && (stepIndex % _accentEvery == 0);
                if (accented) baseVel = Math.Min(127, baseVel + 14);
                int vel = Math.Clamp(baseVel + Random.Shared.Next(-_humanizeVel, _humanizeVel + 1), 1, 127);

                // Wait until swung onset (before NoteOn)
                SleepAccurate(swingDelay + Math.Max(0, jitter), ct);

                // Note on
                Send(new NoteOnEvent((SevenBitNumber)note, (SevenBitNumber)vel) { Channel = _channel });

                // Gate (keep inside the step even with swing)
                int gate = Math.Min(_cfg.Note.GateMs, (int)(stepMs - swingDelay - 5));
                gate = Math.Max(20, gate); // safety min
                SleepAccurate(gate, ct);

                // Note off
                Send(new NoteOffEvent((SevenBitNumber)note, (SevenBitNumber)0) { Channel = _channel });

                // Wait until next step boundary
                BusyWaitUntil(sw, nextMs, ct);
                stepIndex = (stepIndex + 1) % steps;
            }
        }

        private void CcLoop(CancellationToken ct)
        {
            int cc = Math.Clamp(_cfg.Cc.Number, 0, 127);
            int min = Math.Clamp(_cfg.Cc.Min, 0, 127);
            int max = Math.Clamp(_cfg.Cc.Max, 0, 127);
            int interval = Math.Max(5, _cfg.Cc.IntervalMs);

            double phase = 0.0;
            while (!ct.IsCancellationRequested)
            {
                // Triangle-ish LFO 0..1
                phase += 0.015; if (phase > 1) phase -= 1;
                double tri = phase < 0.5 ? (phase * 2) : (2 - phase * 2);
                int val = min + (int)Math.Round(tri * (max - min));
                Send(new ControlChangeEvent((SevenBitNumber)cc, (SevenBitNumber)val) { Channel = _channel });
                SleepAccurate(interval, ct);
            }
        }

        private static void BusyWaitUntil(Stopwatch sw, double targetMs, CancellationToken ct)
        {
            // Sleep most of it, spin the last ~2ms for tighter timing
            while (true)
            {
                double remaining = targetMs - sw.Elapsed.TotalMilliseconds;
                if (ct.IsCancellationRequested || remaining <= 0) return;
                if (remaining > 2.0) Thread.Sleep((int)Math.Floor(remaining - 1.0));
                else Thread.SpinWait(2000);
            }
        }

        private static void SleepAccurate(int ms, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested && sw.ElapsedMilliseconds < ms)
            {
                var remain = ms - sw.ElapsedMilliseconds;
                if (remain > 2) Thread.Sleep(1);
                else Thread.SpinWait(2000);
            }
        }

        private static int[] BuildScale(int root, string scaleName)
        {
            int[] major = { 0, 2, 4, 5, 7, 9, 11, 12 };
            int[] minor = { 0, 2, 3, 5, 7, 8, 10, 12 };
            var src = (scaleName?.ToLowerInvariant()) switch { "major" => major, _ => minor };
            return src.Select(semi => root + semi).ToArray();
        }

        private static int NoteNameToMidi(string name)
        {
            // e.g., C4, D#3, Bb2
            if (string.IsNullOrWhiteSpace(name)) return 60;
            name = name.Trim().ToUpperInvariant();
            string[] pitch = { "C", "C#", "DB", "D", "D#", "EB", "E", "F", "F#", "GB", "G", "G#", "AB", "A", "A#", "BB", "B" };
            int[] semis = { 0, 1, 1, 2, 3, 3, 4, 5, 6, 6, 7, 8, 8, 9, 10, 10, 11 };
            string l = name[..^1];
            int oct = int.Parse(name[^1].ToString());
            int idx = Array.FindIndex(pitch, p => p == l);
            if (idx < 0) idx = 0;
            return 12 + oct * 12 + semis[idx]; // C0 = 12
        }

        private void Send(MidiEvent e)
        {
            // Optional: keep logs readable by skipping spammy clock ticks
            if (_cfg.Debug.LogToConsole && e is not TimingClockEvent)
            {
                var ts = DateTime.Now.ToString("HH:mm:ss.fff");
                Console.WriteLine($"{ts} -> {e}");
            }

            _out.SendEvent(e); // <-- send to the MIDI device (no recursion)
        }

        private void ConsoleUiLoop(CancellationToken ct)
        {
            Console.WriteLine("[SPACE]=Play/Pause  ↑/↓=BPM ±2  R=Random Pattern  P=Program#  Q=Quit");
            while (!ct.IsCancellationRequested)
            {
                if (!Console.KeyAvailable) { Thread.Sleep(25); continue; }
                var k = Console.ReadKey(true).Key;

                if (k == ConsoleKey.Spacebar) { _running = !_running; Console.WriteLine(_running ? "▶ Play" : "⏸ Pause"); }
                else if (k == ConsoleKey.UpArrow) { Interlocked.Add(ref _bpm, 2); Console.WriteLine($"BPM: {_bpm}"); }
                else if (k == ConsoleKey.DownArrow) { Interlocked.Add(ref _bpm, -2); Console.WriteLine($"BPM: {_bpm}"); }
                else if (k == ConsoleKey.R) { _randomPattern = !_randomPattern; Console.WriteLine($"Pattern: {(_randomPattern ? "Random" : "Up")}"); }
                else if (k == ConsoleKey.P)
                {
                    Console.Write("Program (0–127): ");
                    if (int.TryParse(Console.ReadLine(), out var prog)) Send(new ProgramChangeEvent((SevenBitNumber)Math.Clamp(prog, 0, 127)) { Channel = _channel });
                }
                else if (k == ConsoleKey.Q) { _cts.Cancel(); break; }
                else if (k == ConsoleKey.S) { _swing = Math.Min(0.70, _swing + 0.02); Console.WriteLine($"Swing: {_swing:0.00}"); }
                else if (k == ConsoleKey.A) { _swing = Math.Max(0.50, _swing - 0.02); Console.WriteLine($"Swing: {_swing:0.00}"); }
                else if (k == ConsoleKey.D1) { _accentEvery = Math.Max(0, _accentEvery - 1); Console.WriteLine($"Accent every: {(_accentEvery == 0 ? "off" : _accentEvery)}"); }
                else if (k == ConsoleKey.D2) { _accentEvery = Math.Min(16, _accentEvery + 1); Console.WriteLine($"Accent every: {(_accentEvery == 0 ? "off" : _accentEvery)}"); }
            }
        }
    }
}
