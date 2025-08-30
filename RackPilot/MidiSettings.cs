using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RackPilot
{
    public sealed class MidiSettings
    {
        public string OutputDeviceName { get; set; } = "";
        public int Channel { get; set; } = 1;
        public ClockSettings Clock { get; set; } = new();
        public NoteSettings Note { get; set; } = new();
        public CcSettings Cc { get; set; } = new();
        public sealed class ClockSettings { public bool SendClock { get; set; } = true; public int Bpm { get; set; } = 120; }
        public sealed class NoteSettings
        {
            public string Scale { get; set; } = "Minor";
            public string Root { get; set; } = "C4";
            public int StepsPerBar { get; set; } = 16;
            public int GateMs { get; set; } = 120;
            public int Velocity { get; set; } = 96;
            public string Pattern { get; set; } = "up";
        }
        public sealed class CcSettings
        {
            public bool Enabled { get; set; } = true;
            public int Number { get; set; } = 1;
            public int Min { get; set; } = 20;
            public int Max { get; set; } = 100;
            public int IntervalMs { get; set; } = 30;
        }

        public DebugSettings Debug { get; set; } = new();
        public sealed class DebugSettings { public bool LogToConsole { get; set; } = false; }
    }
}
