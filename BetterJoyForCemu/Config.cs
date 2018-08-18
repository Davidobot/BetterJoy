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

        public static void Init() {
            variables["ProgressiveScan"] = true;
            
            if (File.Exists(PATH)) {
                using (StreamReader file = new StreamReader(PATH)) {
                    string line = String.Empty;
                    while ((line = file.ReadLine()) != null) {
                        string[] vs = line.Split();
                        try {
                            variables[vs[0]] = Boolean.Parse(vs[1]);
                        } catch { }
                    }
                }
            } else {
                using (StreamWriter file = new StreamWriter(PATH)) {
                    foreach (string k in variables.Keys)
                        file.WriteLine(String.Format("{0} {1}", k, variables[k]));
                }
            }
        }

        public static bool Value(string key) {
            if (!variables.TryGetValue(key, out bool temp)) {
                return false;
            }
            return variables[key];
        }

        public static void Save(string key, bool value) {
            variables[key] = value;

            using (StreamWriter file = new StreamWriter(PATH, false)) {
                foreach (string k in variables.Keys)
                    file.WriteLine(String.Format("{0} {1}", k, variables[k]));
            }
        }
    }
}
