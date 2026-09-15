#pragma once

#include <QObject>

#include "OpenRGBPluginInterface.h"

class BetterJoyBridgeClient;
class RGBController_BetterJoy;

class BetterJoyOpenRgbPlugin final : public QObject, public OpenRGBPluginInterface
{
    Q_OBJECT
    Q_PLUGIN_METADATA(IID OpenRGBPluginInterface_IID)
    Q_INTERFACES(OpenRGBPluginInterface)

public:
    OpenRGBPluginInfo GetPluginInfo() override;
    unsigned int GetPluginAPIVersion() override;
    void Load(ResourceManagerInterface* resource_manager) override;
    QWidget* GetWidget() override;
    QMenu* GetTrayMenu() override;
    void Unload() override;

private:
    ResourceManagerInterface* resource_manager_ = nullptr;
    BetterJoyBridgeClient* bridge_ = nullptr;
    RGBController_BetterJoy* controller_ = nullptr;
};
