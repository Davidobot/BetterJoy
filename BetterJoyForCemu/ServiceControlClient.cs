using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;

namespace BetterJoyForCemu {
    // GUI-side client for the service's control pipe (see HeadlessJoyconHost's server half and
    // ServiceControlProtocol) - used by MainForm when it has deferred hardware ownership to a
    // running service, to still show live controller status and trigger rumble test/join-split/
    // calibration against the service's real Joycon instances. All events fire from a
    // background read thread - subscribers marshal onto the UI thread themselves.
    public class ServiceControlClient : IDisposable {
        public event Action<List<ControllerRecord>> SnapshotReceived;
        public event Action<int> CalibrationStarted;
        public event Action<int> CalibrationComplete;
        public event Action<int> CalibrationFailed;
        public event Action<CalibrationStepInfo> CalibrationStep;
        public event Action<ButtonTransitionInfo> ButtonTransition;
        public event Action<LowBatteryInfo> LowBattery;
        public event Action Disconnected;

        private NamedPipeClientStream pipe;
        private BinaryWriter writer;
        private readonly object writeLock = new object();
        private bool disposed;

        public bool IsConnected => pipe != null && pipe.IsConnected;

        // Returns false if the service isn't listening within the timeout (e.g. it just
        // stopped) - caller decides what that means for it.
        public bool Connect(int timeoutMs = 3000) {
            var newPipe = new NamedPipeClientStream(".", ServiceControlIpc.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try {
                newPipe.Connect(timeoutMs);
            } catch {
                // A failed/timed-out attempt still allocated a real handle - leaked on every
                // retry otherwise (TryConnectWithRetries in MainForm calls this up to 3 times).
                newPipe.Dispose();
                return false;
            }

            pipe = newPipe;
            writer = new BinaryWriter(pipe);
            var reader = new BinaryReader(pipe);

            Task.Run(() => ReadLoop(pipe, reader));
            return true;
        }

        private void ReadLoop(NamedPipeClientStream connectedPipe, BinaryReader reader) {
            try {
                while (connectedPipe.IsConnected) {
                    var type = (ControlMessageType)reader.ReadByte();
                    switch (type) {
                        case ControlMessageType.ControllerSnapshot:
                            List<ControllerRecord> records = ServiceControlIpc.ReadSnapshot(reader);
                            SnapshotReceived?.Invoke(records);
                            break;
                        case ControlMessageType.CalibrationStarted:
                            CalibrationStarted?.Invoke(reader.ReadByte());
                            break;
                        case ControlMessageType.CalibrationComplete:
                            CalibrationComplete?.Invoke(reader.ReadByte());
                            break;
                        case ControlMessageType.CalibrationFailed:
                            CalibrationFailed?.Invoke(reader.ReadByte());
                            break;
                        case ControlMessageType.CalibrationStep:
                            CalibrationStep?.Invoke(ServiceControlIpc.ReadCalibrationStep(reader));
                            break;
                        case ControlMessageType.ButtonTransition:
                            ButtonTransition?.Invoke(ServiceControlIpc.ReadButtonTransition(reader));
                            break;
                        case ControlMessageType.LowBattery:
                            LowBattery?.Invoke(ServiceControlIpc.ReadLowBattery(reader));
                            break;
                    }
                }
            } catch {
                // service stopped/pipe dropped
            } finally {
                try { connectedPipe.Dispose(); } catch { }
                Disconnected?.Invoke();
            }
        }

        public void RequestSnapshot() => Send(w => ServiceControlIpc.WriteSimple(w, ControlMessageType.RequestSnapshot));
        public void TestRumble(int padId) => Send(w => ServiceControlIpc.WritePadIdMessage(w, ControlMessageType.TestRumble, padId));
        public void JoinOrSplit(int padId) => Send(w => ServiceControlIpc.WritePadIdMessage(w, ControlMessageType.JoinOrSplit, padId));
        public void ForceSelfPair(int padId) => Send(w => ServiceControlIpc.WritePadIdMessage(w, ControlMessageType.ForceSelfPair, padId));
        public void StartCalibration(int padId) => Send(w => ServiceControlIpc.WritePadIdMessage(w, ControlMessageType.StartCalibration, padId));
        public void CalibrationReady(int padId) => Send(w => ServiceControlIpc.WritePadIdMessage(w, ControlMessageType.CalibrationReady, padId));
        public void StartButtonCapture() => Send(w => ServiceControlIpc.WriteSimple(w, ControlMessageType.StartButtonCapture));
        public void StopButtonCapture() => Send(w => ServiceControlIpc.WriteSimple(w, ControlMessageType.StopButtonCapture));
        public void BeginBindingCapture() => Send(w => ServiceControlIpc.WriteSimple(w, ControlMessageType.BeginBindingCapture));
        public void EndBindingCapture() => Send(w => ServiceControlIpc.WriteSimple(w, ControlMessageType.EndBindingCapture));
        public void PrepareUsbAudio(int padId, int volumePercent) =>
            Send(w => ServiceControlIpc.WriteUsbAudioMessage(w, padId, volumePercent));

        private void Send(Action<BinaryWriter> write) {
            lock (writeLock) {
                if (disposed || pipe == null || !pipe.IsConnected)
                    return;

                try {
                    write(writer);
                } catch {
                    // best-effort - a mid-write disconnect just drops this one command
                }
            }
        }

        public void Dispose() {
            disposed = true;
            lock (writeLock) {
                try { writer?.Dispose(); } catch { }
                try { pipe?.Dispose(); } catch { }
                writer = null;
                pipe = null;
            }
        }
    }
}
