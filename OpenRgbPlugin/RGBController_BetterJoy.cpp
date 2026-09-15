#include "RGBController_BetterJoy.h"

#include "BetterJoyBridgeClient.h"

RGBController_BetterJoy::RGBController_BetterJoy(BetterJoyBridgeClient* bridge)
    : bridge_(bridge)
{
    name = "BetterJoy2";
    vendor = "BetterJoy Contributors";
    description = "BetterJoy2 controller lighting bridge";
    version = "1";
    serial = "BetterJoy2";
    location = "127.0.0.1:6743";
    type = DEVICE_TYPE_GAMEPAD;
    flags = CONTROLLER_FLAG_VIRTUAL;

    mode direct;
    direct.name = "Direct";
    direct.value = 0;
    direct.flags = MODE_FLAG_HAS_PER_LED_COLOR;
    direct.color_mode = MODE_COLORS_PER_LED;
    modes.push_back(direct);

    SetupZones();
}

void RGBController_BetterJoy::SetupZones()
{
    zone lightbar;
    lightbar.name = "Lightbar";
    lightbar.type = ZONE_TYPE_SINGLE;
    lightbar.leds_min = 1;
    lightbar.leds_max = 1;
    lightbar.leds_count = 1;
    lightbar.matrix_map = nullptr;
    zones.push_back(lightbar);

    leds.resize(1);
    leds[0].name = "Lightbar";
    leds[0].value = 0;
    SetupColors();
}

void RGBController_BetterJoy::ResizeZone(int, int)
{
}

void RGBController_BetterJoy::DeviceUpdateLEDs()
{
    if(bridge_ != nullptr && !colors.empty())
    {
        bridge_->SetColor(colors[0]);
    }
}

void RGBController_BetterJoy::UpdateZoneLEDs(int)
{
    DeviceUpdateLEDs();
}

void RGBController_BetterJoy::UpdateSingleLED(int)
{
    DeviceUpdateLEDs();
}

void RGBController_BetterJoy::DeviceUpdateMode()
{
    DeviceUpdateLEDs();
}

void RGBController_BetterJoy::SetCustomMode()
{
}
