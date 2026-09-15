#pragma once

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <thread>

// Persistent loopback client for BetterJoy2's private OpenRGB SDK endpoint.
// OpenRGB owns this object for the lifetime of the plugin; RGBController_BetterJoy
// only borrows it, which keeps the transport alive until the controller's base
// class has finished shutting down its own update thread.
class BetterJoyBridgeClient
{
public:
    BetterJoyBridgeClient();
    ~BetterJoyBridgeClient();

    BetterJoyBridgeClient(const BetterJoyBridgeClient&) = delete;
    BetterJoyBridgeClient& operator=(const BetterJoyBridgeClient&) = delete;

    void SetColor(std::uint32_t color);
    void Stop();

private:
    void Run();

    std::atomic<bool> running_{true};
    std::atomic<std::uint32_t> color_{0};
    std::atomic<std::uint64_t> color_revision_{0};
    std::mutex wake_mutex_;
    std::condition_variable wake_condition_;
    std::thread worker_;
};
