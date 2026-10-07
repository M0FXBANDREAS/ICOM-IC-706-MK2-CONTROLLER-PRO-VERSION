# HamTech M0FXB — Icom IC-706 Controller

**A Windows desktop controller with an Icom-inspired display, tuning dial, CI-V controls and PC memories.**

Created by **HamTech M0FXB / M0FXBANDREAS**.

> **Radio compatibility:** although this repository is named `ICOM-IC-706MK2-CONTROLLER`, the supplied application targets the **IC-706MKIIG** and uses CI-V address **58h**. Compatibility with the IC-706 or IC-706MKII is not established by this source.

## Features

- Read and set operating frequency.
- Select LSB, USB, CW, RTTY/DIG, AM, FM and WFM modes.
- Tune using the on-screen dial, mouse wheel and frequency-step buttons.
- Select band presets from 160 m through 70 cm.
- Switch VFO A/B and toggle split.
- Display live CI-V S-meter readings.
- Control supported preamp, attenuator, noise blanker and AGC functions.
- Start and stop radio scanning.
- Save up to **200 named PC memories**.
- Display an audio waveform from the PC’s default recording input.
- Adjust Windows playback volume and mute.
- Select the COM port and baud rate.
- Explore the interface in Demo mode.
- Use full-screen mode with **F11**.

Feature availability depends on the radio, operating mode and the implementation limits described below.

## Requirements

- A Windows PC.
- An Icom IC-706MKIIG.
- A suitable USB-to-CI-V interface connected to the radio’s **REMOTE** socket.
- The interface’s Windows driver.
- **.NET 8 Desktop Runtime** to run the included framework-dependent executable.
- **.NET 8 SDK** to build or use the source launcher.

CI-V carries control commands, not radio audio. A separate audio connection is needed to display radio audio in the scope or hear it through the PC.

## Download and launch

### Run the included executable

1. Select **Code → Download ZIP** on GitHub.
2. Extract the entire ZIP.
3. Install the **.NET 8 Desktop Runtime** if required.
4. Open:

```text
FT857DControl/bin/Release/net8.0-windows/
```

5. Run:

```text
HamTech-M0FXB-IC706MKIIG-Controller.exe
```

Keep the executable with its accompanying DLLs, configuration files and runtime folders.

### Run from source

Install the **.NET 8 SDK**, then double-click:

```text
START-HAMTECH-IC706MKIIG-CONTROLLER.cmd
```

Alternatively, open a terminal in the project root:

```powershell
dotnet run --project .\FT857DControl\FT857DControl.csproj -c Release
```

The launcher builds and runs the source in Release configuration.

## Radio setup

Configure the radio for the application’s CI-V settings:

| Setting | Value |
| --- | --- |
| CI-V address | **58h** |
| CI-V baud rate | Match the value selected in the application. |
| CI-V 731 mode | **OFF**, for the five-byte frequency format. |
| CI-V transceive | OFF can simplify polling; the original project guidance allows either setting. |

The application initializes its baud setting to **9600**. Its connection routine accepts 4800, 9600, 19200 and 38400; select a rate supported by your radio and interface.

The radio address is fixed in `CatProtocol.cs`; it is not selectable in the interface.

## Connect to the radio

1. Turn on the radio and connect the CI-V interface.
2. Launch the controller.
3. Click **Refresh** if the COM port is not listed.
4. Select the correct COM port, or type it manually, such as `COM3`.
5. Select the matching baud rate.
6. Leave **Demo mode** unchecked.
7. Click **Connect**.
8. Use **View → Read radio now** to refresh frequency and mode.

The connection routine waits for a valid radio reply. Connection errors include transmit and receive diagnostics to help identify communication problems.

The faceplate power button connects or disconnects the controller. It does not switch the radio’s physical power.

## Demo mode

To explore the interface without a radio:

1. Select **Demo mode**.
2. Click **Connect**.
3. Try the tuning and display controls.

Demo mode does not send CI-V commands. Controls that are deliberately unsupported may still report a limitation.

## Main controls

| Control | Purpose |
| --- | --- |
| Tuning dial | Tune by dragging or using the mouse wheel. |
| Frequency buttons | Apply the labelled frequency increments. |
| Settings → Tuning step | Select the controller dial step. |
| Band buttons | Jump to preset frequencies. |
| Mode buttons | Set the operating mode. |
| A/B | Swap between VFO A and VFO B. |
| SPL | Toggle split operation. |
| SCAN | Start or stop radio scanning. |
| PRE / ATT / NB / AGC | Control the supported receiver functions. |
| PC SAVE | Save the controller’s current settings to a named PC memory. |
| PC RECALL | Attempt to restore a saved PC memory; see the limitation below. |
| VOL − / VOL + / MUTE | Adjust the Windows playback endpoint. |
| F11 | Toggle full-screen mode. |

Band presets are frequency shortcuts. They do not determine whether transmission is permitted on a selected frequency.

## PC memories

The controller stores up to **200 named memories** on the PC, separately from the radio’s internal memory bank.

The memory file is:

```text
%LOCALAPPDATA%\HamTech M0FXB FT857D Controller\memories.json
```

The folder retains a legacy project name.

Stored records include frequency, mode and repeater/tone settings.

> **Current recall limitation:** the recall routine attempts to program the CTCSS tone frequency, but that operation is deliberately unsupported by this implementation. Recall can report an error after already applying frequency, mode and repeater settings. It should not be treated as a complete, successful restore.

The **CAT LIMIT** button explains that this implementation cannot read the radio’s stored memory names through its documented CI-V command set.

## Audio scope and PC audio

The audio scope captures the PC’s **default recording input**. It is an audio waveform display, not an RF spectrum display.

To show received radio audio, connect an appropriate radio audio output to a suitable PC audio input and select that input in Windows.

The volume buttons affect **Windows playback volume**, not the radio’s physical AF control.

PC squelch uses received signal information to mute or unmute Windows audio. It does not adjust the radio’s physical squelch or RF gain controls.

## Deliberate limits

This controller leaves certain operations to the radio:

- **PTT:** use the microphone or radio PTT. CI-V PTT is disabled.
- **RIT/clarifier:** control it on the radio.
- **CTCSS tone frequency:** set it on the radio. The tone-frequency programming method is unsupported.
- **Tuner start:** operate the tuner from the radio.
- **Physical RF gain:** use the radio’s control.
- **Stored radio memory names:** these are not read by this implementation.

Some buttons or menu entries remain visible even when the underlying operation is unsupported. They may show an explanatory message instead of performing an action.

The **Tone ON** routine also attempts unsupported tone-frequency programming before enabling the tone, so it can fail. Repeater **APPLY** can similarly report an error when tone programming is requested.

## Build a standalone Windows version

From PowerShell in the project root:

```powershell
.\build-windows.ps1
```

The script publishes a self-contained Windows x64 build into:

```text
publish-win-x64/
```

Run:

```text
HamTech-M0FXB-IC706MKIIG-Controller.exe
```

from that output folder.

## Protocol tests

Run the included xUnit protocol tests with:

```powershell
dotnet test .\FT857DControl.Tests\FT857DControl.Tests.csproj
```

These tests check protocol encoding. They do not establish compatibility with every radio or replace hardware testing.

## Troubleshooting

| Problem | What to check |
| --- | --- |
| Application requests .NET | Install the .NET 8 Desktop Runtime for the included executable, or the SDK for source builds. |
| Launcher cannot find the project | Extract the whole ZIP and preserve its folder structure. |
| No COM port appears | Check the interface driver in Windows Device Manager, then click Refresh. |
| COM port cannot be opened | Close other applications using that port. |
| Port opens but no radio reply arrives | Check radio power, cable, address 58h, matching baud and CI-V 731 OFF. |
| PTT, RIT or tuner does not operate | These operations are deliberately left to the radio. |
| PC memory recall reports a tone error | Recall reaches the unsupported CTCSS tone-frequency operation; some settings may already have changed. |
| Audio scope is flat | Check the Windows default recording input and the separate audio connection. |

## Project structure

| File or folder | Purpose |
| --- | --- |
| `FT857DControl/MainWindow.xaml` | WPF interface layout. |
| `FT857DControl/MainWindow.xaml.cs` | Controller behaviour and event handlers. |
| `FT857DControl/Services/CatProtocol.cs` | Icom CI-V command encoding. |
| `FT857DControl/Services/RadioConnection.cs` | Serial communication and radio replies. |
| `FT857DControl.Tests/` | Protocol tests. |
| `START-HAMTECH-IC706MKIIG-CONTROLLER.cmd` | Source build-and-run launcher. |
| `build-windows.ps1` | Self-contained Windows publishing script. |

The source folder and namespace retain the name `FT857DControl`, but the radio protocol implementation uses **Icom CI-V**.

## Credits

**HamTech M0FXB / M0FXBANDREAS**

An independent controller project for the Icom IC-706MKIIG.

[View the project on GitHub](https://github.com/M0FXBANDREAS/ICOM-IC-706MK2-CONTROLLER)

**73 — HamTech M0FXB**


[ICOM-IC-706-MK2-CONTROLLER-PRO-VERSION-main.zip](https://github.com/user-attachments/files/33152945/ICOM-IC-706-MK2-CONTROLLER-PRO-VERSION-main.zip)
[IC706-README-with-pictures.zip](https://github.com/user-attachments/files/33152905/IC706-README-with-pictures.zip)
HamTech M0FXB IC-706MKIIG Controller V1.17.1

Focused fix only: far-left LCD M1/M2/M3/M4 selector.

The V1.17 CI-V/V-M code is unchanged.

Why this fix is different:
V1.17 put a MouseLeftButtonDown handler on the M-page Border, but the whole LCD also has a mouse handler for frequency entry. V1.17.1 adds a PreviewMouseLeftButtonDown handler at the LCD itself and intercepts the far-left bottom M-page region before normal bubbling/frequency-entry handling. This gives M1/M2/M3/M4 a generous dedicated hit area.

Expected action:
Click the far-left M-page text/area on the green LCD:
M1 -> M2 -> M3 -> M4 -> M1

Pages:
M1: SPL / A-B / A=B
M2: MW / M->V / V/M
M3: FIL / NB / MET
M4: VOX / COMP / AGC (SSB/AM controller page)

Note: This cycles the PC controller's M-page. Standard published IC-706MKIIG CI-V does not expose a command to remotely change the physical radio's MENU M-page.

V1.18 additions
- GREEN STEP button: cycles controller step and sends the documented CI-V tuning-step command to the IC-706MKIIG.
- ORANGE FILTER button: cycles WIDE / NORM / NAR using the existing radio filter command path.
- Existing working V/M and other CI-V controls left unchanged.
