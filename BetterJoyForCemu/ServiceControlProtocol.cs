using System;
using System.Collections.Generic;
using System.IO;

namespace BetterJoyForCemu {
    // Message types for the long-lived GUI<->service control pipe (see HeadlessJoyconHost's
    // server half and MainForm/ServiceControlClient's client half) - separate from
    // InputIpcProtocol's service<->input-helper pipe, which only ever carries keyboard/mouse
    // events. This one lets a GUI that has deferred hardware ownership to a running service
    // still show live controller status and trigger rumble test/join-split/calibration.
    public enum ControlMessageType : byte {
        // Service -> GUI
        ControllerSnapshot = 1,
        CalibrationStarted = 2,
        CalibrationComplete = 3,
        CalibrationFailed = 4,
        CalibrationStep = 5,
        ButtonTransition = 6, // a connected controller's button just went down/up - see Reassign
        LowBattery = 7, // a connected controller just crossed the low-battery threshold

        // GUI -> service
        RequestSnapshot = 10,
        TestRumble = 11,
        JoinOrSplit = 12,
        StartCalibration = 13,
        CalibrationReady = 14, // user clicked "OK" on the current step - see CalibrationStep
        StartButtonCapture = 15, // Controller Profiles dialog opened - start pushing ButtonTransition
        StopButtonCapture = 16, // dialog closed - stop
        // Double right-click override: force this solo Joycon to self-pair (vertical
        // orientation) even when other Joycons are connected and a plain JoinOrSplit would
        // otherwise search for one of them to join with instead. See MainForm's
        // HandlePossibleOrientationDoubleClick.
        ForceSelfPair = 17,
        // A specific binding box is actively listening. While this short-lived scope is open,
        // controller-derived desktop presses/clicks are suppressed at their source so the GUI's
        // global hooks observe only inputs the user actually pressed, not mapped consequences.
        BeginBindingCapture = 18,
        EndBindingCapture = 19,
        PrepareUsbAudio = 20,
    }

    public enum ControllerKind : byte {
        Left = 0,
        Right = 1,
        Pro = 2,
        Snes = 3,
        N64 = 4,
        DualSense = 5, // appended, not inserted - existing numeric values are part of the wire protocol
        DualShock4 = 6,
        Xbox = 7,
    }

    public enum ControllerBatteryStatus : byte {
        Unknown = 0,
        Discharging = 1,
        Charging = 2,
        Full = 3,
        NotCharging = 4,
    }

    // One connected controller's display-relevant state - deliberately not a live Joycon
    // reference, since the GUI process may not have one at all when the service owns the
    // hardware. Battery is the legacy 0-4 level used for tile colors/DSU; BatteryPercent -1
    // means no precise percentage is available. OtherPadId -1 means unpaired.
    public struct ControllerRecord {
        public byte PadId;
        public ControllerKind Kind;
        public sbyte Battery;
        public sbyte BatteryPercent;
        public ControllerBatteryStatus BatteryStatus;
        public sbyte OtherPadId;
        public string ProfileId;
        public string ProfileName;
        public long ConnectionSequence;
        // True for a solo Joycon self-paired into vertical orientation (Joycon.other == itself -
        // OtherPadId stays -1 for this case, same as plain solo, since there's no separate
        // partner record to pair against). Lets the GUI pick the correct slot icon (see
        // MainForm.IconFor) without a live Joycon reference to check .other on directly.
        public bool IsVertical;
        public bool IsUsb;
        public string AudioEndpointNameHint;

        public void WriteTo(BinaryWriter writer) {
            writer.Write(PadId);
            writer.Write((byte)Kind);
            writer.Write(Battery);
            writer.Write(OtherPadId);
            writer.Write(ProfileId ?? String.Empty);
            writer.Write(ProfileName ?? String.Empty);
            writer.Write(ConnectionSequence);
            writer.Write(IsVertical);
            writer.Write(BatteryPercent);
            writer.Write((byte)BatteryStatus);
            writer.Write(IsUsb);
            writer.Write(AudioEndpointNameHint ?? String.Empty);
        }

        public static ControllerRecord ReadFrom(BinaryReader reader) {
            return new ControllerRecord {
                PadId = reader.ReadByte(),
                Kind = (ControllerKind)reader.ReadByte(),
                Battery = reader.ReadSByte(),
                OtherPadId = reader.ReadSByte(),
                ProfileId = reader.ReadString(),
                ProfileName = reader.ReadString(),
                ConnectionSequence = reader.ReadInt64(),
                IsVertical = reader.ReadBoolean(),
                BatteryPercent = reader.ReadSByte(),
                BatteryStatus = (ControllerBatteryStatus)reader.ReadByte(),
                IsUsb = reader.ReadBoolean(),
                AudioEndpointNameHint = reader.ReadString(),
            };
        }
    }

    // Start/Done: no time limit at all - the GUI shows a button and the service just waits
    // (CalibrationReady) for the user to click it, however long that takes. Countdown is the one
    // exception (gyro's actual hold-still measurement): the user can't be clicking anything on
    // the PC while proving the controller is sitting untouched, so that phase runs on a fixed
    // server-side timer instead, and Count carries its remaining whole seconds purely for
    // display - the GUI ticks its own once-per-second cosmetic countdown from it rather than the
    // service pushing a message every second.
    public enum CalibStepUiMode : byte { Start, Done, Countdown }

    // One step/phase transition of a calibration run in progress - see HeadlessJoyconHost's
    // StartCalibration (the source of truth for step names/instruction text) and MainForm's
    // CalibrationDialog (the passive renderer of whatever arrives here).
    public struct CalibrationStepInfo {
        public int PadId;
        public int StepNumber;
        public int TotalSteps;
        public string StepName;
        public string Instruction;
        public CalibStepUiMode UiMode;
        public int Count; // only meaningful when UiMode == Countdown
    }

    // A connected controller's button just went down or up - pushed only while a GUI has
    // requested it (StartButtonCapture), so it costs nothing the rest of the time. ProfileId is
    // the logical controller that produced it, allowing Controller Profiles to ignore presses
    // from a different physical controller while editing the selected profile.
    public struct ButtonTransitionInfo {
        public string ProfileId;
        public int ButtonIndex;
        public bool IsDown;
    }

    // Pushed from Controller.BatteryChanged (via HeadlessJoyconHost.NotifyLowBattery) whenever a
    // connected, non-USB controller's battery drops to its lowest reported level - Kind is
    // carried alongside PadId since the GUI needs it to reconstruct the same kind-labeled balloon
    // text the old local-mode NotifyLowBattery used to show directly.
    public struct LowBatteryInfo {
        public byte PadId;
        public ControllerKind Kind;
    }

    public static class ServiceControlIpc {
        // Fixed and well-known (unlike the per-session input-helper pipe) - the GUI needs to
        // find this without being told a name, since it isn't the one that launched the service.
        public const string PipeName = "BetterJoy2ServiceControl";

        public static void WriteSnapshot(BinaryWriter writer, List<ControllerRecord> records) {
            writer.Write((byte)ControlMessageType.ControllerSnapshot);
            writer.Write((byte)records.Count);
            foreach (ControllerRecord record in records)
                record.WriteTo(writer);
            writer.Flush();
        }

        public static List<ControllerRecord> ReadSnapshot(BinaryReader reader) {
            byte count = reader.ReadByte();
            var records = new List<ControllerRecord>(count);
            for (int i = 0; i < count; i++)
                records.Add(ControllerRecord.ReadFrom(reader));
            return records;
        }

        public static void WritePadIdMessage(BinaryWriter writer, ControlMessageType type, int padId) {
            writer.Write((byte)type);
            writer.Write((byte)padId);
            writer.Flush();
        }

        public static void WriteUsbAudioMessage(BinaryWriter writer, int padId, int volumePercent) =>
            WritePadIdVolumeMessage(writer, ControlMessageType.PrepareUsbAudio, padId, volumePercent);

        private static void WritePadIdVolumeMessage(
            BinaryWriter writer, ControlMessageType type, int padId, int volumePercent) {
            writer.Write((byte)type);
            writer.Write((byte)padId);
            writer.Write((byte)Math.Max(0, Math.Min(100, volumePercent)));
            writer.Flush();
        }

        public static void WriteSimple(BinaryWriter writer, ControlMessageType type) {
            writer.Write((byte)type);
            writer.Flush();
        }

        public static void WriteCalibrationStep(BinaryWriter writer, CalibrationStepInfo step) {
            writer.Write((byte)ControlMessageType.CalibrationStep);
            writer.Write((byte)step.PadId);
            writer.Write((byte)step.StepNumber);
            writer.Write((byte)step.TotalSteps);
            writer.Write(step.StepName);
            writer.Write(step.Instruction);
            writer.Write((byte)step.UiMode);
            writer.Write((byte)step.Count);
            writer.Flush();
        }

        public static CalibrationStepInfo ReadCalibrationStep(BinaryReader reader) {
            return new CalibrationStepInfo {
                PadId = reader.ReadByte(),
                StepNumber = reader.ReadByte(),
                TotalSteps = reader.ReadByte(),
                StepName = reader.ReadString(),
                Instruction = reader.ReadString(),
                UiMode = (CalibStepUiMode)reader.ReadByte(),
                Count = reader.ReadByte(),
            };
        }

        public static void WriteButtonTransition(BinaryWriter writer, string profileId, int buttonIndex, bool isDown) {
            writer.Write((byte)ControlMessageType.ButtonTransition);
            writer.Write(profileId ?? String.Empty);
            writer.Write((byte)buttonIndex);
            writer.Write(isDown);
            writer.Flush();
        }

        public static ButtonTransitionInfo ReadButtonTransition(BinaryReader reader) {
            return new ButtonTransitionInfo {
                ProfileId = reader.ReadString(),
                ButtonIndex = reader.ReadByte(),
                IsDown = reader.ReadBoolean(),
            };
        }

        public static void WriteLowBattery(BinaryWriter writer, byte padId, ControllerKind kind) {
            writer.Write((byte)ControlMessageType.LowBattery);
            writer.Write(padId);
            writer.Write((byte)kind);
            writer.Flush();
        }

        public static LowBatteryInfo ReadLowBattery(BinaryReader reader) {
            return new LowBatteryInfo {
                PadId = reader.ReadByte(),
                Kind = (ControllerKind)reader.ReadByte(),
            };
        }
    }
}
