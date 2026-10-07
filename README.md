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
