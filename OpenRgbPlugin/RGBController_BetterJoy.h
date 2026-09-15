#pragma once

#include "RGBController.h"

class BetterJoyBridgeClient;

class RGBController_BetterJoy final : public RGBController
{
public:
    explicit RGBController_BetterJoy(BetterJoyBridgeClient* bridge);

    void SetupZones() override;
    void ResizeZone(int zone, int new_size) override;
    void DeviceUpdateLEDs() override;
    void UpdateZoneLEDs(int zone) override;
    void UpdateSingleLED(int led) override;
    void DeviceUpdateMode() override;
    void SetCustomMode() override;

private:
    BetterJoyBridgeClient* bridge_;
};
