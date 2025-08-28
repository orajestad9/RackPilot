# RackPilot

Tiny .NET 8 MIDI brain that drives a synth (or MIDI→CV) with a quantized arpeggiator, CC modulation, and live console controls.

SPACE = Play/Pause

↑ / ↓ = BPM ±2

R = Toggle Random pattern

P = Enter GM Program number (0–127)

S = Toggle Swing

Q = Quit

## Run
```bash
dotnet run -- --list   # see output devices
copy appsettings.json.example appsettings.json
# set Midi:OutputDeviceName in appsettings.json
dotnet run