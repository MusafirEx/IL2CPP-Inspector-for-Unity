using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace MusafirEx.IL2CPPInspector.Editor
{
    /// <summary>Local IL2CPP analysis pipeline, integrated into the Unity Editor.</summary>
    public sealed class IL2CPPInspectorWindow : EditorWindow
    {
        private const string Pref = "MusafirEx.IL2CPPInspector.";
        private string input = "";
        private string workspace = "";
        private string dumper = "";
        private string ghidra = "";
        private string targets = "CharacterMovementController,PlayerController,AIMovementController";
        private string query = "CharacterMovementController";
        private string preview = "";
        private string status = "Ready";
        private string log = "";
        private readonly object logLock = new object();
        private Vector2 scroll;
        private Task task;

        [MenuItem("Tools/IL2CPP Inspector/Analyzer")]
        public static void Open()
        {
            var window = GetWindow<IL2CPPInspectorWindow>("IL2CPP Inspector");
            window.minSize = new Vector2(670, 600);
            window.Show();
        }

        private void OnEnable()
        {
            input = EditorPrefs.GetString(Pref + "Input", "");
            workspace = EditorPrefs.GetString(Pref + "Workspace",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "IL2CPPInspector"));
            dumper = EditorPrefs.GetString(Pref + "Dumper", "");
            ghidra = EditorPrefs.GetString(Pref + "Ghidra", "");
            targets = EditorPrefs.GetString(Pref + "Targets", targets);
        }

        private void OnDisable()
        {
            Save();
        }

        private void OnInspectorUpdate()
        {
            if (task == null || !task.IsCompleted)
                return;

            if (task.IsFaulted)
            {
                status = "Failed";
                Append(task.Exception != null ? task.Exception.GetBaseException().ToString() : "Unknown error");
            }
            else
            {
                status = "Completed";
            }

            task = null;
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("IL2CPP Inspector for Unity", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Inspect a local APK/XAPK. Il2CppDumper and Ghidra are separate third-party installations. " +
                "Generated code is a reference, not the original C# source.", MessageType.Info);

            FileField("APK / XAPK", ref input, "Choose APK/XAPK", "apk,xapk");
            FolderField("Workspace", ref workspace);
            FileField("Il2CppDumper.exe", ref dumper, "Choose Il2CppDumper.exe", "exe");
            FileField("Ghidra analyzeHeadless", ref ghidra, "Choose analyzeHeadless.bat", "bat");
            targets = EditorGUILayout.TextField("Target class names", targets);
            EditorGUILayout.HelpBox("Outputs are written to the workspace OUTSIDE your Assets directory.", MessageType.None);
            EditorGUILayout.Space(8);

            using (new EditorGUI.DisabledScope(task != null))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("1. Extract", GUILayout.Height(32)))
                        StartAnalysisStage(1);
                    if (GUILayout.Button("2. Il2CppDumper", GUILayout.Height(32)))
                        StartAnalysisStage(2);
                    if (GUILayout.Button("3. Ghidra", GUILayout.Height(32)))
                        StartAnalysisStage(3);
                }

                if (GUILayout.Button("Run all stages", GUILayout.Height(28)))
                    StartAnalysisStage(0);
            }

            EditorGUILayout.LabelField("Status", status);
            EditorGUILayout.LabelField("Results", InspectorPipeline.OutputDirectory(workspace));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open Results Folder"))
                {
                    var dir = InspectorPipeline.OutputDirectory(workspace);
                    if (Directory.Exists(dir)) EditorUtility.RevealInFinder(dir);
                }
                if (GUILayout.Button("Open dump.cs"))
                {
                    var file = Path.Combine(InspectorPipeline.OutputDirectory(workspace), "dumper", "dump.cs");
                    if (File.Exists(file)) EditorUtility.OpenWithDefaultApp(file);
                }
                if (GUILayout.Button("Open Pseudocode"))
                {
                    var file = Path.Combine(InspectorPipeline.OutputDirectory(workspace), "ghidra_export", "targets_pseudocode.txt");
                    if (File.Exists(file)) EditorUtility.OpenWithDefaultApp(file);
                }
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Search dump.cs", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                query = EditorGUILayout.TextField(query);
                if (GUILayout.Button("Search", GUILayout.Width(80))) Search();
            }
            if (!string.IsNullOrEmpty(preview))
                EditorGUILayout.TextArea(preview, GUILayout.MinHeight(70), GUILayout.MaxHeight(160));

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Activity Log", EditorStyles.boldLabel);
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(155));
            EditorGUILayout.TextArea(ReadLog(), GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
            if (GUI.changed) Save();
        }

        private void StartAnalysisStage(int stage)
        {
            if (task != null) return;
            Save();
            var settings = new InspectorSettings
            {
                InputPath = input,
                Workspace = workspace,
                DumperPath = dumper,
                GhidraPath = ghidra,
                Targets = targets,
                AssetsPath = Application.dataPath,
                GhidraScriptsPath = ResolveScriptsPath()
            };
            status = "Running";
            task = Task.Run(() =>
            {
                if (stage == 0 || stage == 1) InspectorPipeline.Extract(settings, Append);
                if (stage == 0 || stage == 2) InspectorPipeline.Dump(settings, Append);
                if (stage == 0 || stage == 3) InspectorPipeline.Decompile(settings, Append);
            });
        }

        private static string ResolveScriptsPath()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForPackageName(
                "com.musafirex.il2cpp-inspector");
            return package == null ? "" : Path.Combine(package.resolvedPath, "Editor", "GhidraScripts");
        }

        private void Search()
        {
            var file = Path.Combine(InspectorPipeline.OutputDirectory(workspace), "dumper", "dump.cs");
            if (!File.Exists(file))
            {
                preview = "Run Il2CppDumper first.";
                return;
            }
            if (string.IsNullOrWhiteSpace(query))
            {
                preview = "Enter a class or method name.";
                return;
            }
            var sb = new StringBuilder();
            int found = 0;
            using (var reader = new StreamReader(file))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    sb.AppendLine(line);
                    for (int i = 0; i < 14 && !reader.EndOfStream; i++)
                        sb.AppendLine(reader.ReadLine());
                    sb.AppendLine("------");
                    if (++found == 8) break;
                }
            }
            preview = found == 0 ? "No matches." : sb.ToString();
        }

        private void Save()
        {
            EditorPrefs.SetString(Pref + "Input", input ?? "");
            EditorPrefs.SetString(Pref + "Workspace", workspace ?? "");
            EditorPrefs.SetString(Pref + "Dumper", dumper ?? "");
            EditorPrefs.SetString(Pref + "Ghidra", ghidra ?? "");
            EditorPrefs.SetString(Pref + "Targets", targets ?? "");
        }

        private void Append(string text)
        {
            lock (logLock)
            {
                log += "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + "\n";
                if (log.Length > 24000) log = log.Substring(log.Length - 24000);
            }
        }

        private string ReadLog()
        {
            lock (logLock) return log;
        }

        private static void FileField(string label, ref string path, string title, string extension)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                path = EditorGUILayout.TextField(label, path);
                if (GUILayout.Button("...", GUILayout.Width(35)))
                {
                    var selected = EditorUtility.OpenFilePanel(title,
                        File.Exists(path) ? Path.GetDirectoryName(path) : "",
                        extension.Contains(",") ? "" : extension);
                    if (!string.IsNullOrEmpty(selected)) path = selected;
                }
            }
        }

        private static void FolderField(string label, ref string path)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                path = EditorGUILayout.TextField(label, path);
                if (GUILayout.Button("...", GUILayout.Width(35)))
                {
                    var selected = EditorUtility.OpenFolderPanel(label,
                        Directory.Exists(path) ? path : "", "");
                    if (!string.IsNullOrEmpty(selected)) path = selected;
                }
            }
        }
    }
}
