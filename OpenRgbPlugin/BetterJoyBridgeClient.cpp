#include "BetterJoyBridgeClient.h"

#include <WinSock2.h>
#include <WS2tcpip.h>

#include <array>
#include <chrono>
#include <cstring>
#include <vector>

namespace
{
    constexpr std::uint16_t kBetterJoyPort = 6743;
    constexpr std::uint32_t kProtocolVersion = 1;
    constexpr std::uint32_t kPacketRequestProtocolVersion = 40;
    constexpr std::uint32_t kPacketUpdateLeds = 1050;

    void WriteU16(std::uint8_t* destination, std::uint16_t value)
    {
        std::memcpy(destination, &value, sizeof(value));
    }

    void WriteU32(std::uint8_t* destination, std::uint32_t value)
    {
        std::memcpy(destination, &value, sizeof(value));
    }

    bool SendAll(SOCKET socket, const std::uint8_t* data, std::size_t size)
    {
        std::size_t sent = 0;
        while(sent < size)
        {
            const int result = send(socket,
                                    reinterpret_cast<const char*>(data + sent),
                                    static_cast<int>(size - sent), 0);
            if(result == SOCKET_ERROR || result == 0)
            {
                return false;
            }
            sent += static_cast<std::size_t>(result);
        }
        return true;
    }

    bool ReceiveAll(SOCKET socket, std::uint8_t* data, std::size_t size)
    {
        std::size_t received = 0;
        while(received < size)
        {
            const int result = recv(socket,
                                    reinterpret_cast<char*>(data + received),
                                    static_cast<int>(size - received), 0);
            if(result == SOCKET_ERROR || result == 0)
            {
                return false;
            }
            received += static_cast<std::size_t>(result);
        }
        return true;
    }

    bool SendPacket(SOCKET socket, std::uint32_t device_id, std::uint32_t packet_id,
                    const std::uint8_t* payload, std::uint32_t payload_size)
    {
        std::array<std::uint8_t, 16> header{};
        header[0] = 'O';
        header[1] = 'R';
        header[2] = 'G';
        header[3] = 'B';
        WriteU32(header.data() + 4, device_id);
        WriteU32(header.data() + 8, packet_id);
        WriteU32(header.data() + 12, payload_size);

        return SendAll(socket, header.data(), header.size()) &&
               (payload_size == 0 || SendAll(socket, payload, payload_size));
    }

    SOCKET Connect()
    {
        SOCKET socket = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if(socket == INVALID_SOCKET)
        {
            return INVALID_SOCKET;
        }

        DWORD timeout_ms = 1500;
        setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO,
                   reinterpret_cast<const char*>(&timeout_ms), sizeof(timeout_ms));
        setsockopt(socket, SOL_SOCKET, SO_SNDTIMEO,
                   reinterpret_cast<const char*>(&timeout_ms), sizeof(timeout_ms));

        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_port = htons(kBetterJoyPort);
        InetPtonA(AF_INET, "127.0.0.1", &address.sin_addr);

        if(connect(socket, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) ==
           SOCKET_ERROR)
        {
            closesocket(socket);
            return INVALID_SOCKET;
        }

        return socket;
    }

    bool NegotiateProtocol(SOCKET socket)
    {
        std::array<std::uint8_t, 4> request{};
        WriteU32(request.data(), kProtocolVersion);
        if(!SendPacket(socket, 0, kPacketRequestProtocolVersion,
                       request.data(), static_cast<std::uint32_t>(request.size())))
        {
            return false;
        }

        std::array<std::uint8_t, 16> header{};
        if(!ReceiveAll(socket, header.data(), header.size()))
        {
            return false;
        }
        if(header[0] != 'O' || header[1] != 'R' || header[2] != 'G' || header[3] != 'B')
        {
            return false;
        }

        std::uint32_t packet_id = 0;
        std::uint32_t payload_size = 0;
        std::memcpy(&packet_id, header.data() + 8, sizeof(packet_id));
        std::memcpy(&payload_size, header.data() + 12, sizeof(payload_size));
        if(packet_id != kPacketRequestProtocolVersion || payload_size != sizeof(std::uint32_t))
        {
            return false;
        }

        std::array<std::uint8_t, 4> response{};
        return ReceiveAll(socket, response.data(), response.size());
    }

    bool SendColor(SOCKET socket, std::uint32_t color)
    {
        // RGBController::GetColorDescription wire format: its own total size,
        // one 16-bit color count, then OpenRGB's packed 0x00BBGGRR value.
        std::array<std::uint8_t, 10> payload{};
        WriteU32(payload.data(), static_cast<std::uint32_t>(payload.size()));
        WriteU16(payload.data() + 4, 1);
        WriteU32(payload.data() + 6, color & 0x00FFFFFFu);
        return SendPacket(socket, 0, kPacketUpdateLeds,
                          payload.data(), static_cast<std::uint32_t>(payload.size()));
    }
}

BetterJoyBridgeClient::BetterJoyBridgeClient()
{
    WSADATA winsock_data{};
    if(WSAStartup(MAKEWORD(2, 2), &winsock_data) != 0)
    {
        running_.store(false);
        return;
    }
    worker_ = std::thread(&BetterJoyBridgeClient::Run, this);
}

BetterJoyBridgeClient::~BetterJoyBridgeClient()
{
    Stop();
    WSACleanup();
}

void BetterJoyBridgeClient::SetColor(std::uint32_t color)
{
    color_.store(color & 0x00FFFFFFu);
    color_revision_.fetch_add(1);
    wake_condition_.notify_one();
}

void BetterJoyBridgeClient::Stop()
{
    if(!running_.exchange(false))
    {
        return;
    }
    wake_condition_.notify_one();
    if(worker_.joinable())
    {
        worker_.join();
    }
}

void BetterJoyBridgeClient::Run()
{
    using namespace std::chrono_literals;

    SOCKET socket = INVALID_SOCKET;
    std::uint64_t sent_revision = 0;

    while(running_.load())
    {
        if(socket == INVALID_SOCKET)
        {
            socket = Connect();
            if(socket == INVALID_SOCKET || !NegotiateProtocol(socket))
            {
                if(socket != INVALID_SOCKET)
                {
                    closesocket(socket);
                    socket = INVALID_SOCKET;
                }
                std::unique_lock<std::mutex> lock(wake_mutex_);
                wake_condition_.wait_for(lock, 1s, [this] { return !running_.load(); });
                continue;
            }

            // Re-send the current OpenRGB color after every reconnect. Revision zero means
            // OpenRGB has not issued a color yet, so connecting alone never paints black.
            sent_revision = 0;
        }

        const std::uint64_t current_revision = color_revision_.load();
        if(current_revision != 0 && current_revision != sent_revision)
        {
            if(!SendColor(socket, color_.load()))
            {
                closesocket(socket);
                socket = INVALID_SOCKET;
                continue;
            }
            sent_revision = current_revision;
        }

        std::unique_lock<std::mutex> lock(wake_mutex_);
        wake_condition_.wait_for(lock, 1s, [this, sent_revision]
        {
            return !running_.load() || color_revision_.load() != sent_revision;
        });

        // A graceful BetterJoy shutdown makes the socket readable with zero bytes. Probe only
        // after the wait so the idle path stays cheap and reconnects without needing a new color.
        if(socket != INVALID_SOCKET)
        {
            fd_set readable;
            FD_ZERO(&readable);
            FD_SET(socket, &readable);
            timeval timeout{};
            if(select(0, &readable, nullptr, nullptr, &timeout) > 0)
            {
                char byte = 0;
                if(recv(socket, &byte, 1, MSG_PEEK) <= 0)
                {
                    closesocket(socket);
                    socket = INVALID_SOCKET;
                }
            }
        }
    }

    if(socket != INVALID_SOCKET)
    {
        closesocket(socket);
    }
}
