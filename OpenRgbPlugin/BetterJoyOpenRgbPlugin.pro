QT += core gui widgets

TEMPLATE = lib
CONFIG += plugin release c++17
CONFIG -= debug_and_release debug
TARGET = BetterJoyOpenRgbPlugin

isEmpty(OPENRGB_SOURCE_DIR) {
    error(OPENRGB_SOURCE_DIR must point to OpenRGB release_candidate_1.0rc3 source)
}

isEmpty(BETTERJOY_PLUGIN_OUTPUT_DIR) {
    BETTERJOY_PLUGIN_OUTPUT_DIR = $$PWD/bin/Release
}

DESTDIR = $$BETTERJOY_PLUGIN_OUTPUT_DIR
OBJECTS_DIR = $$PWD/obj
MOC_DIR = $$PWD/obj/moc
RCC_DIR = $$PWD/obj/rcc
UI_DIR = $$PWD/obj/ui

DEFINES += WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0601

INCLUDEPATH += \
    $$OPENRGB_SOURCE_DIR \
    $$OPENRGB_SOURCE_DIR/RGBController \
    $$OPENRGB_SOURCE_DIR/i2c_smbus

HEADERS += \
    BetterJoyBridgeClient.h \
    BetterJoyOpenRgbPlugin.h \
    RGBController_BetterJoy.h \
    $$OPENRGB_SOURCE_DIR/OpenRGBPluginInterface.h \
    $$OPENRGB_SOURCE_DIR/ResourceManagerInterface.h \
    $$OPENRGB_SOURCE_DIR/RGBController/RGBController.h

SOURCES += \
    BetterJoyBridgeClient.cpp \
    BetterJoyOpenRgbPlugin.cpp \
    RGBController_BetterJoy.cpp \
    $$OPENRGB_SOURCE_DIR/RGBController/RGBController.cpp

LIBS += -lWs2_32
