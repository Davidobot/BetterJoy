using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BetterJoyForCemu {
	public static class Config { // stores dynamic configuration, including
		const string PATH = "settings";
		static Dictionary<string, bool> variables = new Dictionary<string, bool>();

		const int settingsNum = 2; // currently - ProgressiveScan, StartInTray

		public static void Init(List<KeyValuePair<string, float[]>> caliData) {
			variables["ProgressiveScan"] = true;
			variables["StartInTray"] = false;

			if (File.Exists(PATH)) {
				using (StreamReader file = new StreamReader(PATH)) {
					string line = String.Empty;
					int lineNO = 0;
					while ((line = file.ReadLine()) != null) {
						string[] vs = line.Split();
						try {
							if (lineNO < settingsNum) { // load in basic settings
								variables[vs[0]] = Boolean.Parse(vs[1]);
							} else { // load in calibration presets
								caliData.Clear();
								for (int i = 0; i < vs.Length; i++) {
									string[] caliArr = vs[i].Split(',');
									float[] newArr = new float[6];
									for (int j = 1; j < caliArr.Length; j++) {
										newArr[j - 1] = float.Parse(caliArr[j]);
									}
									caliData.Add(new KeyValuePair<string, float[]>(
										caliArr[0],
										newArr
									));
								}
							}
						} catch { }
						lineNO++;
					}
				}
			} else {
				using (StreamWriter file = new StreamWriter(PATH)) {
					foreach (string k in variables.Keys)
						file.WriteLine(String.Format("{0} {1}", k, variables[k]));
					string caliStr = "";
					for (int i = 0; i < caliData.Count; i++) {
						string space = " ";
						if (i == 0) space = "";
						caliStr += space + caliData[i].Key + "," + String.Join(",", caliData[i].Value);
					}
					file.WriteLine(caliStr);
				}
			}
		}

		public static bool Value(string key) {
			if (!variables.ContainsKey("ProgressiveScan") && !variables.ContainsKey("StartInTray")) {
				return false;
			}
			return variables[key];
		}

		public static void SaveCaliData(List<KeyValuePair<string, float[]>> caliData) {
			string[] txt = File.ReadAllLines(PATH);
			if (txt.Length < settingsNum + 1) // no custom calibrations yet
				Array.Resize(ref txt, txt.Length + 1);

			string caliStr = "";
			for (int i = 0; i < caliData.Count; i++) {
				string space = " ";
				if (i == 0) space = "";
				caliStr += space + caliData[i].Key + "," + String.Join(",", caliData[i].Value);
			}
			txt[2] = caliStr;
			File.WriteAllLines(PATH, txt);
		}

		public static void Save(string key, bool value) {
			variables[key] = value;
			string[] txt = File.ReadAllLines(PATH);
			int NO = 0;
			foreach (string k in variables.Keys) {
				txt[NO] = String.Format("{0} {1}", k, variables[k]);
				NO++;
			}
			File.WriteAllLines(PATH, txt);
		}
	}
}