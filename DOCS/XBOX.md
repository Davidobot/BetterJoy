# Xbox controller support

BetterJoy's Xbox input path claims any physical XInput-compatible controller: Microsoft pads over
USB or the Xbox Wireless Adapter, and licensed third-party pads such as the SCUF Valor Pro
(`1B1C:3A15`). Windows marks XInput-compatible HID collections with an `IG_` segment in the device
path, which is how they are recognized. BetterJoy's own ViGEm virtual outputs are excluded, and
recognized Xbox input takes priority over third-party controller entries, so an Xbox-compatible pad
is never decoded as a Nintendo controller. Bluetooth LE Xbox pads expose a plain HID gamepad
without `IG_` and are not claimed yet.

## Windows input path and provenance

Windows exposes an `IG_00` HID collection for XGIP controllers, but it is not the raw GIP transport.
SDL's maintained Windows Xbox driver explicitly rejects that synthetic HID endpoint. BetterJoy keeps
the HID handle only for its existing device ownership and HidHide lifecycle, then reads controller
state directly from the Windows XInput API.

- SDL source explaining the Windows synthetic HID endpoint:
  https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_xboxone.c
- Microsoft "Comparison of XInput and DirectInput features" (the `IG_` device-ID convention that
  identifies XInput devices, from its `IsXInputDevice` sample):
  https://learn.microsoft.com/en-us/windows/win32/xinput/xinput-and-directinput
- Microsoft `XInputGetState` documentation:
  https://learn.microsoft.com/en-us/windows/win32/api/xinput/nf-xinput-xinputgetstate
- Microsoft `XINPUT_GAMEPAD` layout and button constants:
  https://learn.microsoft.com/en-us/windows/win32/api/xinput/ns-xinput-xinput_gamepad
- Wine's independently maintained declarations for the extended XInput state and capability
  structures used to retain Guide input and match VID/PID:
  https://github.com/wine-mirror/wine/blob/master/include/xinput.h
- Sources retrieved: 2026-09-14
- Referenced licenses: SDL zlib; Microsoft documentation terms; Wine LGPL-2.1-or-later

No source code from those projects is copied into BetterJoy. They establish the Windows transport,
native structure layout, button values, and the extended identity fields used by this independent
implementation.

## Intended mapping contract

The physical XInput state is normalized directly into BetterJoy's canonical positional codes:

- physical A/B/X/Y -> virtual Xbox A/B/X/Y positions
- View/Xbox/Menu -> Back/Guide/Start positions
- LB/LT/RB/RT and L3/R3 remain on their matching virtual positions
- D-pad and both analog sticks remain on their matching virtual positions

This direct assignment is intentional. Xbox input must never be interpreted as Nintendo labels or
translated through a physical-controller-to-physical-controller mapping. The same canonical state
feeds both virtual output and Custom binds capture.
