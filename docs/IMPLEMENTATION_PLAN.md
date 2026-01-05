# Sendspin Linux Client - Implementation Plan

## Summary

Port the Sendspin Windows client to Linux as a **separate repository** using:
- **Avalonia UI** - Cross-platform WPF-like framework
- **PipeWire** (via OpenAL Soft) - Modern Linux audio backend
- **Sendspin.SDK** NuGet package - Cross-platform protocol (already ready)

## Architecture Overview

```
Linuxspin/
├── src/
│   ├── SendspinClient.Linux/           # Avalonia UI app
│   │   ├── Configuration/              # XDG directory handling
│   │   ├── ViewModels/                 # Port from Windows (minor changes)
│   │   └── Views/                      # AXAML files (port from XAML)
│   │
│   └── SendspinClient.Linux.Services/  # Linux platform services
│       ├── Audio/                      # PipeWire audio player
│       ├── Notifications/              # D-Bus notifications
│       └── Discord/                    # Rich presence (likely works as-is)
│
└── packaging/                          # AppImage, Flatpak, .deb
```

## What's Reused vs. Replaced

| Component | Windows | Linux | Notes |
|-----------|---------|-------|-------|
| Protocol SDK | Sendspin.SDK | Sendspin.SDK | **NuGet package, no changes** |
| Audio output | NAudio/WASAPI | OpenAL Soft/PipeWire | New implementation |
| Resampling | WdlResampler | Speex DSP | For sync correction |
| UI Framework | WPF | Avalonia | Port XAML → AXAML |
| Notifications | UWP Toast | D-Bus | New implementation |
| Discord | DiscordRPC | DiscordRPC | Likely works as-is |
| Paths | %LocalAppData% | XDG dirs | ~/.config, ~/.local/share |

## Key Interface to Implement

The Linux audio player must implement `IAudioPlayer` from the SDK:

```csharp
public interface IAudioPlayer : IAsyncDisposable
{
    AudioPlayerState State { get; }
    float Volume { get; set; }
    bool IsMuted { get; set; }
    int OutputLatencyMs { get; }  // Critical for sync!

    Task InitializeAsync(AudioFormat format, CancellationToken ct);
    void SetSampleSource(IAudioSampleSource source);
    void Play();
    void Pause();
    void Stop();
    Task SwitchDeviceAsync(string? deviceId, CancellationToken ct);

    event EventHandler<AudioPlayerState>? StateChanged;
    event EventHandler<AudioPlayerError>? ErrorOccurred;
}
```

## Implementation Phases

### Phase 1: Repository & Audio Core (Priority: Critical)

1. **Create repository structure**
   - Solution file, project files
   - Reference Sendspin.SDK v3.1.0 NuGet package
   - Set up CI workflow

2. **Implement XDG paths** (`Configuration/AppPaths.cs`)
   ```csharp
   XDG_CONFIG_HOME → ~/.config/sendspin/
   XDG_DATA_HOME   → ~/.local/share/sendspin/
   XDG_CACHE_HOME  → ~/.cache/sendspin/
   ```

3. **Implement PipeWire audio player** (`Audio/PipeWireAudioPlayer.cs`)
   - Use **OpenAL Soft** (has PipeWire backend, simpler API)
   - ~100ms buffer latency target (matching Windows)
   - Report actual latency for sync compensation

4. **Implement Speex resampler** (`Audio/SpeexResampler.cs`)
   - Rate range: 0.96x - 1.04x (±4%)
   - Required for smooth sync correction

### Phase 2: Basic UI & Integration

5. **Create Avalonia app shell**
   - `App.axaml.cs` with DI setup (mirror Windows pattern)
   - `MainWindow.axaml` basic layout

6. **Port MainViewModel**
   - Replace `System.Windows.Threading` → `Avalonia.Threading`
   - Remove NAudio device enumeration (move to services)
   - Keep all SDK integration code

7. **Wire up full pipeline**
   - Server discovery → Connection → Audio playback
   - Verify sync accuracy

### Phase 3: Platform Features

8. **D-Bus notifications** (`Notifications/DBusNotificationService.cs`)
   - Use `Tmds.DBus` package
   - Track change, connection status notifications

9. **Discord Rich Presence**
   - Test existing `DiscordRichPresence` package on Linux
   - Adapt if needed

10. **Audio device selection**
    - Enumerate devices via OpenAL
    - Settings UI for device selection

### Phase 4: Packaging & Release

11. **AppImage** (primary)
    - Self-contained, works on most distros
    - Desktop integration file

12. **Flatpak** (sandboxed)
    - For software centers
    - Needs PulseAudio socket permission (PipeWire compatible)

13. **Debian package** (optional)
    - For apt-based distros

## Key NuGet Dependencies

```xml
<!-- Avalonia UI -->
<PackageReference Include="Avalonia" Version="11.2.5" />
<PackageReference Include="Avalonia.Desktop" Version="11.2.5" />
<PackageReference Include="Avalonia.Themes.Fluent" Version="11.2.5" />

<!-- MVVM (same as Windows) -->
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />

<!-- Cross-platform SDK -->
<PackageReference Include="Sendspin.SDK" Version="3.1.0" />

<!-- Audio (OpenAL with PipeWire backend) -->
<PackageReference Include="OpenAL.NETCore" Version="1.0.9" />

<!-- Resampling for sync -->
<PackageReference Include="SpeexDSP.NETCore" Version="0.0.1" />

<!-- D-Bus notifications -->
<PackageReference Include="Tmds.DBus" Version="0.21.1" />

<!-- Discord (same as Windows) -->
<PackageReference Include="DiscordRichPresence" Version="1.3.0.28" />
```

## Technical Challenges & Solutions

### 1. Latency Reporting
**Problem:** OpenAL doesn't expose exact output latency like WASAPI.
**Solution:** Use `AL_SEC_OFFSET` to track playback position, or make latency configurable (like `StaticDelayMs` on Windows).

### 2. Dynamic Resampling
**Problem:** NAudio's WdlResampler is Windows-specific.
**Solution:** Use Speex DSP resampler with quality level 5+ for comparable quality.

### 3. System Tray
**Problem:** Linux system tray support is inconsistent across DEs.
**Solution:** Use `StatusNotifierItem` D-Bus interface (KDE, modern GNOME), fall back to AppIndicator (Ubuntu), or omit tray initially.

### 4. Audio Device Hot-Switching
**Pattern from Windows** (WasapiAudioPlayer.cs:246-343):
1. Stop playback, remember state
2. Dispose current device/context
3. Open new device/context
4. Re-attach sample source + resampler
5. Resume if was playing

## Reference Files

These Windows files serve as implementation patterns:

| Purpose | Reference File |
|---------|----------------|
| Audio player structure | `src/SendspinClient.Services/Audio/WasapiAudioPlayer.cs` |
| Resampler integration | `src/SendspinClient.Services/Audio/DynamicResamplerSampleProvider.cs` |
| DI configuration | `src/SendspinClient/App.xaml.cs` |
| ViewModel patterns | `src/SendspinClient/ViewModels/MainViewModel.cs` |
| IAudioPlayer interface | `src/Sendspin.SDK/Audio/IAudioPlayer.cs` |

## Success Criteria

- [ ] Connects to Music Assistant server via mDNS
- [ ] Plays audio in sync with other Sendspin clients (< 10ms drift)
- [ ] Sync correction via resampling works smoothly
- [ ] Track change notifications appear
- [ ] Volume control works
- [ ] Audio device selection works
- [ ] Builds as AppImage for distribution
