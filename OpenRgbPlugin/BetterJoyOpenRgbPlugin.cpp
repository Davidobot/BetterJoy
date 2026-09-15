#include "BetterJoyOpenRgbPlugin.h"

#include "BetterJoyBridgeClient.h"
#include "RGBController_BetterJoy.h"

OpenRGBPluginInfo BetterJoyOpenRgbPlugin::GetPluginInfo()
{
    OpenRGBPluginInfo info{};
    info.Name = "BetterJoy2 Bridge";
    info.Description = "Exports BetterJoy2 controller lighting through OpenRGB";
    info.Version = "1.0.0";
    info.Commit = "BetterJoy2";
    info.URL = "https://github.com/Geofferey/BetterJoy";
    // This bridge has no configuration UI of its own. rc3's AddPlugin callback blindly wraps a
    // null GetWidget() result for every valid tab location, so deliberately use the documented
    // invalid-location path: the plugin still loads and appears in OpenRGB's Plugins list, but
    // no empty tab (and no null-widget crash) is created.
    info.Location = static_cast<unsigned int>(-1);
    info.Label = "";
    info.TabIconString = "";
    return info;
}

unsigned int BetterJoyOpenRgbPlugin::GetPluginAPIVersion()
{
    return OPENRGB_PLUGIN_API_VERSION;
}

void BetterJoyOpenRgbPlugin::Load(ResourceManagerInterface* resource_manager)
{
    if(resource_manager_ != nullptr)
    {
        return;
    }

    resource_manager_ = resource_manager;
    bridge_ = new BetterJoyBridgeClient();
    controller_ = new RGBController_BetterJoy(bridge_);
    resource_manager_->RegisterRGBController(controller_);
}

QWidget* BetterJoyOpenRgbPlugin::GetWidget()
{
    return nullptr;
}

QMenu* BetterJoyOpenRgbPlugin::GetTrayMenu()
{
    return nullptr;
}

void BetterJoyOpenRgbPlugin::Unload()
{
    if(resource_manager_ != nullptr && controller_ != nullptr)
    {
        resource_manager_->UnregisterRGBController(controller_);
    }

    // The bridge outlives the RGBController base class's internal update thread.
    delete controller_;
    controller_ = nullptr;
    delete bridge_;
    bridge_ = nullptr;
    resource_manager_ = nullptr;
}
