using System;
using System.Collections.Generic;
using System.IO;

namespace BetterJoyForCemu {
	public static class Config { // stores dynamic configuration, including
		static readonly string path;
		static Dictionary<string, string> variables = new Dictionary<string, string>();

		const int settingsNum = 11; // currently - ProgressiveScan, StartInTray + special buttons

        static Config() {
            path = Path.Combine(AppPaths.DataDir, "settings");
        }

		public static string GetDefaultValue(string s) {
			switch (s) {
				case "ProgressiveScan":
					return "1";
				case "capture":
					return "key_" + ((int)WindowsInput.Events.KeyCode.PrintScreen);
				case "reset_mouse":
					return "joy_" + ((int)Controller.Button.STICK);
			}
			return "0";
		}

		// Helper function to count how many lines are in a file
		// https://www.dotnetperls.com/line-count
		static long CountLinesInFile(string f) {
			// Zero based count
			long count = -1;
			using (StreamReader r = new StreamReader(f)) {
				string line;
				while ((line = r.ReadLine()) != null) {
					count++;
				}
			}
			return count;
		}

		static readonly string[] DefaultKeys = { "ProgressiveScan", "StartInTray", "capture", "home", "sl_l", "sl_r", "sr_l", "sr_r", "shake", "reset_mouse", "active_gyro" };

		// Startup only - tolerant/best-effort (a malformed individual line is skipped, not
		// fatal) and destructive when the file looks stale (deletes and recreates on too few
		// lines). Safe here because nothing else is racing this file at process start. See
		// ReloadSettingsOnly for the live-reload path, which can't make either assumption.
		//
		// Calibration data no longer lives in this file - it moved to controller_mappings.xml
		// (keyed by serial, atomic temp-file+File.Replace writes, tolerant reload) because this
		// file's positional-line format had a real data-loss bug: SaveCaliData/SaveStickCaliData
		// below used to rewrite fixed line indices via File.ReadAllLines+File.WriteAllLines, not
		// atomically, so a crash or kill between those two calls could leave the file with the
		// basic-settings lines intact but the calibration lines missing entirely - which this
		// method's own line-count check tolerated as "no stick recalibration data yet" rather
		// than detecting as corruption, silently reverting every controller's calibration to
		// default for that session. Worse, the NEXT save from that same session would then
		// overwrite the file with that now-calibration-poor in-memory state, permanently losing
		// whatever was actually still on disk. ControllerMappings.LoadCalibrationInto below
		// handles a one-time migration of whatever's still salvageable from this file's old
		// calibration lines (see Config.TryTakeLegacyCalibration) so no existing user's saved
		// calibration is dropped by this move.
		public static void Init(List<KeyValuePair<string, float[]>> caliData, List<KeyValuePair<string, ushort[]>> stickCaliData, List<KeyValuePair<string, ushort[]>> stick2CaliData) {
			foreach (string s in DefaultKeys)
				variables[s] = GetDefaultValue(s);

			if (File.Exists(path)) {

				// Reset settings file if old settings
				if (CountLinesInFile(path) < settingsNum) {
					File.Delete(path);
					Init(caliData, stickCaliData, stick2CaliData);
					return;
				}

				using (StreamReader file = new StreamReader(path)) {
					string line = String.Empty;
					int lineNO = 0;
					while ((line = file.ReadLine()) != null && lineNO < settingsNum) {
						string[] vs = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
						try {
							variables[vs[0]] = vs[1];
						} catch { }
						lineNO++;
					}
				}
			} else {
				using (StreamWriter file = new StreamWriter(path)) {
					foreach (string k in variables.Keys)
						file.WriteLine(String.Format("{0} {1}", k, variables[k]));
				}
			}

			ControllerMappings.LoadCalibrationInto(caliData, stickCaliData, stick2CaliData);
		}

		// One-time migration read for calibration data still sitting in this file's old
		// position-based lines (index settingsNum = gyro/accel, settingsNum+1 = primary stick,
		// settingsNum+2 = secondary stick) - called by ControllerMappings.LoadCalibrationInto
		// only when controller_mappings.xml has no calibration data of its own yet. Blanks the
		// migrated lines out immediately after reading them, so this can only ever hand back real
		// data once; the blanked lines are the migration's own completion marker; no separate
		// flag needed, and every OTHER setting in this file is left untouched. Returns false (all
		// three lists empty) if the file doesn't exist, has nothing in those lines, or can't be
		// read right now - the caller treats that as "nothing to migrate," not an error.
		public static bool TryTakeLegacyCalibration(
				out List<KeyValuePair<string, float[]>> caliData,
				out List<KeyValuePair<string, ushort[]>> stickCaliData,
				out List<KeyValuePair<string, ushort[]>> stick2CaliData) {
			caliData = new List<KeyValuePair<string, float[]>>();
			stickCaliData = new List<KeyValuePair<string, ushort[]>>();
			stick2CaliData = new List<KeyValuePair<string, ushort[]>>();

			if (!File.Exists(path))
				return false;

			string[] txt;
			try {
				txt = File.ReadAllLines(path);
			} catch {
				return false; // torn/locked read - try again next startup, not fatal
			}

			bool foundAny = false;
			if (txt.Length > settingsNum && ParseCaliLine(txt[settingsNum], caliData))
				foundAny = true;
			if (txt.Length > settingsNum + 1 && ParseStickCaliLine(txt[settingsNum + 1], stickCaliData))
				foundAny = true;
			if (txt.Length > settingsNum + 2 && ParseStickCaliLine(txt[settingsNum + 2], stick2CaliData))
				foundAny = true;

			if (!foundAny)
				return false;

			if (txt.Length > settingsNum) txt[settingsNum] = "";
			if (txt.Length > settingsNum + 1) txt[settingsNum + 1] = "";
			if (txt.Length > settingsNum + 2) txt[settingsNum + 2] = "";
			try {
				File.WriteAllLines(path, txt);
			} catch {
				// Migration already succeeded in the out lists above and the caller persists
				// them into controller_mappings.xml regardless - a failed blank-out here just
				// means a redundant (harmless) migration attempt next startup, not data loss.
			}

			return true;
		}

		private static bool ParseCaliLine(string line, List<KeyValuePair<string, float[]>> target) {
			if (String.IsNullOrWhiteSpace(line))
				return false;
			string[] vs = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
			bool any = false;
			foreach (string entry in vs) {
				try {
					string[] caliArr = entry.Split(',');
					float[] newArr = new float[6];
					for (int j = 1; j < caliArr.Length; j++)
						newArr[j - 1] = float.Parse(caliArr[j]);
					target.Add(new KeyValuePair<string, float[]>(caliArr[0], newArr));
					any = true;
				} catch { }
			}
			return any;
		}

		private static bool ParseStickCaliLine(string line, List<KeyValuePair<string, ushort[]>> target) {
			if (String.IsNullOrWhiteSpace(line))
				return false;
			string[] vs = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
			bool any = false;
			foreach (string entry in vs) {
				try {
					string[] caliArr = entry.Split(',');
					ushort[] newArr = new ushort[6];
					for (int j = 1; j < caliArr.Length; j++)
						newArr[j - 1] = ushort.Parse(caliArr[j]);
					target.Add(new KeyValuePair<string, ushort[]>(caliArr[0], newArr));
					any = true;
				} catch { }
			}
			return any;
		}

		// Live cross-process reload (see HeadlessJoyconHost's FileSystemWatcher) - deliberately
		// far more conservative than Init(): the shared file can be observed mid-write by
		// another process at any time, and this runs on every debounced change, not once at a
		// controlled startup. Never deletes or rewrites the file - a short/malformed read here
		// could just be a transient snapshot of someone else's in-progress write, not evidence
		// the format is genuinely stale, and destroying the user's valid settings over that
		// would be far worse than just retrying on the next watcher event. Parses into a
		// temporary dictionary and only publishes it if the WHOLE read succeeds, so a torn read
		// never partially overwrites a valid in-memory snapshot with a mix of old and new
		// values. Never touches calibration data at all - that's handled entirely in-process by
		// StartCalibration and never needs a file-driven reload.
		public static void ReloadSettingsOnly() {
			if (!File.Exists(path))
				return;

			if (CountLinesInFile(path) < settingsNum)
				return; // possibly just a transient mid-write snapshot - retry next watcher event

			var newVariables = new Dictionary<string, string>();
			foreach (string s in DefaultKeys)
				newVariables[s] = GetDefaultValue(s);

			try {
				using (StreamReader file = new StreamReader(path)) {
					string line;
					int lineNO = 0;
					while ((line = file.ReadLine()) != null && lineNO < settingsNum) {
						string[] vs = line.Split();
						newVariables[vs[0]] = vs[1];
						lineNO++;
					}
				}
			} catch {
				return; // torn/locked read - keep current in-memory settings, retry next event
			}

			variables = newVariables;
		}

		public static int IntValue(string key) {
			if (!variables.ContainsKey(key)) {
				return 0;
			}
			return Int32.Parse(variables[key]);
		}

		public static string Value(string key) {
			if (!variables.ContainsKey(key)) {
				return "";
			}
			return variables[key];
		}

		public static bool SetValue(string key, string value) {
			if (!variables.ContainsKey(key))
				return false;
			variables[key] = value;
			return true;
		}

		// Delegates to controller_mappings.xml's atomic store now - see Init's comment for why
		// this file's own positional-line saves were retired (non-atomic File.ReadAllLines +
		// File.WriteAllLines could lose calibration data on a crash mid-write). Kept as a
		// same-named forwarding method rather than changing call sites - CalibrationState.cs
		// doesn't need to know where calibration actually lives on disk.
		public static void SaveCaliData(List<KeyValuePair<string, float[]>> caliData) {
			ControllerMappings.SaveGyroCalibration(caliData);
		}

		public static void SaveStickCaliData(List<KeyValuePair<string, ushort[]>> stickCaliData, List<KeyValuePair<string, ushort[]>> stick2CaliData) {
			ControllerMappings.SaveStickCalibration(stickCaliData, stick2CaliData);
		}

		public static void Save() {
			string[] txt = File.ReadAllLines(path);
			int NO = 0;
			foreach (string k in variables.Keys) {
				txt[NO] = String.Format("{0} {1}", k, variables[k]);
				NO++;
			}
			File.WriteAllLines(path, txt);
		}
	}
}
