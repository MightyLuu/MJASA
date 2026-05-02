using UnityEngine;
using KSP;
using KSP.UI.Screens;
using System;
using System.IO;
using System.Collections.Generic;
using MuMech;

[KSPAddon(KSPAddon.Startup.Flight, false)]
public class MJASA : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Constants
    // -------------------------------------------------------------------------

    private const string LOG_PATH      = "GameData/MJASA/mjasa-archive.csv";
    private const string SETTINGS_PATH = "GameData/MJASA/settings.cfg";
    private const string TOOLBAR_ICON  = "MJASA/toolbar_icon";

    private static readonly string CSV_HEADER =
        "kspTime,vesselName,universalTime,missionTitle,notes," +
        "turnStartAltitude,turnStartVelocity,turnEndAltitude,turnEndAngle,turnShapeExponent," +
        "orbitAltitude,autostage,stageLimit,preStageDelay,postStageDelay," +
        "hotstaging,hotstagingLeadTime";

    // -------------------------------------------------------------------------
    // UI State
    // -------------------------------------------------------------------------

    private bool showLogger  = false;
    private bool showHistory = false;

    private Rect loggerRect  = new Rect(200, 200, 280, 180);
    private Rect historyRect = new Rect(80, 80, 1000, 460);

    private string  missionTitle = "";
    private string  notes        = "";
    private Vector2 loggerScroll = Vector2.zero;

    // -------------------------------------------------------------------------
    // Settings
    // -------------------------------------------------------------------------

    private bool autoOpenOnLaunch = false;

    // -------------------------------------------------------------------------
    // Toolbar
    // -------------------------------------------------------------------------

    private ApplicationLauncherButton toolbarButton = null;

    // -------------------------------------------------------------------------
    // History State
    // -------------------------------------------------------------------------

    private List<string[]> logEntries    = new List<string[]>();
    private Vector2        historyScroll = Vector2.zero;

    private enum Tab { Mission, TurnPath, Staging }
    private Tab activeTab = Tab.Mission;

    private int pendingDeleteIndex = -1;

    // -- Notes editing --
    private int    editingNotesIndex = -1;   // row being edited (-1 = none)
    private string editingNotesValue = "";
    private bool    justOpenedEdit     = false; // true for exactly one frame after opening
    private Vector2 editNotesScroll    = Vector2.zero;

    // -------------------------------------------------------------------------
    // Column layout per tab
    // -------------------------------------------------------------------------

    // CSV column index reference (0-based, matches CSV_HEADER order):
    //  0 kspTime            5 turnStartAltitude   10 orbitAltitude    15 hotstaging
    //  1 vesselName          6 turnStartVelocity   11 autostage         16 hotstagingLeadTime
    //  2 universalTime       7 turnEndAltitude     12 stageLimit
    //  3 missionTitle        8 turnEndAngle        13 preStageDelay
    //  4 notes               9 turnShapeExponent   14 postStageDelay

    private static readonly int[] MISSION_COLS   = { 0, 1, 3, 4 };
    private static readonly int[] TURNPATH_COLS  = { 0, 1, 3, 10, 5, 6, 7, 8, 9 };
    private static readonly int[] STAGING_COLS   = { 0, 1, 3, 12, 13, 14, 15, 16 };

    private static readonly string[] MISSION_LABELS  = { "Date", "Vessel", "Mission", "Notes" };
    private static readonly string[] TURNPATH_LABELS = { "Date", "Vessel", "Mission", "Orbit alt", "Start alt", "Start vel", "End alt", "End angle", "Shape %" };
    private static readonly string[] STAGING_LABELS  = { "Date", "Vessel", "Mission", "Stage stop", "Pre delay", "Post delay", "Hotstaging", "Lead time" };

    private static readonly int[] MISSION_WIDTHS  = { 120, 130, 130, 300 };
    private static readonly int[] TURNPATH_WIDTHS = { 120, 130, 130,  90,  90,  90,  90,  90,  90 };
    private static readonly int[] STAGING_WIDTHS  = { 120, 130, 130,  90,  90,  90,  90,  90 };

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    void Start()
    {
        LoadSettings();
        AddToolbarButton();

        if (autoOpenOnLaunch)
        {
            showLogger = true;
            toolbarButton?.SetTrue(makeCall: false);
        }
    }

    void OnDestroy()
    {
        if (toolbarButton != null)
            ApplicationLauncher.Instance.RemoveModApplication(toolbarButton);
    }

    // -------------------------------------------------------------------------
    // Toolbar
    // -------------------------------------------------------------------------

    void AddToolbarButton()
    {
        if (ApplicationLauncher.Instance == null) return;

        Texture2D icon = GameDatabase.Instance.GetTexture(TOOLBAR_ICON, false);

        toolbarButton = ApplicationLauncher.Instance.AddModApplication(
            onTrue:          OnToolbarOpen,
            onFalse:         OnToolbarClose,
            onHover:         null,
            onHoverOut:      null,
            onEnable:        null,
            onDisable:       null,
            visibleInScenes: ApplicationLauncher.AppScenes.FLIGHT,
            texture:         icon
        );
    }

    void OnToolbarOpen()  { showLogger = true; }

    void OnToolbarClose()
    {
        showLogger         = false;
        showHistory        = false;
        pendingDeleteIndex = -1;
        CancelEdit();
    }

    // -------------------------------------------------------------------------
    // Settings persistence
    // -------------------------------------------------------------------------

    void LoadSettings()
    {
        string path = KSPUtil.ApplicationRootPath + SETTINGS_PATH;
        if (!File.Exists(path)) return;

        foreach (string line in File.ReadAllLines(path))
        {
            string[] parts = line.Split('=');
            if (parts.Length != 2) continue;
            string key   = parts[0].Trim();
            string value = parts[1].Trim();
            if (key == "autoOpenOnLaunch")
                bool.TryParse(value, out autoOpenOnLaunch);
        }
    }

    void SaveSettings()
    {
        string path = KSPUtil.ApplicationRootPath + SETTINGS_PATH;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, $"autoOpenOnLaunch = {autoOpenOnLaunch.ToString().ToLower()}\n");
    }

    // -------------------------------------------------------------------------
    // GUI
    // -------------------------------------------------------------------------

    void OnGUI()
    {
        if (showLogger)
            loggerRect = GUILayout.Window(123456, loggerRect, DrawLoggerWindow, "MJASA");

        if (showHistory)
            historyRect = GUILayout.Window(123457, historyRect, DrawHistoryWindow, "MJASA — Archive");
    }

    void DrawLoggerWindow(int id)
    {
        GUILayout.BeginVertical();

        GUILayout.Label("Mission Title:");
        missionTitle = GUILayout.TextField(missionTitle);

        GUILayout.Space(5);

        GUILayout.Label("Notes:");
        loggerScroll = GUILayout.BeginScrollView(loggerScroll, GUILayout.Height(60));
        notes = GUILayout.TextArea(notes, GUILayout.ExpandHeight(true));
        GUILayout.EndScrollView();

        GUILayout.Space(5);

        if (GUILayout.Button("Archive MechJeb Settings"))
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel != null)
                LogLaunch(vessel);
        }

        GUILayout.Space(5);

        if (GUILayout.Button(showHistory ? "Hide Archive" : "Show Archive"))
        {
            showHistory = !showHistory;
            if (showHistory)
                LoadHistory();
        }

        GUILayout.Space(8);

        if (GUILayout.Button("Close"))
        {
            showLogger  = false;
            showHistory = false;
            toolbarButton?.SetFalse(makeCall: false);
        }

        GUILayout.Space(3);

        bool newAutoOpen = GUILayout.Toggle(autoOpenOnLaunch, " Auto-open on scene load");
        if (newAutoOpen != autoOpenOnLaunch)
        {
            autoOpenOnLaunch = newAutoOpen;
            SaveSettings();
        }

        GUILayout.EndVertical();
        GUI.DragWindow();
    }

    void DrawHistoryWindow(int id)
    {
        GUILayout.BeginVertical();

        // -- Tab bar --
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(activeTab == Tab.Mission,  " Mission ",  GUI.skin.button)) { if (activeTab != Tab.Mission)  { activeTab = Tab.Mission;  CancelEdit(); } }
        if (GUILayout.Toggle(activeTab == Tab.TurnPath, " Turn path", GUI.skin.button)) { if (activeTab != Tab.TurnPath) { activeTab = Tab.TurnPath; CancelEdit(); } }
        if (GUILayout.Toggle(activeTab == Tab.Staging,  " Staging ",  GUI.skin.button)) { if (activeTab != Tab.Staging)  { activeTab = Tab.Staging;  CancelEdit(); } }
        GUILayout.EndHorizontal();

        GUILayout.Space(4);

        if (logEntries.Count == 0)
        {
            GUILayout.Label("No archived entries found.");
        }
        else
        {
            GUILayout.Label($"{logEntries.Count} entr{(logEntries.Count == 1 ? "y" : "ies")} archived — newest first");
            GUILayout.Space(4);

            int[]    cols      = ActiveCols();
            string[] labels    = ActiveLabels();
            int[]    widths    = ActiveWidths();
            bool     isMission = activeTab == Tab.Mission;

            historyScroll = GUILayout.BeginScrollView(historyScroll);

            // Header row
            GUILayout.BeginHorizontal();
            for (int c = 0; c < labels.Length; c++)
                GUILayout.Label($"<b>{labels[c]}</b>", GUILayout.Width(widths[c]));
            if (isMission)
                GUILayout.Label("<b>Actions</b>", GUILayout.Width(160));
            GUILayout.EndHorizontal();

            GUILayout.Space(3);

            int deleteTarget = -1;

            for (int r = logEntries.Count - 1; r >= 0; r--)
            {
                string[] row             = logEntries[r];
                bool     isPendingDelete = pendingDeleteIndex == r;
                bool     isEditing       = editingNotesIndex == r;

                GUILayout.BeginHorizontal(isPendingDelete ? GUI.skin.box : GUIStyle.none);

                for (int c = 0; c < cols.Length; c++)
                {
                    int    colIdx = cols[c];
                    string cell   = colIdx < row.Length ? row[colIdx] : "";

                    // Notes column: show edited value live while editing this row
                    if (isMission && colIdx == 4 && isEditing)
                    {
                        GUILayout.Label(editingNotesValue, GUILayout.Width(widths[c]));
                    }
                    else
                    {
                        if (double.TryParse(cell,
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out double num))
                            cell = num.ToString("0.##");

                        GUILayout.Label(cell, GUILayout.Width(widths[c]));
                    }
                }

                // Action buttons — Mission tab only
                if (isMission)
                {
                    if (isEditing)
                    {
                        // Save / Cancel while editing
                        if (GUILayout.Button("Save", GUILayout.Width(70)))
                            CommitEdit(r, row);
                        if (GUILayout.Button("X", GUILayout.Width(36)))
                            CancelEdit();
                    }
                    else if (isPendingDelete)
                    {
                        if (GUILayout.Button("Delete", GUILayout.Width(70))) deleteTarget = r;
                        if (GUILayout.Button("X",  GUILayout.Width(36))) pendingDeleteIndex = -1;
                    }
                    else
                    {
                        if (GUILayout.Button("Edit note", GUILayout.Width(70)))
                            OpenEdit(r, row);
                        if (GUILayout.Button("Del", GUILayout.Width(36)))
                        {
                            pendingDeleteIndex = r;
                            CancelEdit();
                        }
                    }
                }

                GUILayout.EndHorizontal();

                // Inline TextField — drawn as a separate row directly below
                if (isMission && isEditing)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(8);

                    string controlName = $"mjasa_notes_{r}";

                    editNotesScroll = GUILayout.BeginScrollView(
                        editNotesScroll,
                        alwaysShowHorizontal: false,
                        alwaysShowVertical:   false,
                        horizontalScrollbar:  GUIStyle.none,
                        verticalScrollbar:    GUI.skin.verticalScrollbar,
                        GUILayout.Width(historyRect.width - 80),
                        GUILayout.Height(60));

                    // Register the control name before drawing so FocusControl can find it
                    GUI.SetNextControlName(controlName);
                    editingNotesValue = GUILayout.TextArea(
                        editingNotesValue,
                        GUILayout.Width(historyRect.width - 100),
                        GUILayout.ExpandHeight(true));

                    GUILayout.EndScrollView();

                    // Set focus exactly once, the first Repaint after opening
                    if (justOpenedEdit && Event.current.type == EventType.Repaint)
                    {
                        GUI.FocusControl(controlName);
                        justOpenedEdit = false;
                    }

                    GUILayout.EndHorizontal();
                    GUILayout.Space(4);
                }
            }

            GUILayout.EndScrollView();

            if (deleteTarget >= 0)
            {
                logEntries.RemoveAt(deleteTarget);
                pendingDeleteIndex = -1;
                CancelEdit();
                SaveAllEntries();
            }
        }

        GUILayout.Space(6);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Refresh")) { pendingDeleteIndex = -1; CancelEdit(); LoadHistory(); }
        if (GUILayout.Button("Close"))   { pendingDeleteIndex = -1; CancelEdit(); showHistory = false; }
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
        GUI.DragWindow();
    }

    // -------------------------------------------------------------------------
    // Notes edit helpers
    // -------------------------------------------------------------------------

    void OpenEdit(int rowIndex, string[] row)
    {
        editingNotesIndex = rowIndex;
        editingNotesValue = row.Length > 4 ? row[4] : "";
        justOpenedEdit    = true;
        pendingDeleteIndex = -1;
    }

    void CommitEdit(int rowIndex, string[] row)
    {
        if (row.Length > 4)
            row[4] = editingNotesValue;

        CancelEdit();
        SaveAllEntries();

        ScreenMessages.PostScreenMessage("[MJASA] Note updated.", 2f, ScreenMessageStyle.UPPER_CENTER);
        Debug.Log($"[MJASA] Note for entry {rowIndex} updated.");
    }

    void CancelEdit()
    {
        editingNotesIndex = -1;
        editingNotesValue = "";
        justOpenedEdit    = false;
        editNotesScroll   = Vector2.zero;
    }

    // -------------------------------------------------------------------------
    // Tab helpers
    // -------------------------------------------------------------------------

    int[]    ActiveCols()   => activeTab == Tab.Mission ? MISSION_COLS   : activeTab == Tab.TurnPath ? TURNPATH_COLS   : STAGING_COLS;
    string[] ActiveLabels() => activeTab == Tab.Mission ? MISSION_LABELS : activeTab == Tab.TurnPath ? TURNPATH_LABELS : STAGING_LABELS;
    int[]    ActiveWidths() => activeTab == Tab.Mission ? MISSION_WIDTHS : activeTab == Tab.TurnPath ? TURNPATH_WIDTHS : STAGING_WIDTHS;

    // -------------------------------------------------------------------------
    // Core Logging
    // -------------------------------------------------------------------------

    void LogLaunch(Vessel vessel)
    {
        try
        {
            var mjCore = vessel.FindPartModuleImplementing<MuMech.MechJebCore>();
            if (mjCore == null) { Debug.Log("[MJASA] No MechJebCore found on active vessel."); return; }

            var ascent = mjCore.GetComputerModule<MechJebModuleAscentSettings>();
            if (ascent == null) { Debug.Log("[MJASA] No Ascent Settings module found."); return; }

            var staging = mjCore.GetComputerModule<MechJebModuleStagingController>();
            if (staging == null) { Debug.Log("[MJASA] No Staging Controller module found."); return; }

            double ut = Planetarium.GetUniversalTime();

            LaunchData data = new LaunchData
            {
                kspTime            = KSPUtil.PrintDateCompact(ut, includeTime: true, includeSeconds: true),
                vesselName         = vessel.vesselName,
                universalTime      = ut,
                missionTitle       = missionTitle,
                notes              = notes,

                turnStartAltitude  = ascent.AutoTurnStartAltitude,
                turnStartVelocity  = ascent.AutoTurnStartVelocity,
                turnEndAltitude    = ascent.AutoTurnEndAltitude,
                turnEndAngle       = ascent.TurnEndAngle,
                turnShapeExponent  = ascent.TurnShapeExponent * 100,

                orbitAltitude      = ascent.DesiredOrbitAltitude,
                autostage          = ascent._autostage,

                stageLimit         = staging.AutostageLimit.ValConfig,
                preStageDelay      = staging.AutostagePreDelay.ValConfig,
                postStageDelay     = staging.AutostagePostDelay.ValConfig,
                hotstaging         = staging.HotStaging,
                hotstagingLeadTime = staging.HotStagingLeadTime.ValConfig,
            };

            AppendCsvRow(data);
            missionTitle = "";
            notes        = "";

            if (showHistory) LoadHistory();

            ScreenMessages.PostScreenMessage("[MJASA] Settings archived.", 3f, ScreenMessageStyle.UPPER_CENTER);
            Debug.Log("[MJASA] MechJeb settings archived successfully.");
        }
        catch (Exception e)
        {
            Debug.LogError("[MJASA] Error while archiving settings: " + e);
        }
    }

    // -------------------------------------------------------------------------
    // CSV Read / Write
    // -------------------------------------------------------------------------

    void AppendCsvRow(LaunchData d)
    {
        string path = KSPUtil.ApplicationRootPath + LOG_PATH;
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        bool needsHeader = !File.Exists(path);

        using (StreamWriter sw = new StreamWriter(path, append: true))
        {
            if (needsHeader)
                sw.WriteLine(CSV_HEADER);

            sw.WriteLine(string.Join(",",
                CsvCell(d.kspTime),
                CsvCell(d.vesselName),
                d.universalTime.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                CsvCell(d.missionTitle),
                CsvCell(d.notes),
                F(d.turnStartAltitude),
                F(d.turnStartVelocity),
                F(d.turnEndAltitude),
                F(d.turnEndAngle),
                F(d.turnShapeExponent),
                F(d.orbitAltitude),
                d.autostage.ToString().ToLower(),
                F(d.stageLimit),
                F(d.preStageDelay),
                F(d.postStageDelay),
                d.hotstaging.ToString().ToLower(),
                F(d.hotstagingLeadTime)
            ));
        }
    }

    void LoadHistory()
    {
        logEntries.Clear();
        string path = KSPUtil.ApplicationRootPath + LOG_PATH;

        if (!File.Exists(path)) { Debug.Log("[MJASA] No archive file found at: " + path); return; }

        // Parse the whole file at once so the CSV parser can handle
        // fields that contain escaped newlines (\n sequences)
        string raw = File.ReadAllText(path);
        List<string[]> all = ParseCsv(raw);

        // Row 0 is the header — skip it
        for (int i = 1; i < all.Count; i++)
            logEntries.Add(all[i]);

        Debug.Log($"[MJASA] Loaded {logEntries.Count} archived entries.");
    }

    void SaveAllEntries()
    {
        string path = KSPUtil.ApplicationRootPath + LOG_PATH;
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        using (StreamWriter sw = new StreamWriter(path, append: false))
        {
            sw.WriteLine(CSV_HEADER);
            foreach (string[] row in logEntries)
                sw.WriteLine(string.Join(",", System.Array.ConvertAll(row, CsvCell)));
        }

        Debug.Log($"[MJASA] Archive rewritten with {logEntries.Count} entries.");
    }

    // Parses a full CSV text (multiple rows) correctly handling quoted fields.
    // Newlines inside quoted fields are preserved as \n sequences in the stored value.
    List<string[]> ParseCsv(string text)
    {
        var rows    = new List<string[]>();
        var fields  = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        // Normalise line endings
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // Escaped quote ""
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else if (c == '\n')
                {
                    // Real newline inside a quoted field — should not happen with
                    // our encoding, but handle gracefully
                    current.Append('\n');
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(DecodeField(current.ToString()));
                    current.Clear();
                }
                else if (c == '\n')
                {
                    // End of row
                    fields.Add(DecodeField(current.ToString()));
                    current.Clear();
                    if (fields.Count > 0)
                        rows.Add(fields.ToArray());
                    fields.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
        }

        // Last field / row (file may not end with newline)
        if (current.Length > 0 || fields.Count > 0)
        {
            fields.Add(DecodeField(current.ToString()));
            if (fields.Count > 0)
                rows.Add(fields.ToArray());
        }

        return rows;
    }

    // Decodes \\n escape sequences back to real newlines for display
    string DecodeField(string value) =>
        value.Replace("\\n", "\n");

    // Encodes a value for CSV: escapes \n as \\n, wraps in quotes if needed
    string CsvCell(string value)
    {
        if (value == null) value = "";
        value = value.Replace("\r", "");          // strip \r
        value = value.Replace("\n", "\\n");       // encode newlines as \n escape
        if (value.Contains(",") || value.Contains("\"") || value.Contains("\\n"))
            value = "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    string F(double value) =>
        value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

    // -------------------------------------------------------------------------
    // Data Model
    // -------------------------------------------------------------------------

    private class LaunchData
    {
        public string kspTime;
        public string vesselName;
        public double universalTime;
        public string missionTitle;
        public string notes;

        public double turnStartAltitude;
        public double turnStartVelocity;
        public double turnEndAltitude;
        public double turnEndAngle;
        public double turnShapeExponent;

        public double orbitAltitude;
        public bool   autostage;
        public double stageLimit;
        public double preStageDelay;
        public double postStageDelay;
        public bool   hotstaging;
        public double hotstagingLeadTime;
    }
}