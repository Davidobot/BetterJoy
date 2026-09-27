namespace BetterJoyForCemu {
    // Only complete standard input reports have the layout consumed by Joycon.
    // Command acknowledgements and other HID reports must not reach input parsing.
    internal static class InputReportGuard {
        private const byte StandardInputReportId = 0x30;
        private const int FullReportLength = 49;
        private const int ButtonReportLength = 12;

        internal static bool IsInputReport(byte[] report, int length, bool hasImuLayout) {
            int minimumLength = hasImuLayout ? FullReportLength : ButtonReportLength;
            return report != null &&
                length >= minimumLength &&
                length <= report.Length &&
                report[0] == StandardInputReportId;
        }
    }
}
